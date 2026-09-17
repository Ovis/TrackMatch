namespace TrackMatch.Core.Candidates;

/// <summary>移動・リネーム・内容変更などのファイル操作後にHuman Verdictをどう扱うかを定義する。</summary>
public static class HumanVerdictFileOperationPolicy
{
    /// <summary>Track IdentityとContentが維持された移動・リネームではHuman Verdictを維持する。</summary>
    public static bool KeepsVerdictForPathOnlyChange() => true;

    /// <summary>Content Verification FailureではHuman VerdictをCurrentから外す。</summary>
    public static bool InvalidatesVerdictForContentFailure() => true;
}
