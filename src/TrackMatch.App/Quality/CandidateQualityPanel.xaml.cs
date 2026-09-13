using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace TrackMatch.App.Quality;

/// <summary>
/// Candidate音質比較の表示と、選択A/Bの手動再解析Interactionを扱う。
/// </summary>
public partial class CandidateQualityPanel : UserControl
{
    public CandidateQualityPanel()
    {
        InitializeComponent();
    }

    private void CandidateQualityPanel_Loaded(object sender, RoutedEventArgs e)
    {
        if (Window.GetWindow(this) is MainWindow window)
        {
            window.EnsureQualityAnalysisStarted();
        }
    }

    private async void Reanalyze_Click(object sender, RoutedEventArgs e)
    {
        if (Window.GetWindow(this) is MainWindow window)
        {
            await window.ReanalyzeSelectedQualityAsync();
        }
    }

    /// <summary>
    /// 内部スクロールを持たない測定値Grid上でも、音質比較全体のScrollViewerをホイール操作できるようにする。
    /// </summary>
    private void QualityMeasurements_PreviewMouseWheel(object sender, MouseWheelEventArgs e)
    {
        // DataGridは自身に縦ScrollBarがなくてもMouseWheelを処理するため、
        // そのままでは親ScrollViewerへスクロール操作が伝わらない。
        QualityScrollViewer.ScrollToVerticalOffset(QualityScrollViewer.VerticalOffset - e.Delta);
        e.Handled = true;
    }
}
