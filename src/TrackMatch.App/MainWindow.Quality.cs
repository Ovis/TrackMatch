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
    private Grid? _candidateDetailGrid;
    private Border? _candidateEmptyState;
    private TextBlock? _candidateEmptyStateTitle;
    private TextBlock? _candidateEmptyStateDescription;
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
            AttachCandidateEmptyState(detailGrid);
        }

        AttachQualityProgressToFooter();
        _qualityUiAttached = true;
    }

    /// <summary>
    /// 候補未選択時は空の詳細コントロールを残さず、次の操作が分かる空状態表示へ切り替える。
    /// </summary>
    private void AttachCandidateEmptyState(Grid detailGrid)
    {
        if (detailGrid.Parent is not Grid workspaceGrid)
        {
            return;
        }

        _candidateDetailGrid = detailGrid;
        _candidateEmptyStateTitle = new TextBlock
        {
            FontSize = 20,
            FontWeight = FontWeights.SemiBold,
            HorizontalAlignment = HorizontalAlignment.Center,
            TextAlignment = TextAlignment.Center,
        };
        _candidateEmptyStateDescription = new TextBlock
        {
            Margin = new Thickness(0, 10, 0, 0),
            MaxWidth = 520,
            Foreground = (Brush)FindResource("TextSecondaryBrush"),
            HorizontalAlignment = HorizontalAlignment.Center,
            TextAlignment = TextAlignment.Center,
            TextWrapping = TextWrapping.Wrap,
        };

        var content = new StackPanel
        {
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(32),
        };
        content.Children.Add(_candidateEmptyStateTitle);
        content.Children.Add(_candidateEmptyStateDescription);

        _candidateEmptyState = new Border
        {
            Background = (Brush)FindResource("CardBackgroundBrush"),
            BorderBrush = (Brush)FindResource("BorderBrush"),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(4),
            Child = content,
        };

        // 詳細ペインと同じセルへ重ねることで、候補が現れたときに既存レイアウトを一切動かさず復帰できる。
        Grid.SetRow(_candidateEmptyState, Grid.GetRow(detailGrid));
        Grid.SetRowSpan(_candidateEmptyState, Grid.GetRowSpan(detailGrid));
        Grid.SetColumn(_candidateEmptyState, Grid.GetColumn(detailGrid));
        Grid.SetColumnSpan(_candidateEmptyState, Grid.GetColumnSpan(detailGrid));
        Panel.SetZIndex(_candidateEmptyState, 1);
        workspaceGrid.Children.Add(_candidateEmptyState);
        UpdateCandidateDetailState();
    }

    /// <summary>
    /// 候補の有無と選択状態に応じて詳細ペインと案内文を切り替える。
    /// </summary>
    private void UpdateCandidateDetailState()
    {
        if (_candidateDetailGrid is null || _candidateEmptyState is null
            || _candidateEmptyStateTitle is null || _candidateEmptyStateDescription is null)
        {
            return;
        }

        var hasSelection = _viewModel.SelectedCandidate is not null;
        _candidateDetailGrid.Visibility = hasSelection ? Visibility.Visible : Visibility.Collapsed;
        _candidateEmptyState.Visibility = hasSelection ? Visibility.Collapsed : Visibility.Visible;
        if (hasSelection)
        {
            return;
        }

        if (_viewModel.Candidates.Count == 0)
        {
            _candidateEmptyStateTitle.Text = "比較する候補がありません";
            _candidateEmptyStateDescription.Text = "「スキャン・分析」を実行するか、レビュー対象の一致度下限やタブを確認してください。";
            return;
        }

        _candidateEmptyStateTitle.Text = "候補を選択してください";
        _candidateEmptyStateDescription.Text = "左の候補一覧から比較する音源を選択してください。";
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
        ImproveComparisonMetrics(comparisonGroup);
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

    /// <summary>
    /// 比較指標は短いラベルだけでは意味が伝わりにくいため、わずかな行間と指標説明を付加する。
    /// </summary>
    private static void ImproveComparisonMetrics(GroupBox comparisonGroup)
    {
        var descriptions = new Dictionary<string, string>
        {
            ["Aの一致範囲"] = "音源A全体のうち、音源Bとの一致区間として対応付けられた割合です。100%に近いほどAのほぼ全体が一致しています。",
            ["Bの一致範囲"] = "音源B全体のうち、音源Aとの一致区間として対応付けられた割合です。100%に近いほどBのほぼ全体が一致しています。",
            ["再生時間の近さ"] = "短い方の再生時間を長い方の再生時間で割った割合です。100%に近いほどA/Bの長さが近いことを示します。",
            ["最適オフセット"] = "A/Bの音響特徴が最もよく一致するように時間位置をずらした量です。0秒に近いほど開始位置が近いことを示します。",
            ["一致区間の長さ"] = "A/Bで音響的に一致していると判定された区間の長さです。",
        };

        foreach (var label in FindVisualChildren<TextBlock>(comparisonGroup))
        {
            if (!descriptions.TryGetValue(label.Text, out var description))
            {
                continue;
            }

            label.ToolTip = description;
            label.Margin = new Thickness(label.Margin.Left, 2, label.Margin.Right, 2);

            if (VisualTreeHelper.GetParent(label) is not Grid rowGrid)
            {
                continue;
            }

            var row = Grid.GetRow(label);
            foreach (var value in rowGrid.Children.OfType<TextBlock>().Where(item => Grid.GetRow(item) == row))
            {
                value.ToolTip = description;
                value.Margin = new Thickness(value.Margin.Left, 2, value.Margin.Right, 2);
            }
        }
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
        if (e.PropertyName == nameof(MainWindowViewModel.SelectedCandidate))
        {
            UpdateCandidateDetailState();
        }

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
