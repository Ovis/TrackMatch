using TrackMatch.Core.Quality;
using TrackMatch.Infrastructure.Audio;
using Xunit;

namespace TrackMatch.Infrastructure.Tests;

/// <summary>
/// 外部削除された音源を通常の解析失敗として扱い、FileNotFoundExceptionを発生させないことを検証する。
/// </summary>
public sealed class MissingAudioFileQualityAnalysisTests
{
    [Fact]
    public async Task AnalyzeAsync_WhenFileDoesNotExist_ReturnsFailedWithoutThrowing()
    {
        var missingPath = Path.Combine(Path.GetTempPath(), $"trackmatch-missing-{Guid.NewGuid():N}.flac");
        var analyzer = new NAudioTrackQualityAnalyzer();

        var result = await analyzer.AnalyzeAsync(1, missingPath, TestContext.Current.CancellationToken);

        Assert.Equal(QualityAnalysisStatus.Failed, result.Status);
        Assert.NotNull(result.FailureReason);
        Assert.Contains("音声ファイルが見つかりません", result.FailureReason, StringComparison.Ordinal);
    }
}
