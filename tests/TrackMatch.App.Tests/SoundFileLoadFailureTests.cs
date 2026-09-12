using NAudio.SoundFile;
using TrackMatch.App;
using TrackMatch.App.Playback;
using TrackMatch.Core.Candidates;
using TrackMatch.Core.Playback;
using Xunit;

namespace TrackMatch.App.Tests;

/// <summary>
/// libsndfile由来の再生準備失敗がGUI全体の終了へ波及しないことを確認する。
/// </summary>
public sealed class SoundFileLoadFailureTests
{
    [Fact]
    public void LoadCandidate_WhenSoundFileExceptionOccurs_DisablesPlaybackWithoutThrowing()
    {
        using var viewModel = new SynchronizedPlaybackControlsViewModel(new ThrowingPlaybackService());
        var candidate = CreateCandidate();

        var exception = Record.Exception(() => viewModel.LoadCandidate(candidate));

        Assert.Null(exception);
        Assert.False(viewModel.IsLoaded);
        Assert.False(viewModel.CanControl);
        Assert.StartsWith("再生準備失敗:", viewModel.StatusText, StringComparison.Ordinal);
    }

    private static CandidateReviewItemViewModel CreateCandidate()
        => new(new CandidateReviewReportRow(
            1,
            2,
            null,
            null,
            0.95,
            0.9,
            0.9,
            1,
            TimeSpan.Zero,
            TimeSpan.FromSeconds(120),
            "missing-a.flac",
            "missing-b.mp3",
            [],
            [],
            "A",
            "B",
            null,
            null,
            [],
            [],
            TimeSpan.FromSeconds(180),
            TimeSpan.FromSeconds(181),
            1_000_000,
            900_000,
            "FLAC",
            "MP3",
            "FLAC",
            "MPEG Layer III",
            900,
            320,
            44100,
            48000,
            16,
            null,
            2,
            2));

    private sealed class ThrowingPlaybackService : ISynchronizedPlaybackService
    {
        public event EventHandler? PlaybackEnded
        {
            add { }
            remove { }
        }

        public event Action<string>? PlaybackFailed
        {
            add { }
            remove { }
        }

        public TimeSpan Position => TimeSpan.Zero;

        public TimeSpan Duration => TimeSpan.Zero;

        public PlaybackOffsets Offsets => new(TimeSpan.Zero, TimeSpan.Zero);

        public SynchronizedPlaybackMode Mode { get; set; }

        public float VolumeA { get; set; } = 1f;

        public float VolumeB { get; set; } = 1f;

        public bool IsPlaying => false;

        public bool IsPaused => false;

        public void Load(string pathA, string pathB, TimeSpan bestOffset)
            => throw new SoundFileException(0, "sf_open", "System error");

        public void Play()
        {
        }

        public void Pause()
        {
        }

        public void Stop()
        {
        }

        public void Seek(TimeSpan position)
        {
        }

        public void SetOffsets(TimeSpan offsetA, TimeSpan offsetB)
        {
        }

        public void Dispose()
        {
        }
    }
}
