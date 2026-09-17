namespace TrackMatch.Core.Candidates;

/// <summary>Human Verdict Historyへ退避した契機を表す。</summary>
public enum HumanVerdictHistoryChangeKind
{
    UserChanged,
    UserCleared,
    Invalidated,
    Undo,
}
