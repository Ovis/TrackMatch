namespace TrackMatch.Core.Candidates;

/// <summary>Human Verdictの履歴表示に必要な監査情報を表す。</summary>
public sealed record HumanVerdictAuditEntry(
    CandidateReview Verdict,
    HumanVerdictSource Source,
    DateTime ChangedAtUtc,
    HumanVerdictHistoryChangeKind ChangeKind,
    HumanVerdictInvalidationReason? InvalidationReason);
