using TrackMatch.Core.Playback;
using Xunit;

namespace TrackMatch.Core.Tests;

public sealed class SynchronizedPlaybackModelTests
{
    [Fact]
    public void NormalizeOffsets_PreservesRelativeDifferenceAndSetsOneSideToZero()
    {
        var normalized = PlaybackOffsets.Normalize(TimeSpan.FromMilliseconds(-740), TimeSpan.Zero);

        Assert.Equal(TimeSpan.Zero, normalized.A);
        Assert.Equal(TimeSpan.FromMilliseconds(740), normalized.B);
    }

    [Fact]
    public void ToFrame_RoundsToNearestSampleFrame()
    {
        var frame = PlaybackTimeline.ToFrame(TimeSpan.FromMilliseconds(0.5), 44100);

        Assert.Equal(22, frame);
    }

    [Fact]
    public void UnionTimeline_IncludesOffsetsAndLongerTail()
    {
        var length = PlaybackTimeline.GetUnionLengthFrames(
            durationAFrames: 100,
            durationBFrames: 80,
            offsetAFrames: 0,
            offsetBFrames: 40);

        Assert.Equal(120, length);
    }

    [Fact]
    public void HasAudio_TreatsBeforeStartAndAfterEndAsSilence()
    {
        Assert.False(PlaybackTimeline.HasAudio(commonFrame: 10, sourceOffsetFrames: 20, sourceDurationFrames: 50));
        Assert.True(PlaybackTimeline.HasAudio(commonFrame: 20, sourceOffsetFrames: 20, sourceDurationFrames: 50));
        Assert.True(PlaybackTimeline.HasAudio(commonFrame: 69, sourceOffsetFrames: 20, sourceDurationFrames: 50));
        Assert.False(PlaybackTimeline.HasAudio(commonFrame: 70, sourceOffsetFrames: 20, sourceDurationFrames: 50));
    }

    [Theory]
    [InlineData(-10, 100, 0)]
    [InlineData(50, 100, 50)]
    [InlineData(110, 100, 100)]
    public void ClampCommonPosition_ClampsToUnionRange(long input, long length, long expected)
    {
        Assert.Equal(expected, PlaybackTimeline.ClampCommonPosition(input, length));
    }

    [Fact]
    public void Route_AOnly_AppliesOnlyAGain()
    {
        var output = PlaybackFrameRouter.Route(
            new StereoSampleFrame(0.4f, -0.2f),
            new StereoSampleFrame(0.8f, 0.6f),
            0.5f,
            0.25f,
            SynchronizedPlaybackMode.AOnly);

        Assert.Equal(0.2f, output.Left, 5);
        Assert.Equal(-0.1f, output.Right, 5);
    }

    [Fact]
    public void Route_BOnly_AppliesOnlyBGain()
    {
        var output = PlaybackFrameRouter.Route(
            new StereoSampleFrame(0.4f, -0.2f),
            new StereoSampleFrame(0.8f, 0.6f),
            0.5f,
            0.25f,
            SynchronizedPlaybackMode.BOnly);

        Assert.Equal(0.2f, output.Left, 5);
        Assert.Equal(0.15f, output.Right, 5);
    }

    [Fact]
    public void Route_StereoOverlay_PreservesStereoAndAddsBothTracks()
    {
        var output = PlaybackFrameRouter.Route(
            new StereoSampleFrame(0.4f, 0.1f),
            new StereoSampleFrame(0.2f, -0.3f),
            1f,
            1f,
            SynchronizedPlaybackMode.StereoOverlay);

        Assert.Equal(0.6f, output.Left, 5);
        Assert.Equal(-0.2f, output.Right, 5);
    }

    [Fact]
    public void Route_SplitLeftRight_DownmixesEachTrackToMono()
    {
        var output = PlaybackFrameRouter.Route(
            new StereoSampleFrame(0.8f, 0.2f),
            new StereoSampleFrame(-0.4f, 0.2f),
            1f,
            0.5f,
            SynchronizedPlaybackMode.SplitLeftRight);

        Assert.Equal(0.5f, output.Left, 5);
        Assert.Equal(-0.05f, output.Right, 5);
    }
}
