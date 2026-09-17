namespace TrackMatch.Core.Candidates;

/// <summary>Human Verdictを維持したまま再確認を促す派生状態を表す。</summary>
public enum HumanVerdictReevaluationState
{
    /// <summary>再確認を要求する新しい根拠はない。</summary>
    Current,

    /// <summary>機械解析結果の変化等により再確認を推奨するが、Human Verdictは有効なまま維持する。</summary>
    Recommended,
}
