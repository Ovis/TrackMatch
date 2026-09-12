using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using TrackMatch.App.Quality;

namespace TrackMatch.App;

/// <summary>
/// Main Windowへ品質解析のUI・ライフサイクル・手動再解析操作を接続する。
/// </summary>
public partial class MainWindow
{
    private MainWindowQualityAnalysisController? _qualityController;
    private TextBlock? _qualityAnalysisStatusTextBlock;
    private bool _qualityLifecycleAttached;
    private bool _qualityUiAttached;

    protected override void OnContentRendered(EventArgs e)
    {
        base.OnContentRendered(e);
        EnsureQualityUiAttached();
        EnsureQualityAnalysisStarted();
    }

    internal void EnsureQualityAnalysisStarted()
    {
        if (_qualityLifecycleAttached) return;
        _qualityLifecycleAttached = true;
        _qualityController = new MainWindowQualityAnalysisController(_viewModel, text =>
        {
            if (_qualityAnalysisStatusTextBlock is not null) _qualityAnalysisStatusTextBlock.Text = text;
        });
        _viewModel.PropertyChanged += QualityViewModel_PropertyChanged;
        Closed += QualityWindow_Closed;
        if (!_viewModel.IsLoading && !_viewModel.IsAnalyzing && _viewModel.SelectedLibrary is not null) _qualityController.Restart();
    }

    internal Task ReanalyzeSelectedQualityAsync() => _qualityController?.ReanalyzeSelectedAsync() ?? Task.CompletedTask;

    private void EnsureQualityUiAttached()
    {
        if (_qualityUiAttached) return;
        var comparisonGroup = FindVisualChildren<GroupBox>(this).FirstOrDefault(item => string.Equals(item.Header?.ToString(), "比較結果", StringComparison.Ordinal));
        if (comparisonGroup?.Parent is Grid detailGrid) RearrangeCandidateDetailLayout(detailGrid, comparisonGroup);
        AttachQualityProgressToFooter();
        _qualityUiAttached = true;
    }

    private void RearrangeCandidateDetailLayout(Grid detailGrid, GroupBox comparisonGroup)
    {
        var sourceGrid = detailGrid.Children.OfType<Grid>().FirstOrDefault(item => Grid.GetRow(item) == 0);
        var playbackGroup = detailGrid.Children.OfType<GroupBox>().FirstOrDefault(item => string.Equals(item.Header?.ToString(), "A/B 同期再生", StringComparison.Ordinal));
        var reviewGrid = detailGrid.Children.OfType<Grid>().FirstOrDefault(item => Grid.GetRow(item) == 6);
        if (sourceGrid is null || playbackGroup is null || reviewGrid is null) return;

        ReplaceSourcePanels(sourceGrid);
        CompactComparisonResult(comparisonGroup);
        sourceGrid.DataContextChanged += (_, _) => ScrollSourcePanelsToTop(sourceGrid);
        ScrollSourcePanelsToTop(sourceGrid);

        detailGrid.Children.Clear();
        detailGrid.RowDefinitions.Clear();
        detailGrid.ColumnDefinitions.Clear();
        detailGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        detailGrid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(2, GridUnitType.Star), MinHeight = 120 });
        detailGrid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(12) });
        detailGrid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(3, GridUnitType.Star), MinHeight = 140 });
        detailGrid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(12) });
        detailGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        detailGrid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(12) });
        detailGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        Grid.SetRow(sourceGrid, 0);
        Grid.SetColumn(sourceGrid, 0);
        detailGrid.Children.Add(sourceGrid);

        var comparisonAndQuality = new Grid();
        comparisonAndQuality.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(2, GridUnitType.Star) });
        comparisonAndQuality.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(12) });
        comparisonAndQuality.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(3, GridUnitType.Star) });
        Grid.SetRow(comparisonAndQuality, 2);
        Grid.SetRow(comparisonGroup, 0);
        Grid.SetColumn(comparisonGroup, 0);
        comparisonAndQuality.Children.Add(comparisonGroup);
        var qualityPanel = new CandidateQualityPanel();
        Grid.SetColumn(qualityPanel, 2);
        comparisonAndQuality.Children.Add(qualityPanel);
        detailGrid.Children.Add(comparisonAndQuality);
        Grid.SetRow(playbackGroup, 4);
        detailGrid.Children.Add(playbackGroup);
        Grid.SetRow(reviewGrid, 6);
        detailGrid.Children.Add(reviewGrid);
    }

    private static void ReplaceSourcePanels(Grid sourceGrid)
    {
        var sourceA = sourceGrid.Children.OfType<GroupBox>().FirstOrDefault(item => string.Equals(item.Header?.ToString(), "音源 A", StringComparison.Ordinal));
        var sourceB = sourceGrid.Children.OfType<GroupBox>().FirstOrDefault(item => string.Equals(item.Header?.ToString(), "音源 B", StringComparison.Ordinal));
        if (sourceA is not null) sourceA.Content = new CandidateSourceSummaryPanel(isTrackA: true);
        if (sourceB is not null) sourceB.Content = new CandidateSourceSummaryPanel(isTrackA: false);
    }

    private static void CompactComparisonResult(GroupBox comparisonGroup)
    {
        if (comparisonGroup.Content is not Grid comparisonGrid) return;
        foreach (var row in comparisonGrid.RowDefinitions) row.Height = GridLength.Auto;
        comparisonGrid.VerticalAlignment = VerticalAlignment.Top;
        comparisonGroup.VerticalContentAlignment = VerticalAlignment.Top;
    }

    private static void ScrollSourcePanelsToTop(Grid sourceGrid)
    {
        foreach (var scrollViewer in FindVisualChildren<ScrollViewer>(sourceGrid)) scrollViewer.ScrollToTop();
    }

    private void AttachQualityProgressToFooter()
    {
        if (Content is not Grid rootGrid) return;
        // 新UIではFooterをBorderで包むため、旧実装の「Row 8直下のGrid」前提を外す。
        var footerBorder = rootGrid.Children.OfType<Border>().FirstOrDefault(item => Grid.GetRow(item) == 8);
        if (footerBorder?.Child is not Grid footer) return;
        footer.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(18) });
        footer.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        _qualityAnalysisStatusTextBlock = new TextBlock { Text = "音質解析: 待機中", VerticalAlignment = VerticalAlignment.Center };
        Grid.SetColumn(_qualityAnalysisStatusTextBlock, footer.ColumnDefinitions.Count - 1);
        footer.Children.Add(_qualityAnalysisStatusTextBlock);
    }

    private void QualityViewModel_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (_qualityController is null) return;
        if (e.PropertyName == nameof(MainWindowViewModel.IsAnalyzing) && _viewModel.IsAnalyzing) { _qualityController.Stop(); return; }
        if (e.PropertyName == nameof(MainWindowViewModel.IsLoading) && !_viewModel.IsLoading && !_viewModel.IsAnalyzing) _qualityController.Restart();
    }

    private void QualityWindow_Closed(object? sender, EventArgs e)
    {
        Closed -= QualityWindow_Closed;
        _viewModel.PropertyChanged -= QualityViewModel_PropertyChanged;
        _qualityController?.Dispose();
        _qualityController = null;
    }

    private static IEnumerable<T> FindVisualChildren<T>(DependencyObject root) where T : DependencyObject
    {
        for (var index = 0; index < VisualTreeHelper.GetChildrenCount(root); index++)
        {
            var child = VisualTreeHelper.GetChild(root, index);
            if (child is T typed) yield return typed;
            foreach (var descendant in FindVisualChildren<T>(child)) yield return descendant;
        }
    }
}
