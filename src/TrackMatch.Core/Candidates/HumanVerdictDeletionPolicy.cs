namespace TrackMatch.Core.Candidates;

/// <summary>Human Verdictからファイル削除候補として安全に扱えるTrackかを判定する。</summary>
public static class HumanVerdictDeletionPolicy
{
    /// <summary>Conflictがなく、一意なPreferred Track以外だけを削除候補として扱える。</summary>
    public static bool CanRejectTrack(long trackId, long? preferredTrackId, bool hasConflict)
        => !hasConflict && preferredTrackId is { } preferred && trackId != preferred;
}
