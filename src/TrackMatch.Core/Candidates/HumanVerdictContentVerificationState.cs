namespace TrackMatch.Core.Candidates;

/// <summary>ファイル操作後にHuman Verdictの適用可否を判断するContent Verification状態を表す。</summary>
public enum HumanVerdictContentVerificationState
{
    /// <summary>現在のTrack Contentに対してHuman Verdictを適用できる。</summary>
    Verified,

    /// <summary>内容同一性を確認できず、Human VerdictをCurrentから外す必要がある。</summary>
    Failed,
}
