using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
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
        ApplyCandidateGridHeaderFillerBackground();
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

    /// <summary>
    /// 選択中Candidateの音質情報を手動で再解析する。
    /// </summary>
    private async void ReanalyzeQuality_Click(object sender, RoutedEventArgs e)
    {
        await ReanalyzeSelectedQualityAsync();
    }

    private void EnsureQualityUiAttached()
    {
        if (_qualityUiAttached) return;

        var comparisonGroup = FindVisualChildren<GroupBox>(this)
            .FirstOrDefault(item => string.Equals(item.Header?.ToString(), "比較結果", StringComparison.Ordinal));
        var detailGrid = comparisonGroup is null ? null : FindCandidateDetailGrid(comparisonGroup);
        if (comparisonGroup is not null && detailGrid is not null)
        {
            RearrangeCandidateDetailLayout(detailGrid, comparisonGroup);
        }

        AttachQualityProgressToFooter();
        _qualityUiAttached = true;
    }

    /// <summary>
    /// UI刷新後は比較結果が中間Gridの内側へ入ったため、即親ではなく同期再生を持つ祖先Gridを詳細領域として特定する。
    /// </summary>
    private static Grid? FindCandidateDetailGrid(DependencyObject source)
    {
        for (var current = VisualTreeHelper.GetParent(source); current is not null; current = VisualTreeHelper.GetParent(current))
        {
            if (current is not Grid grid)
            {
                continue;
            }

            if (grid.Children.OfType<GroupBox>()
                .Any(item => string.Equals(item.Header?.ToString(), "A/B 同期再生", StringComparison.Ordinal)))
            {
                return grid;
            }
        }

        return null;
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

        // comparisonGroupは既存の中間Gridから外してから新しい比較領域へ付け替える。
        if (comparisonGroup.Parent is Panel previousParent)
        {
            previousParent.Children.Remove(comparisonGroup);
        }
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
        if (comparisonGroup.Content is not Panel comparisonPanel) return;
        comparisonPanel.VerticalAlignment = VerticalAlignment.Top;
        comparisonGroup.VerticalContentAlignment = VerticalAlignment.Top;
    }

    private static void ScrollSourcePanelsToTop(Grid sourceGrid)
    {
        foreach (var scrollViewer in FindVisualChildren<ScrollViewer>(sourceGrid)) scrollViewer.ScrollToTop();
    }

    /// <summary>
    /// DataGridがVertical ScrollBar用に確保する列ヘッダー右端のFillerを、通常の列ヘッダーと同じ背景色へ揃える。
    /// </summary>
    private void ApplyCandidateGridHeaderFillerBackground()
    {
        var candidateGrid = FindVisualChildren<DataGrid>(this).FirstOrDefault();
        if (candidateGrid is null) return;

        // 共通DataGridColumnHeaderと同じ色を直接使い、Filler専用Resourceを増やさない。
        var headerBackground = new SolidColorBrush(Color.FromRgb(0xF3, 0xF6, 0xF9));
        var borderBrush = (Brush)FindResource("BorderBrush");
        foreach (var header in FindVisualChildren<DataGridColumnHeader>(candidateGrid))
        {
            if (header.Column is null)
            {
                header.Background = headerBackground;
                header.BorderBrush = borderBrush;
            }
        }
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
