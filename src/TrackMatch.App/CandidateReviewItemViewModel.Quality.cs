using System.ComponentModel;
using System.Runtime.CompilerServices;
using TrackMatch.App.Quality;
using TrackMatch.Core.Quality;

namespace TrackMatch.App;

/// <summary>
/// Candidateレビュー項目へバックグラウンド音質解析の表示状態を付加する。
/// </summary>
public sealed partial class CandidateReviewItemViewModel : INotifyPropertyChanged
{
    private CandidateQualityPresentationModel _quality = CandidateQualityPresentationModel.Pending;

    /// <summary>
    /// Candidate一覧向けの短い音質比較要約を取得する。
    /// </summary>
    public string QualityListSummary => _quality.ListSummary;

    /// <summary>
    /// 詳細パネルの解析状態を取得する。
    /// </summary>
    public string QualityStatusText => _quality.StatusText;

    public string QualitySummaryLine1 => _quality.SummaryLine1;

    public string QualitySummaryLine2 => _quality.SummaryLine2;

    public string QualitySummaryLine3 => _quality.SummaryLine3;

    public IReadOnlyList<CandidateQualityFindingViewModel> QualityFindings => _quality.Findings;

    public IReadOnlyList<CandidateQualityMeasurementRowViewModel> QualityMeasurements => _quality.Measurements;

    public bool CanReanalyzeQuality => _quality.CanReanalyze;

    public event PropertyChangedEventHandler? PropertyChanged;

    /// <summary>
    /// 最新のTrack単体解析・Candidate比較結果をUI表示へ反映する。
    /// </summary>
    public void ApplyQualityAnalysis(
        TrackQualityAnalysis? analysisA,
        TrackQualityAnalysis? analysisB,
        CandidateQualityComparison? comparison)
    {
        _quality = CandidateQualityPresentationFormatter.Format(analysisA, analysisB, comparison);
        NotifyQualityPropertiesChanged();
    }

    /// <summary>
    /// 再解析開始前など、Candidateの品質表示を解析中へ戻す。
    /// </summary>
    public void MarkQualityAnalyzing()
    {
        _quality = CandidateQualityPresentationModel.Pending with
        {
            ListSummary = "音質: 解析中",
            StatusText = "音質解析: 解析中",
            SummaryLine1 = "音質を解析しています。候補レビューはそのまま利用できます。",
        };
        NotifyQualityPropertiesChanged();
    }

    private void NotifyQualityPropertiesChanged()
    {
        OnQualityPropertyChanged(nameof(QualityListSummary));
        OnQualityPropertyChanged(nameof(QualityStatusText));
        OnQualityPropertyChanged(nameof(QualitySummaryLine1));
        OnQualityPropertyChanged(nameof(QualitySummaryLine2));
        OnQualityPropertyChanged(nameof(QualitySummaryLine3));
        OnQualityPropertyChanged(nameof(QualityFindings));
        OnQualityPropertyChanged(nameof(QualityMeasurements));
        OnQualityPropertyChanged(nameof(CanReanalyzeQuality));
    }

    private void OnQualityPropertyChanged([CallerMemberName] string? propertyName = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
}
