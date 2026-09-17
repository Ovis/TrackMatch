namespace TrackMatch.Core.Candidates;

/// <summary>Trackが一時的にMissingとなった場合のHuman Verdict規則を定義する。</summary>
public static class HumanVerdictMissingTrackPolicy
{
    /// <summary>MissingはContent変更の証拠ではないためHuman Verdict自体はCurrentに維持する。</summary>
    public static bool KeepsCurrentVerdict() => true;

    /// <summary>Missing Trackを含む派生Groupは物理処置対象から外す。</summary>
    public static bool ExcludesFromMaterializedGroup() => true;
}
