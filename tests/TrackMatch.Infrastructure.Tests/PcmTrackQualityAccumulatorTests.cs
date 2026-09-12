using TrackMatch.Core.Quality;
using TrackMatch.Infrastructure.Audio;
using Xunit;

namespace TrackMatch.Infrastructure.Tests;

/// <summary>
/// Decode処理から独立した品質解析集計を合成PCMで検証する。
/// </summary>
public sealed class PcmTrackQualityAccumulatorTests
{
    private const int SampleRate = 48000;

    [Fact]
    public void Build_MeasuresLoudnessTruePeakAndPlrForStereoSignal()
    {
        using var accumulator = new PcmTrackQualityAccumulator(2, SampleRate);
        accumulator.AddFrames(CreateStereoSine(seconds: 4, leftAmplitude: 0.25f, rightAmplitude: 0.25f));

        var result = accumulator.Build(
            1,
            new DateTime(2026, 9, 12, 0, 0, 0, DateTimeKind.Utc));

        Assert.Equal(QualityAnalysisStatus.Analyzed, result.Status);
        Assert.Equal(QualityAnalysisVersions.TrackQualityAnalysis, result.AnalysisVersion);
        Assert.NotNull(result.IntegratedLoudnessLufs);
        Assert.NotNull(result.TruePeakDbtp);
        Assert.NotNull(result.LoudnessRangeLu);
        Assert.NotNull(result.PeakToLoudnessRatioDb);
        Assert.InRange(result.TruePeakDbtp!.Value, -12.2, -11.8);
        Assert.InRange(Math.Abs(result.LeftRightLevelDifferenceDb!.Value), 0, 0.01);
    }

    [Fact]
    public void Build_TracksPeakNearRunsAndDurations()
    {
        using var accumulator = new PcmTrackQualityAccumulator(2, SampleRate);
        var samples = new float[200 * 2];

        FillStereoFrames(samples, startFrame: 10, frameCount: 20, value: 1.0f);
        FillStereoFrames(samples, startFrame: 80, frameCount: 30, value: -1.0f);
        accumulator.AddFrames(samples);

        var result = accumulator.Build(
            1,
            new DateTime(2026, 9, 12, 0, 0, 0, DateTimeKind.Utc));

        Assert.Equal(100, result.PeakNearSampleCount);
        Assert.Equal(2, result.ClippingRunCount);
        Assert.Equal(TimeSpan.FromSeconds(50d / SampleRate), result.ClippingTotalDuration);
        Assert.Equal(TimeSpan.FromSeconds(30d / SampleRate), result.ClippingLongestDuration);
    }

    [Fact]
    public void Build_MeasuresLeftRightLevelDifference()
    {
        using var accumulator = new PcmTrackQualityAccumulator(2, SampleRate);
        accumulator.AddFrames(CreateStereoSine(seconds: 4, leftAmplitude: 0.5f, rightAmplitude: 0.25f));

        var result = accumulator.Build(
            1,
            new DateTime(2026, 9, 12, 0, 0, 0, DateTimeKind.Utc));

        Assert.NotNull(result.LeftRightLevelDifferenceDb);
        Assert.InRange(result.LeftRightLevelDifferenceDb!.Value, 5.9, 6.1);
    }

    [Fact]
    public void Build_ProducesFiniteSpectrumSummaryWithoutKeepingRawFft()
    {
        using var accumulator = new PcmTrackQualityAccumulator(2, SampleRate);
        accumulator.AddFrames(CreateStereoSine(seconds: 4, leftAmplitude: 0.25f, rightAmplitude: 0.25f, frequencyHz: 1000));

        var result = accumulator.Build(
            1,
            new DateTime(2026, 9, 12, 0, 0, 0, DateTimeKind.Utc));

        Assert.NotNull(result.EffectiveUpperFrequencyHz);
        Assert.InRange(result.EffectiveUpperFrequencyHz!.Value, 900, 1200);
        Assert.NotNull(result.HighFrequencyEnergyRatio);
        Assert.InRange(result.HighFrequencyEnergyRatio!.Value, 0, 0.001);
    }

    private static float[] CreateStereoSine(
        int seconds,
        float leftAmplitude,
        float rightAmplitude,
        double frequencyHz = 1000)
    {
        var frames = SampleRate * seconds;
        var samples = new float[frames * 2];
        for (var frame = 0; frame < frames; frame++)
        {
            var sine = (float)Math.Sin((2 * Math.PI * frequencyHz * frame) / SampleRate);
            samples[frame * 2] = sine * leftAmplitude;
            samples[(frame * 2) + 1] = sine * rightAmplitude;
        }

        return samples;
    }

    private static void FillStereoFrames(float[] samples, int startFrame, int frameCount, float value)
    {
        for (var frame = startFrame; frame < startFrame + frameCount; frame++)
        {
            samples[frame * 2] = value;
            samples[(frame * 2) + 1] = value;
        }
    }
}
