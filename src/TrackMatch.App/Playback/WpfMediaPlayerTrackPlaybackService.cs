using System.Windows.Media;

namespace TrackMatch.App.Playback;

/// <summary>
/// WPFのMediaPlayerを利用して候補トラックを再生する。
/// </summary>
public sealed class WpfMediaPlayerTrackPlaybackService : ITrackPlaybackService
{
    private readonly MediaPlayer _player = new();
    private bool _disposed;

    public WpfMediaPlayerTrackPlaybackService()
    {
        _player.MediaEnded += OnMediaEnded;
        _player.MediaFailed += OnMediaFailed;
    }

    /// <inheritdoc />
    public event EventHandler? PlaybackEnded;

    /// <inheritdoc />
    public event Action<string>? PlaybackFailed;

    /// <inheritdoc />
    public void Play(string path)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        if (!File.Exists(path))
        {
            throw new FileNotFoundException("再生対象の音声ファイルが見つからない。", path);
        }

        // A/Bを切り替えるたびに同じMediaPlayerを使い回し、複数トラックが同時再生されないようにする。
        _player.Stop();
        _player.Close();
        _player.Open(new Uri(Path.GetFullPath(path), UriKind.Absolute));
        _player.Play();
    }

    /// <inheritdoc />
    public void Stop()
    {
        if (_disposed)
        {
            return;
        }

        _player.Stop();
        _player.Close();
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _player.MediaEnded -= OnMediaEnded;
        _player.MediaFailed -= OnMediaFailed;
        _player.Stop();
        _player.Close();
    }

    private void OnMediaEnded(object? sender, EventArgs e)
        => PlaybackEnded?.Invoke(this, EventArgs.Empty);

    private void OnMediaFailed(object? sender, ExceptionEventArgs e)
        => PlaybackFailed?.Invoke(e.ErrorException?.Message ?? "音声を再生できない。");
}
