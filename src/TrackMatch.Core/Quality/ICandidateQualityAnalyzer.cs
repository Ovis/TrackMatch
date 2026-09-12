using TrackMatch.Core.Candidates;

namespace TrackMatch.Core.Quality;

/// <summary>
/// Candidateの一致区間を使ってA/B間の相対的な音量・ダイナミクス差を解析する。
/// </summary>
public interface ICandidateQualityAnalyzer
{
    /// <summary>
    /// Candidateの一致区間とTrack単体解析結果から相対品質比較を生成する。
    /// </summary>
    /// <param name="candidate">Chromaprint詳細比較で確定した一致区間と相対Offset</param>
    /// <param name="pathA">Track Aの音声ファイル</param>
    /// <param name="pathB">Track Bの音声ファイル</param>
    /// <param name="analysisA">Track Aの単体品質解析結果</param>
    /// <param name="analysisB">Track Bの単体品質解析結果</param>
    Task<CandidateQualityComparison> AnalyzeAsync(
        CandidateComparison candidate,
        string pathA,
        string pathB,
        TrackQualityAnalysis analysisA,
        TrackQualityAnalysis analysisB,
        CancellationToken cancellationToken = default);
}
