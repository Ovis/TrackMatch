using NAudio.CoreAudioApi;
using NAudio.SoundFile;
using NAudio.Wave;
using TrackMatch.Core.Playback;

namespace TrackMatch.App.Playback;

/// <summary>
/// libsndfileでA/BをFloat PCMへDecodeし、1本のWASAPI streamとして同期再生する。
/// </summary>
public sealed class NAudioSynchronizedPlaybackService : ISynchronizedPlaybackService
{
    private readonly object _gate = new();
    private readonly MMDeviceEnumerator _deviceEnumerator;
    private readonly MMDeviceNotificationClient _deviceNotifications;
    private string? _pathA;
    private string? _pathB;
    private TimeSpan _durationA;
    private TimeSpan _durationB;
    private PlaybackOffsets _offsets = new(TimeSpan.Zero, TimeSpan.Zero);
    private TimeSpan _position;
    private SynchronizedPlaybackMode _mode = SynchronizedPlaybackMode.StereoOverlay;
    private float _volumeA = 1f;
    private float _volumeB = 1f;
    private PlaybackStateKind _state;
    private WasapiPlayer? _player;
    private SoundFileReader? _readerA;
    private SoundFileReader? _readerB;
    private FileStream? _streamA;
    private FileStream? _streamB;
    private SynchronizedPairSampleProvider? _pairProvider;
    private TimeSpan _pipelineStartPosition;
    private bool _disposed;

    public NAudioSynchronizedPlaybackService()
    {
        _deviceEnumerator = new MMDeviceEnumerator();
        _deviceNotifications = _deviceEnumerator.CreateNotificationClient();
        _deviceNotifications.DefaultDeviceChanged += OnDefaultDeviceChanged;
    }

    public event EventHandler? PlaybackEnded;

    public event Action<string>? PlaybackFailed;

    public TimeSpan Position
    {
        get
        {
            lock (_gate)
            {
                return GetCurrentPositionUnsafe();
            }
        }
    }

    public TimeSpan Duration
    {
        get
        {
            lock (_gate)
            {
                return GetDurationUnsafe();
            }
        }
    }

    public PlaybackOffsets Offsets
    {
        get
        {
            lock (_gate)
            {
                return _offsets;
            }
        }
    }

    public SynchronizedPlaybackMode Mode
    {
        get
        {
            lock (_gate)
            {
                return _mode;
            }
        }
        set
        {
            lock (_gate)
            {
                ThrowIfDisposed();
                _mode = value;
                if (_pairProvider is not null)
                {
                    _pairProvider.Mode = value;
                }
            }
        }
    }

    public float VolumeA
    {
        get
        {
            lock (_gate)
            {
                return _volumeA;
            }
        }
        set
        {
            ValidateVolume(value, nameof(value));
            lock (_gate)
            {
                ThrowIfDisposed();
                _volumeA = value;
                if (_pairProvider is not null)
                {
                    _pairProvider.VolumeA = value;
                }
            }
        }
    }

    public float VolumeB
    {
        get
        {
            lock (_gate)
            {
                return _volumeB;
            }
        }
        set
        {
            ValidateVolume(value, nameof(value));
            lock (_gate)
            {
                ThrowIfDisposed();
                _volumeB = value;
                if (_pairProvider is not null)
                {
                    _pairProvider.VolumeB = value;
                }
            }
        }
    }

    public bool IsPlaying
    {
        get
        {
            lock (_gate)
            {
                return _state == PlaybackStateKind.Playing;
            }
        }
    }

    public bool IsPaused
    {
        get
        {
            lock (_gate)
            {
                return _state == PlaybackStateKind.Paused;
            }
        }
    }

    /// <inheritdoc />
    public void Load(string pathA, string pathB, TimeSpan bestOffset)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(pathA);
        ArgumentException.ThrowIfNullOrWhiteSpace(pathB);

        lock (_gate)
        {
            ThrowIfDisposed();
            DisposePipelineUnsafe();

            // Candidate切替時は前Candidateの一時状態を持ち越さない。
            _pathA = Path.GetFullPath(pathA);
            _pathB = Path.GetFullPath(pathB);
            using (var streamA = OpenAudioStream(_pathA))
            using (var streamB = OpenAudioStream(_pathB))
            using (var readerA = new SoundFileReader(streamA))
            using (var readerB = new SoundFileReader(streamB))
            {
                _durationA = readerA.TotalTime;
                _durationB = readerB.TotalTime;
            }

            _offsets = PlaybackOffsets.Normalize(TimeSpan.Zero, bestOffset);
            _position = TimeSpan.Zero;
            _volumeA = 1f;
            _volumeB = 1f;
            _state = PlaybackStateKind.Stopped;
        }
    }

    /// <inheritdoc />
    public void Play()
    {
        lock (_gate)
        {
            ThrowIfDisposed();
            EnsureLoaded();

            var duration = GetDurationUnsafe();
            if (_position >= duration)
            {
                // Natural End後のPlayだけは先頭へ戻す。Pause/Seek等では位置を勝手に変更しない。
                _position = TimeSpan.Zero;
            }

            try
            {
                DisposePipelineUnsafe();
                BuildPipelineUnsafe(_position);
                _state = PlaybackStateKind.Playing;
                _player!.Play();
            }
            catch (Exception exception) when (exception is IOException or InvalidOperationException or NotSupportedException or DllNotFoundException or SoundFileException)
            {
                DisposePipelineUnsafe();
                _state = PlaybackStateKind.Stopped;
                PlaybackFailed?.Invoke(exception.Message);
            }
        }
    }

    /// <inheritdoc />
    public void Pause()
    {
        lock (_gate)
        {
            ThrowIfDisposed();
            if (_state != PlaybackStateKind.Playing)
            {
                return;
            }

            _position = GetCurrentPositionUnsafe();
            DisposePipelineUnsafe();
            _state = PlaybackStateKind.Paused;
        }
    }

    /// <inheritdoc />
    public void Stop()
    {
        lock (_gate)
        {
            ThrowIfDisposed();
            DisposePipelineUnsafe();
            _position = TimeSpan.Zero;
            _state = PlaybackStateKind.Stopped;
        }
    }

    /// <inheritdoc />
    public void Seek(TimeSpan position)
    {
        lock (_gate)
        {
            ThrowIfDisposed();
            EnsureLoaded();
            var wasPlaying = _state == PlaybackStateKind.Playing;
            _position = ClampPosition(position, GetDurationUnsafe());

            if (wasPlaying)
            {
                DisposePipelineUnsafe();
                BuildPipelineUnsafe(_position);
                _state = PlaybackStateKind.Playing;
                _player!.Play();
            }
        }
    }

    /// <inheritdoc />
    public void SetOffsets(TimeSpan offsetA, TimeSpan offsetB)
    {
        lock (_gate)
        {
            ThrowIfDisposed();
            EnsureLoaded();
            var currentPosition = GetCurrentPositionUnsafe();
            var wasPlaying = _state == PlaybackStateKind.Playing;
            _offsets = PlaybackOffsets.Normalize(offsetA, offsetB);
            _position = ClampPosition(currentPosition, GetDurationUnsafe());

            // Offset変更ではCommon Positionを維持し、Source位置だけを新Offsetから再計算する。
            if (wasPlaying)
            {
                DisposePipelineUnsafe();
                BuildPipelineUnsafe(_position);
                _state = PlaybackStateKind.Playing;
                _player!.Play();
            }
        }
    }

    /// <inheritdoc />
    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            _deviceNotifications.DefaultDeviceChanged -= OnDefaultDeviceChanged;
            DisposePipelineUnsafe();
            _deviceNotifications.Dispose();
            _deviceEnumerator.Dispose();
        }
    }

    private void BuildPipelineUnsafe(TimeSpan commonPosition)
    {
        using var device = _deviceEnumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
        var player = new WasapiPlayerBuilder()
            .WithDevice(device)
            .WithSharedMode()
            .WithEventSync()
            .Build();
        var sampleRate = player.DeviceMixFormat.SampleRate;

        FileStream? streamA = null;
        FileStream? streamB = null;
        SoundFileReader? readerA = null;
        SoundFileReader? readerB = null;
        try
        {
            streamA = OpenAudioStream(_pathA!);
            streamB = OpenAudioStream(_pathB!);
            readerA = new SoundFileReader(streamA);
            readerB = new SoundFileReader(streamB);

            var commonFrame = PlaybackTimeline.ToFrame(commonPosition, sampleRate);
            var offsetAFrame = PlaybackTimeline.ToFrame(_offsets.A, sampleRate);
            var offsetBFrame = PlaybackTimeline.ToFrame(_offsets.B, sampleRate);
            var durationAFrames = PlaybackTimeline.ToFrame(_durationA, sampleRate);
            var durationBFrames = PlaybackTimeline.ToFrame(_durationB, sampleRate);
            var unionFrames = PlaybackTimeline.GetUnionLengthFrames(
                durationAFrames,
                durationBFrames,
                offsetAFrame,
                offsetBFrame);
            commonFrame = PlaybackTimeline.ClampCommonPosition(commonFrame, unionFrames);

            SeekSource(readerA, commonFrame, offsetAFrame, durationAFrames, sampleRate);
            SeekSource(readerB, commonFrame, offsetBFrame, durationBFrames, sampleRate);

            var providerA = new StereoChannelNormalizer(readerA);
            var providerB = new StereoChannelNormalizer(readerB);
            var pair = new SynchronizedPairSampleProvider(
                providerA,
                Math.Max(0, offsetAFrame - commonFrame),
                providerB,
                Math.Max(0, offsetBFrame - commonFrame),
                sampleRate,
                unionFrames - commonFrame)
            {
                Mode = _mode,
                VolumeA = _volumeA,
                VolumeB = _volumeB,
            };

            player.PlaybackStopped += OnPlaybackStopped;
            player.Init(pair.ToWaveProvider());
            _readerA = readerA;
            _readerB = readerB;
            _streamA = streamA;
            _streamB = streamB;
            _pairProvider = pair;
            _player = player;
            _pipelineStartPosition = PlaybackTimeline.FromFrame(commonFrame, sampleRate);
            _position = _pipelineStartPosition;
        }
        catch
        {
            readerA?.Dispose();
            readerB?.Dispose();
            streamA?.Dispose();
            streamB?.Dispose();
            player.Dispose();
            throw;
        }
    }

    private static void SeekSource(
        SoundFileReader reader,
        long commonFrame,
        long offsetFrame,
        long durationFrames,
        int commonSampleRate)
    {
        var sourceFrame = PlaybackTimeline.ToSourceFrame(commonFrame, offsetFrame);
        if (sourceFrame <= 0)
        {
            reader.CurrentTime = TimeSpan.Zero;
            return;
        }

        var clamped = Math.Min(sourceFrame, durationFrames);
        reader.CurrentTime = PlaybackTimeline.FromFrame(clamped, commonSampleRate);
    }

    private TimeSpan GetCurrentPositionUnsafe()
    {
        if (_player is null || _state != PlaybackStateKind.Playing)
        {
            return _position;
        }

        try
        {
            var bytes = _player.GetPosition();
            var elapsed = TimeSpan.FromSeconds(bytes / (double)_player.OutputWaveFormat.AverageBytesPerSecond);
            return ClampPosition(_pipelineStartPosition + elapsed, GetDurationUnsafe());
        }
        catch
        {
            return _position;
        }
    }

    private TimeSpan GetDurationUnsafe()
    {
        if (_pathA is null || _pathB is null)
        {
            return TimeSpan.Zero;
        }

        var endA = AddChecked(_offsets.A, _durationA);
        var endB = AddChecked(_offsets.B, _durationB);
        return endA >= endB ? endA : endB;
    }

    private static TimeSpan AddChecked(TimeSpan left, TimeSpan right)
    {
        try
        {
            return left + right;
        }
        catch (OverflowException)
        {
            throw new ArgumentOutOfRangeException(nameof(left), "Offset適用後のTimelineがTimeSpan範囲を超えている。");
        }
    }

    private static TimeSpan ClampPosition(TimeSpan value, TimeSpan duration)
    {
        if (value <= TimeSpan.Zero)
        {
            return TimeSpan.Zero;
        }

        return value >= duration ? duration : value;
    }

    private void DisposePipelineUnsafe()
    {
        if (_player is not null)
        {
            _player.PlaybackStopped -= OnPlaybackStopped;
            _player.Stop();
            _player.Dispose();
        }

        _player = null;
        _pairProvider = null;
        _readerA?.Dispose();
        _readerB?.Dispose();
        _readerA = null;
        _readerB = null;
        _streamA?.Dispose();
        _streamB?.Dispose();
        _streamA = null;
        _streamB = null;
    }

    private static FileStream OpenAudioStream(string path)
    {
        // Windows版libsndfileのsf_openへPathを直接渡すとUnicode Pathを正しく開けない場合がある。
        // .NET側でFileStreamを開いてvirtual I/Oへ渡すことで、日本語等を含むPathをOSのUnicode APIで解決する。
        return new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
    }

    private void OnPlaybackStopped(object? sender, StoppedEventArgs e)
    {
        EventHandler? ended = null;
        Action<string>? failed = null;
        string? failureMessage = null;

        lock (_gate)
        {
            if (_disposed || !ReferenceEquals(sender, _player))
            {
                return;
            }

            if (e.Exception is not null)
            {
                _position = GetCurrentPositionUnsafe();
                _state = PlaybackStateKind.Stopped;
                failureMessage = e.Exception.Message;
                failed = PlaybackFailed;
            }
            else if (_state == PlaybackStateKind.Playing)
            {
                _position = GetDurationUnsafe();
                _state = PlaybackStateKind.Stopped;
                ended = PlaybackEnded;
            }

            DisposePipelineUnsafe();
        }

        if (failureMessage is not null)
        {
            failed?.Invoke(failureMessage);
        }
        else
        {
            ended?.Invoke(this, EventArgs.Empty);
        }
    }

    private void OnDefaultDeviceChanged(object? sender, DefaultDeviceChangedEventArgs e)
    {
        if (e.Flow != DataFlow.Render || e.Role != Role.Multimedia)
        {
            return;
        }

        lock (_gate)
        {
            if (_disposed || _state != PlaybackStateKind.Playing)
            {
                return;
            }

            // Hot migrationは行わない。現在Streamを止め、次回Play時に新Default DeviceのMix Formatで再構築する。
            DisposePipelineUnsafe();
            _position = TimeSpan.Zero;
            _state = PlaybackStateKind.Stopped;
        }
    }

    private void EnsureLoaded()
    {
        if (_pathA is null || _pathB is null)
        {
            throw new InvalidOperationException("Candidateが読み込まれていない。");
        }
    }

    private void ThrowIfDisposed()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
    }

    private static void ValidateVolume(float value, string parameterName)
    {
        if (!float.IsFinite(value) || value < 0f)
        {
            throw new ArgumentOutOfRangeException(parameterName);
        }
    }

    private enum PlaybackStateKind
    {
        Stopped,
        Playing,
        Paused,
    }
}
