namespace TrackMatch.Core.Candidates;

/// <summary>Human Verdictから派生する競合種別を表す。</summary>
public enum HumanVerdictConflictKind
{
    None,
    DuplicateVsNotDuplicate,
    PreferredTrack,
}
