namespace TrackMatch.App.Quality;

/// <summary>
/// Candidate一覧と詳細パネルへ表示する音質比較の整形済み状態を保持する。
/// </summary>
public sealed record CandidateQualityPresentationModel(
    string ListSummary,
    string StatusText,
    string SummaryLine1,
    string SummaryLine2,
    string SummaryLine3,
    IReadOnlyList<CandidateQualityFindingViewModel> Findings,
    IReadOnlyList<CandidateQualityMeasurementRowViewModel> Measurements,
    bool CanReanalyze)
{
    /// <summary>
    /// まだ解析結果を持たないCandidate向けの初期表示を返す。
    /// </summary>
    public static CandidateQualityPresentationModel Pending { get; } = new(
        "音質: 解析待ち",
        "音質解析: 解析待ち",
        "音質解析を待っています。",
        string.Empty,
        string.Empty,
        [],
        [],
        true);
}

/// <summary>
/// 品質所見1件をUI向けに整形した表示データを保持する。
/// </summary>
public sealed record CandidateQualityFindingViewModel(string Severity, string Message);

/// <summary>
/// A/Bの技術測定値を1行で比較表示する。
/// </summary>
public sealed record CandidateQualityMeasurementRowViewModel(
    string Label,
    string ValueA,
    string ValueB,
    string? Description = null);
