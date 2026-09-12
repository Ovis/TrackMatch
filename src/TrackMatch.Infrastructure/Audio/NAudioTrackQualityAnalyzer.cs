using NAudio.SoundFile;
using TrackMatch.Core.Quality;

namespace TrackMatch.Infrastructure.Audio;

/// <summary>
/// libsndfileでPCMを1回だけDecodeし、Track単体の品質解析へ供給する。
/// </summary>
public sealed class NAudioTrackQualityAnalyzer : ITrackQualityAnalyzer
{
    private const int BufferFrames = 4096;

    /// <inheritdoc />
    public async Task<TrackQualityAnalysis> AnalyzeAsync(
        long trackId,
        string path,
        CancellationToken cancellationToken = default)
    {
        if (trackId <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(trackId));
        }

        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        var fullPath = Path.GetFullPath(path);

        try
        {
            return await Task.Run(
                () => AnalyzeCore(trackId, fullPath, cancellationToken),
                cancellationToken);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (NotSupportedException ex)
        {
            return CreateFailure(trackId, QualityAnalysisStatus.Unsupported, ex.Message);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or SoundFileException or ArgumentException)
        {
            return CreateFailure(trackId, QualityAnalysisStatus.Failed, ex.Message);
        }
    }

    private static TrackQualityAnalysis AnalyzeCore(
        long trackId,
        string fullPath,
        CancellationToken cancellationToken)
    {
        // libsndfileのWindows path APIではUnicode pathを正しく扱えないため、再生処理と同様に.NET側でFileStreamを開く。
        using var stream = new FileStream(
            fullPath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            bufferSize: 64 * 1024,
            FileOptions.SequentialScan);
        using var reader = new SoundFileReader(stream);

        var channels = reader.WaveFormat.Channels;
        var sampleRate = reader.WaveFormat.SampleRate;
        if (channels <= 0 || sampleRate <= 0)
        {
            throw new NotSupportedException("音声のチャンネル数またはサンプルレートを取得できませんでした。");
        }

        using var accumulator = new PcmTrackQualityAccumulator(channels, sampleRate);
        var buffer = new float[BufferFrames * channels];

        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var read = reader.Read(buffer.AsSpan());
            if (read <= 0)
            {
                break;
            }

            accumulator.AddFrames(buffer.AsSpan(0, read));
        }

        return accumulator.Build(trackId, DateTime.UtcNow);
    }

    private static TrackQualityAnalysis CreateFailure(
        long trackId,
        QualityAnalysisStatus status,
        string message)
        => new(
            trackId,
            QualityAnalysisVersions.TrackQualityAnalysis,
            status,
            null,
            null,
            null,
            null,
            0,
            0,
            TimeSpan.Zero,
            TimeSpan.Zero,
            null,
            null,
            null,
            null,
            null,
            null,
            null,
            message);
}
