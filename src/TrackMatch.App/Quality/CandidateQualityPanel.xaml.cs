using System.Windows;
using System.Windows.Controls;

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
}
