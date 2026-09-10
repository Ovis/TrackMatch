namespace TrackMatch.Core.Candidates;

/// <summary>
/// 候補ペアに対する人手レビュー結果を表す。
/// </summary>
public sealed record CandidateReview(
    CandidatePairKey Pair,
    CandidateReviewDecision Decision,
    string? Note);
