namespace TrackMatch.Core.Candidates;

/// <summary>Group内Human Verdictから導出したPreferred Track状態を表す。</summary>
public enum HumanVerdictPreferredTrackStatus
{
    Unavailable,
    Resolved,
    Conflict,
}

public sealed record HumanVerdictPreferredTrackResult(HumanVerdictPreferredTrackStatus Status, long? TrackId);
