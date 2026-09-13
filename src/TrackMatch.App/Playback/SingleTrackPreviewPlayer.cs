using NAudio.CoreAudioApi;
using NAudio.SoundFile;
using NAudio.Wave;

namespace TrackMatch.App.Playback;

/// <summary>
/// 重複グループ詳細画面で、1ファイルだけを簡易試聴するための再生処理を提供する。
/// </summary>
/// <remarks>
/// A/B同期再生とは目的が異なるため専用の軽量Playerとして分離し、シークや複数同時再生は持たせない。
/// </remarks>
internal sealed class SingleTrackPreviewPlayer : IDisposable
{
    private readonly object _gate = new();
    private readonly MMDeviceEnumerator _deviceEnumerator = new();
    private WasapiPlayer? _player;
    private SoundFileReader? _reader;
    private FileStream? _stream;
    private string? _playingPath;
    private bool _disposed;

    /// <summary>自然終了または停止で現在の試聴が終了したときに発生する。</summary>
    public event EventHandler? PlaybackStopped;

    /// <summary>現在再生しているファイルの絶対Path。停止中はnull。</summary>
    public string? PlayingPath
    {
        get
        {
            lock (_gate)
            {
                return _playingPath;
            }
        }
    }

    /// <summary>
    /// 指定ファイルを先頭から再生する。別ファイルを再生中の場合は先に停止する。
    /// </summary>
    public void Play(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        lock (_gate)
        {
            ThrowIfDisposed();
            DisposePipelineUnsafe();

            var fullPath = Path.GetFullPath(path);
            FileStream? stream = null;
            SoundFileReader? reader = null;
            WasapiPlayer? player = null;
            try
            {
                // libsndfileへPathを直接渡さずFileStream経由にすることで、日本語等を含むWindows Pathでも
                // A/B同期再生と同じUnicode Pathの扱いを維持する。
                stream = new FileStream(fullPath, FileMode.Open, FileAccess.Read, FileShare.Read);
                reader = new SoundFileReader(stream);
                using var device = _deviceEnumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
                player = new WasapiPlayerBuilder()
                    .WithDevice(device)
                    .WithSharedMode()
                    .WithEventSync()
                    .Build();
                player.PlaybackStopped += OnPlaybackStopped;
                player.Init(reader.ToWaveProvider());

                _stream = stream;
                _reader = reader;
                _player = player;
                _playingPath = fullPath;
                player.Play();
            }
            catch
            {
                if (player is not null)
                {
                    player.PlaybackStopped -= OnPlaybackStopped;
                    player.Dispose();
                }

                reader?.Dispose();
                stream?.Dispose();
                _playingPath = null;
                throw;
            }
        }
    }

    /// <summary>現在の簡易試聴を停止する。</summary>
    public void Stop()
    {
        lock (_gate)
        {
            ThrowIfDisposed();
            var hadPlayback = _playingPath is not null;
            DisposePipelineUnsafe();
            if (hadPlayback)
            {
                PlaybackStopped?.Invoke(this, EventArgs.Empty);
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
            DisposePipelineUnsafe();
            _deviceEnumerator.Dispose();
        }
    }

    private void OnPlaybackStopped(object? sender, StoppedEventArgs e)
    {
        EventHandler? stopped;
        lock (_gate)
        {
            if (_disposed || !ReferenceEquals(sender, _player))
            {
                return;
            }

            DisposePipelineUnsafe();
            stopped = PlaybackStopped;
        }

        stopped?.Invoke(this, EventArgs.Empty);
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
        _reader?.Dispose();
        _reader = null;
        _stream?.Dispose();
        _stream = null;
        _playingPath = null;
    }

    private void ThrowIfDisposed()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
    }
}
