using NAudio.SoundFile;
using R128Net;
using TrackMatch.Core.Candidates;
using TrackMatch.Core.Playback;
using TrackMatch.Core.Quality;

namespace TrackMatch.Infrastructure.Audio;

/// <summary>
/// Candidateの一致区間だけをDecodeし、A/Bの相対的な音量・ダイナミクス差を解析する。
/// </summary>
public sealed class NAudioCandidateQualityAnalyzer : ICandidateQualityAnalyzer
{
    private static readonly TimeSpan WindowDuration = TimeSpan.FromSeconds(1);

    /// <inheritdoc />
    public async Task<CandidateQualityComparison> AnalyzeAsync(
        CandidateComparison candidate,
        string pathA,
        string pathB,
        TrackQualityAnalysis analysisA,
        TrackQualityAnalysis analysisB,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(candidate);
        ArgumentException.ThrowIfNullOrWhiteSpace(pathA);
        ArgumentException.ThrowIfNullOrWhiteSpace(pathB);
        ArgumentNullException.ThrowIfNull(analysisA);
        ArgumentNullException.ThrowIfNull(analysisB);

        var fullPathA = Path.GetFullPath(pathA);
        var fullPathB = Path.GetFullPath(pathB);

        // Candidate表示後に外部操作で片側だけ削除されるケースを通常状態として扱う。
        // FileStream生成前にA/Bを確認し、欠落時はFileNotFoundExceptionを発生させず解析失敗へ変換する。
        if (!File.Exists(fullPathA))
        {
            return CreateFailure(candidate, $"音源Aのファイルが見つかりません: {fullPathA}");
        }

        if (!File.Exists(fullPathB))
        {
            return CreateFailure(candidate, $"音源Bのファイルが見つかりません: {fullPathB}");
        }

        try
        {
            return await Task.Run(
                () => AnalyzeCore(candidate, fullPathA, fullPathB, analysisA, analysisB, cancellationToken),
                cancellationToken);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or SoundFileException or NotSupportedException or ArgumentException or InvalidDataException)
        {
            // Exists確認直後の削除やNAS切断などは競合として残るため、I/O例外の捕捉は保険として維持する。
            return CreateFailure(candidate, ex.Message);
        }
    }

    private static CandidateQualityComparison AnalyzeCore(
        CandidateComparison candidate,
        string pathA,
        string pathB,
        TrackQualityAnalysis analysisA,
        TrackQualityAnalysis analysisB,
        CancellationToken cancellationToken)
    {
        using var streamA = OpenAudioStream(pathA);
        using var streamB = OpenAudioStream(pathB);
        using var readerA = new SoundFileReader(streamA);
        using var readerB = new SoundFileReader(streamB);

        var offsets = PlaybackOffsets.Normalize(TimeSpan.Zero, candidate.BestOffset);
        var commonStart = offsets.A >= offsets.B ? offsets.A : offsets.B;
        var sourceStartA = commonStart - offsets.A;
        var sourceStartB = commonStart - offsets.B;
        var availableA = readerA.TotalTime - sourceStartA;
        var availableB = readerB.TotalTime - sourceStartB;
        if (candidate.MatchedDuration <= TimeSpan.Zero || availableA <= TimeSpan.Zero || availableB <= TimeSpan.Zero)
        {
            throw new InvalidDataException("Candidateの一致区間に解析可能な音声がありません。");
        }

        var compareDuration = new[] { candidate.MatchedDuration, availableA, availableB }.Min();
        Seek(readerA, sourceStartA);
        Seek(readerB, sourceStartB);

        using var meterA = new LoudnessMeter(
            readerA.WaveFormat.Channels,
            readerA.WaveFormat.SampleRate,
            LoudnessModes.Integrated);
        using var meterB = new LoudnessMeter(
            readerB.WaveFormat.Channels,
            readerB.WaveFormat.SampleRate,
            LoudnessModes.Integrated);

        var gainDifferences = new List<double>();
        var elapsed = TimeSpan.Zero;
        while (elapsed < compareDuration)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var duration = compareDuration - elapsed < WindowDuration
                ? compareDuration - elapsed
                : WindowDuration;

            var windowA = ReadWindow(readerA, duration, cancellationToken);
            var windowB = ReadWindow(readerB, duration, cancellationToken);
            if (windowA.SampleCount == 0 || windowB.SampleCount == 0)
            {
                break;
            }

            meterA.AddFrames(windowA.Samples.AsSpan(0, windowA.SampleCount));
            meterB.AddFrames(windowB.Samples.AsSpan(0, windowB.SampleCount));

            var rmsA = CalculateRms(windowA.Samples, windowA.SampleCount);
            var rmsB = CalculateRms(windowB.Samples, windowB.SampleCount);
            if (rmsA > 0 && rmsB > 0)
            {
                gainDifferences.Add(20 * Math.Log10(rmsB / rmsA));
            }

            elapsed += duration;
        }

        return CandidateQualityComparisonFactory.CreateAnalyzed(
            candidate,
            NormalizeFinite(meterA.IntegratedLoudness),
            NormalizeFinite(meterB.IntegratedLoudness),
            gainDifferences,
            analysisA,
            analysisB,
            DateTime.UtcNow);
    }

    private static FileStream OpenAudioStream(string path)
        => new(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            bufferSize: 64 * 1024,
            FileOptions.SequentialScan);

    private static void Seek(SoundFileReader reader, TimeSpan sourcePosition)
    {
        var frame = PlaybackTimeline.ToFrame(sourcePosition, reader.WaveFormat.SampleRate);
        var bytePosition = checked(frame * reader.WaveFormat.BlockAlign);
        reader.Position = Math.Clamp(bytePosition, 0, reader.Length);
    }

    private static WindowSamples ReadWindow(
        SoundFileReader reader,
        TimeSpan duration,
        CancellationToken cancellationToken)
    {
        var frames = Math.Max(1, PlaybackTimeline.ToFrame(duration, reader.WaveFormat.SampleRate));
        var requestedSamplesLong = checked(frames * reader.WaveFormat.Channels);
        if (requestedSamplesLong > int.MaxValue)
        {
            throw new NotSupportedException("解析窓が大きすぎます。");
        }

        var samples = new float[(int)requestedSamplesLong];
        var total = 0;
        while (total < samples.Length)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var read = reader.Read(samples.AsSpan(total));
            if (read <= 0)
            {
                break;
            }

            total += read;
        }

        total -= total % reader.WaveFormat.Channels;
        return new WindowSamples(samples, total);
    }

    private static double CalculateRms(float[] samples, int count)
    {
        double squareSum = 0;
        for (var i = 0; i < count; i++)
        {
            squareSum += samples[i] * samples[i];
        }

        return count == 0 ? 0 : Math.Sqrt(squareSum / count);
    }

    private static double? NormalizeFinite(double value)
        => double.IsFinite(value) ? value : null;

    private static CandidateQualityComparison CreateFailure(CandidateComparison candidate, string message)
        => new(
            candidate.TrackIdA,
            candidate.TrackIdB,
            QualityAnalysisVersions.CandidateQualityComparison,
            QualityAnalysisStatus.Failed,
            null,
            null,
            null,
            null,
            null,
            null,
            null,
            null,
            message);

    private sealed record WindowSamples(float[] Samples, int SampleCount);
}
