using System.ComponentModel;
using System.Globalization;
using System.Runtime.CompilerServices;
using NAudio.SoundFile;
using TrackMatch.Core.Playback;

namespace TrackMatch.App.Playback;

/// <summary>
/// 同期A/B Playback EngineをWPFへ公開するためのUI状態と操作を管理する。
/// </summary>
public sealed class SynchronizedPlaybackControlsViewModel : INotifyPropertyChanged, IDisposable
{
    private readonly ISynchronizedPlaybackService _service;
    private readonly SynchronizationContext? _synchronizationContext;
    private TimeSpan _bestOffset;
    private bool _isLoaded;
    private bool _disposed;
    private double _positionSeconds;
    private double _durationSeconds;
    private double _offsetASeconds;
    private double _offsetBSeconds;
    private double _volumeAPercent = 100d;
    private double _volumeBPercent = 100d;
    private PlaybackModeOption _selectedMode;
    private string _positionText = "00:00.000";
    private string _durationText = "00:00.000";
    private string _statusText = "停止中";

    /// <summary>
    /// 同期Playback操作用ViewModelを生成する。
    /// </summary>
    /// <param name="service">A/BをCommon Timeline上で同期再生するService</param>
    public SynchronizedPlaybackControlsViewModel(ISynchronizedPlaybackService service)
    {
        _service = service ?? throw new ArgumentNullException(nameof(service));
        _synchronizationContext = SynchronizationContext.Current;
        Modes =
        [
            new PlaybackModeOption(SynchronizedPlaybackMode.AOnly, "A only"),
            new PlaybackModeOption(SynchronizedPlaybackMode.BOnly, "B only"),
            new PlaybackModeOption(SynchronizedPlaybackMode.StereoOverlay, "A/B Stereo Overlay"),
            new PlaybackModeOption(SynchronizedPlaybackMode.SplitLeftRight, "A→L / B→R"),
        ];
        _selectedMode = Modes.Single(item => item.Value == SynchronizedPlaybackMode.StereoOverlay);
        _service.Mode = _selectedMode.Value;
        _service.PlaybackEnded += OnPlaybackEnded;
        _service.PlaybackFailed += OnPlaybackFailed;
    }

    public IReadOnlyList<PlaybackModeOption> Modes { get; }

    public PlaybackModeOption SelectedMode
    {
        get => _selectedMode;
        set
        {
            ArgumentNullException.ThrowIfNull(value);
            if (ReferenceEquals(_selectedMode, value))
            {
                return;
            }

            _selectedMode = value;
            _service.Mode = value.Value;
            OnPropertyChanged();
        }
    }

    public bool IsLoaded
    {
        get => _isLoaded;
        private set
        {
            if (SetField(ref _isLoaded, value))
            {
                OnPropertyChanged(nameof(CanControl));
            }
        }
    }

    public bool CanControl => IsLoaded;

    public double PositionSeconds
    {
        get => _positionSeconds;
        private set => SetField(ref _positionSeconds, value);
    }

    public double DurationSeconds
    {
        get => _durationSeconds;
        private set => SetField(ref _durationSeconds, value);
    }

    public string PositionText
    {
        get => _positionText;
        private set => SetField(ref _positionText, value);
    }

    public string DurationText
    {
        get => _durationText;
        private set => SetField(ref _durationText, value);
    }

    public string StatusText
    {
        get => _statusText;
        private set => SetField(ref _statusText, value);
    }

    public double OffsetASeconds
    {
        get => _offsetASeconds;
        private set => SetField(ref _offsetASeconds, value);
    }

    public double OffsetBSeconds
    {
        get => _offsetBSeconds;
        private set => SetField(ref _offsetBSeconds, value);
    }

    public string OffsetAText => $"{OffsetASeconds:0.000} s";

    public string OffsetBText => $"{OffsetBSeconds:0.000} s";

    public double VolumeAPercent
    {
        get => _volumeAPercent;
        set
        {
            var normalized = Math.Clamp(value, 0d, 100d);
            if (SetField(ref _volumeAPercent, normalized))
            {
                _service.VolumeA = (float)(normalized / 100d);
            }
        }
    }

    public double VolumeBPercent
    {
        get => _volumeBPercent;
        set
        {
            var normalized = Math.Clamp(value, 0d, 100d);
            if (SetField(ref _volumeBPercent, normalized))
            {
                _service.VolumeB = (float)(normalized / 100d);
            }
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    /// <summary>
    /// Candidate切替時に解析BestOffsetを適用し、一時Playback状態を初期化する。
    /// </summary>
    public void LoadCandidate(CandidateReviewItemViewModel? candidate)
    {
        ThrowIfDisposed();
        _service.Stop();
        if (candidate is null)
        {
            _bestOffset = TimeSpan.Zero;
            IsLoaded = false;
            ResetDisplay();
            return;
        }

        try
        {
            _bestOffset = candidate.Row.BestOffset;
            _service.Load(candidate.Row.PathA, candidate.Row.PathB, _bestOffset);
            IsLoaded = true;
            _volumeAPercent = 100d;
            _volumeBPercent = 100d;
            OnPropertyChanged(nameof(VolumeAPercent));
            OnPropertyChanged(nameof(VolumeBPercent));
            SyncFromService();
            StatusText = "停止中";
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidOperationException or NotSupportedException or DllNotFoundException or ArgumentException or SoundFileException)
        {
            IsLoaded = false;
            ResetDisplay();
            StatusText = $"再生準備失敗: {exception.Message}";
        }
    }

    /// <summary>
    /// 停止中/一時停止中なら再生し、再生中なら一時停止する。
    /// </summary>
    public void TogglePlayPause()
    {
        if (!IsLoaded)
        {
            return;
        }

        if (_service.IsPlaying)
        {
            _service.Pause();
            SyncFromService();
            StatusText = "一時停止中";
            return;
        }

        _service.Play();
        SyncFromService();
        if (_service.IsPlaying)
        {
            StatusText = "再生中";
        }
    }

    /// <summary>
    /// Playbackを停止し、Common Positionを先頭へ戻す。
    /// </summary>
    public void Stop()
    {
        _service.Stop();
        SyncFromService();
        StatusText = "停止中";
    }

    /// <summary>
    /// Seek Bar等からCommon Positionを秒単位で変更する。
    /// </summary>
    public void SeekSeconds(double seconds)
    {
        if (!IsLoaded || !double.IsFinite(seconds))
        {
            return;
        }

        try
        {
            _service.Seek(TimeSpan.FromSeconds(seconds));
            SyncFromService();
        }
        catch (OverflowException)
        {
            StatusText = "Seek位置が表現可能範囲を超えています。";
        }
    }

    /// <summary>
    /// `mm:ss.fff`または`h:mm:ss.fff`形式の入力をCommon Positionへ反映する。
    /// </summary>
    public bool CommitPositionText(string text)
    {
        if (!IsLoaded || !TryParsePlaybackTime(text, out var value))
        {
            SyncFromService();
            return false;
        }

        _service.Seek(value);
        SyncFromService();
        return true;
    }

    /// <summary>
    /// 指定側Offsetを10ms単位等で調整し、直後に正規化してEngineへ反映する。
    /// </summary>
    public void AdjustOffset(bool isTrackA, TimeSpan delta)
    {
        if (!IsLoaded)
        {
            return;
        }

        var offsets = _service.Offsets;
        try
        {
            var a = isTrackA ? offsets.A + delta : offsets.A;
            var b = isTrackA ? offsets.B : offsets.B + delta;
            _service.SetOffsets(a, b);
            SyncFromService();
        }
        catch (Exception exception) when (exception is OverflowException or ArgumentOutOfRangeException)
        {
            StatusText = "Offsetが表現可能範囲を超えています。";
        }
    }

    /// <summary>
    /// 秒Decimalの直接入力を指定側Offsetへ反映する。
    /// </summary>
    public bool CommitOffsetText(bool isTrackA, string text)
    {
        if (!IsLoaded || !TryParseSeconds(text, out var value))
        {
            SyncFromService();
            return false;
        }

        try
        {
            var offsets = _service.Offsets;
            _service.SetOffsets(
                isTrackA ? value : offsets.A,
                isTrackA ? offsets.B : value);
            SyncFromService();
            return true;
        }
        catch (ArgumentOutOfRangeException)
        {
            StatusText = "Offsetが表現可能範囲を超えています。";
            SyncFromService();
            return false;
        }
    }

    /// <summary>
    /// Manual Offsetを破棄し、Candidate解析時のBestOffsetへ戻す。
    /// </summary>
    public void ResetOffsetToAnalysis()
    {
        if (!IsLoaded)
        {
            return;
        }

        _service.SetOffsets(TimeSpan.Zero, _bestOffset);
        SyncFromService();
    }

    /// <summary>
    /// Engine上の再生位置をUI表示へ同期する。Main WindowのTimerから呼び出す。
    /// </summary>
    public void RefreshPosition()
    {
        if (!IsLoaded)
        {
            return;
        }

        SyncFromService();
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _service.PlaybackEnded -= OnPlaybackEnded;
        _service.PlaybackFailed -= OnPlaybackFailed;
        _service.Dispose();
    }

    private void SyncFromService()
    {
        PositionSeconds = Math.Max(0d, _service.Position.TotalSeconds);
        DurationSeconds = Math.Max(0d, _service.Duration.TotalSeconds);
        PositionText = FormatTime(_service.Position);
        DurationText = FormatTime(_service.Duration);
        OffsetASeconds = _service.Offsets.A.TotalSeconds;
        OffsetBSeconds = _service.Offsets.B.TotalSeconds;
        OnPropertyChanged(nameof(OffsetAText));
        OnPropertyChanged(nameof(OffsetBText));
    }

    private void ResetDisplay()
    {
        _positionSeconds = 0d;
        _durationSeconds = 0d;
        _offsetASeconds = 0d;
        _offsetBSeconds = 0d;
        _positionText = "00:00.000";
        _durationText = "00:00.000";
        _volumeAPercent = 100d;
        _volumeBPercent = 100d;
        OnPropertyChanged(nameof(PositionSeconds));
        OnPropertyChanged(nameof(DurationSeconds));
        OnPropertyChanged(nameof(PositionText));
        OnPropertyChanged(nameof(DurationText));
        OnPropertyChanged(nameof(OffsetASeconds));
        OnPropertyChanged(nameof(OffsetBSeconds));
        OnPropertyChanged(nameof(OffsetAText));
        OnPropertyChanged(nameof(OffsetBText));
        OnPropertyChanged(nameof(VolumeAPercent));
        OnPropertyChanged(nameof(VolumeBPercent));
    }

    private void OnPlaybackEnded(object? sender, EventArgs e)
        => PostToCapturedContext(() =>
        {
            SyncFromService();
            StatusText = "再生終了";
        });

    private void OnPlaybackFailed(string message)
        => PostToCapturedContext(() =>
        {
            SyncFromService();
            StatusText = $"再生失敗: {message}";
        });

    private void PostToCapturedContext(Action action)
    {
        if (_synchronizationContext is null)
        {
            action();
            return;
        }

        _synchronizationContext.Post(_ => action(), null);
    }

    private static bool TryParseSeconds(string text, out TimeSpan value)
    {
        var normalized = text.Trim();
        if (normalized.EndsWith("s", StringComparison.OrdinalIgnoreCase))
        {
            normalized = normalized[..^1].Trim();
        }

        if (!decimal.TryParse(normalized, NumberStyles.Float, CultureInfo.CurrentCulture, out var seconds)
            && !decimal.TryParse(normalized, NumberStyles.Float, CultureInfo.InvariantCulture, out seconds))
        {
            value = default;
            return false;
        }

        try
        {
            value = TimeSpan.FromTicks(checked((long)Math.Round(seconds * TimeSpan.TicksPerSecond, MidpointRounding.AwayFromZero)));
            return true;
        }
        catch (OverflowException)
        {
            value = default;
            return false;
        }
    }

    private static bool TryParsePlaybackTime(string text, out TimeSpan value)
    {
        var formats = new[] { @"m\:ss\.fff", @"mm\:ss\.fff", @"h\:mm\:ss\.fff", @"hh\:mm\:ss\.fff" };
        return TimeSpan.TryParseExact(text.Trim(), formats, CultureInfo.InvariantCulture, out value)
            || TimeSpan.TryParse(text.Trim(), CultureInfo.CurrentCulture, out value);
    }

    private static string FormatTime(TimeSpan value)
        => value.TotalHours >= 1d
            ? value.ToString(@"h\:mm\:ss\.fff", CultureInfo.InvariantCulture)
            : value.ToString(@"mm\:ss\.fff", CultureInfo.InvariantCulture);

    private void ThrowIfDisposed() => ObjectDisposedException.ThrowIf(_disposed, this);

    private bool SetField<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value))
        {
            return false;
        }

        field = value;
        OnPropertyChanged(propertyName);
        return true;
    }

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
}

/// <summary>
/// Playback Modeの表示名とEngine値を対応付ける。
/// </summary>
public sealed record PlaybackModeOption(SynchronizedPlaybackMode Value, string Label);
