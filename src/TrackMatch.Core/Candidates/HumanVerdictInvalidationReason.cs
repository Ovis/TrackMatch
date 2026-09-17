namespace TrackMatch.Core.Candidates;

/// <summary>Human Verdictを履歴へ退避しCurrentから外す理由を表す。</summary>
public enum HumanVerdictInvalidationReason
{
    /// <summary>Trackの音声内容が変更され、過去の音声比較判断を適用できない。</summary>
    ContentChanged,

    /// <summary>Trackの恒久削除によりCurrent Pairを維持できない。</summary>
    TrackDeleted,
}
