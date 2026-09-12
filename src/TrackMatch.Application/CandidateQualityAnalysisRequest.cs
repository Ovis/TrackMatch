using TrackMatch.Core.Candidates;

namespace TrackMatch.Application;

/// <summary>
/// Candidate一致区間の相対品質解析に必要な比較情報とA/B音声Pathを保持する。
/// </summary>
public sealed record CandidateQualityAnalysisRequest(
    CandidateComparison Candidate,
    string PathA,
    string PathB);
