namespace TrackMatch.Core.Candidates;

/// <summary>ごみ箱移動を実行できるHuman Verdict派生状態かを判定する。</summary>
public static class HumanVerdictTrashPolicy
{
    /// <summary>ConflictまたはPreferred未確定では破壊的処置を許可しない。</summary>
    public static bool CanExecute(long? preferredTrackId, HumanVerdictConflictKind conflictKind)
        => preferredTrackId is not null && conflictKind == HumanVerdictConflictKind.None;
}
