namespace TrackMatch.Core.Candidates;

/// <summary>Human Verdict Conflictが存在するGroupで許可する操作を定義する。</summary>
public static class HumanVerdictConflictResolutionPolicy
{
    public static bool CanPerformDestructiveAction(HumanVerdictConflictKind kind) => kind == HumanVerdictConflictKind.None;
    public static bool CanEditHumanVerdict(HumanVerdictConflictKind kind) => true;
}
