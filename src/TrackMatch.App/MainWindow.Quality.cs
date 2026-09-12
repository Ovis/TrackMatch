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

    /// <summary>
    /// Window描画完了後に、音源A/B、比較結果/音質比較、同期再生の順に詳細領域を組み替える。
    /// </summary>
    protected override void OnContentRendered(EventArgs e)
    {
        base.OnContentRendered(e);
        EnsureQualityUiAttached();
        EnsureQualityAnalysisStarted();
    }

    /// <summary>
    /// 音質比較パネルが利用可能になった時点で品質解析Controllerを初期化する。
    /// </summary>
    internal void EnsureQualityAnalysisStarted()
    {
        if (_qualityLifecycleAttached)
        {
            return;
        }

        _qualityLifecycleAttached = true;
        _qualityController = new MainWindowQualityAnalysisController(
            _viewModel,
            text =>
            {
                if (_qualityAnalysisStatusTextBlock is not null)
                {
                    _qualityAnalysisStatusTextBlock.Text = text;
                }
            });
        _viewModel.PropertyChanged += QualityViewModel_PropertyChanged;
        Closed += QualityWindow_Closed;

        if (!_viewModel.IsLoading && !_viewModel.IsAnalyzing && _viewModel.SelectedLibrary is not null)
        {
            _qualityController.Restart();
        }
    }

    /// <summary>
    /// 選択CandidateのA/Bだけを手動再解析する。
    /// </summary>
    internal Task ReanalyzeSelectedQualityAsync()
        => _qualityController?.ReanalyzeSelectedAsync() ?? Task.CompletedTask;

    private void EnsureQualityUiAttached()
    {
        if (_qualityUiAttached)
        {
            return;
        }

        var comparisonGroup = FindVisualChildren<GroupBox>(this)
            .FirstOrDefault(item => string.Equals(item.Header?.ToString(), "比較結果", StringComparison.Ordinal));
        if (comparisonGroup?.Parent is Grid detailGrid)
        {
            RearrangeCandidateDetailLayout(detailGrid, comparisonGroup);
        }

        AttachQualityProgressToFooter();
        _qualityUiAttached = true;
    }

    private void RearrangeCandidateDetailLayout(Grid detailGrid, GroupBox comparisonGroup)
    {
        var sourceGrid = detailGrid.Children
            .OfType<Grid>()
            .FirstOrDefault(item => Grid.GetRow(item) == 0);
        var playbackGroup = detailGrid.Children
            .OfType<GroupBox>()
            .FirstOrDefault(item => string.Equals(item.Header?.ToString(), "A/B 同期再生", StringComparison.Ordinal));
        var reviewGrid = detailGrid.Children
            .OfType<Grid>()
            .FirstOrDefault(item => Grid.GetRow(item) == 6);

        if (sourceGrid is null || playbackGroup is null || reviewGrid is null)
        {
            return;
        }

        ReplaceSourcePanels(sourceGrid);

        // 比較結果の各行は内容量に応じた高さだけを使う。
        // 元XAMLでは既定のStar行だったため、領域が広いほど行間が引き伸ばされていた。
        CompactComparisonResult(comparisonGroup);

        // 候補を切り替えたとき、前候補でスクロールした位置を引き継ぐとタイトルが見えなくなる。
        // 新しいA/Bを確認するときは必ず先頭から表示して、音源名を最初に確認できるようにする。
        sourceGrid.DataContextChanged += (_, _) => ScrollSourcePanelsToTop(sourceGrid);
        ScrollSourcePanelsToTop(sourceGrid);

        detailGrid.Children.Clear();
        detailGrid.RowDefinitions.Clear();
        detailGrid.ColumnDefinitions.Clear();
        detailGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        // 音源情報と比較情報をどちらもStar行にすることで、ウィンドウの高さに応じて双方が縮む。
        // 小さいウィンドウでも音源A/Bの見出しとタイトルが消えない程度の最小高だけ確保する。
        detailGrid.RowDefinitions.Add(new RowDefinition
        {
            Height = new GridLength(2, GridUnitType.Star),
            MinHeight = 120,
        });
        detailGrid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(12) });
        detailGrid.RowDefinitions.Add(new RowDefinition
        {
            Height = new GridLength(3, GridUnitType.Star),
            MinHeight = 140,
        });
        detailGrid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(12) });
        detailGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        detailGrid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(12) });
        detailGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        Grid.SetRow(sourceGrid, 0);
        Grid.SetColumn(sourceGrid, 0);
        detailGrid.Children.Add(sourceGrid);

        // 比較結果は必要な内容高だけを使って上詰めし、残りの横幅を音質比較へ割り当てる。
        // 音質比較は内部スクロールを持つため、この行が縮んでもMain Window全体を押し広げない。
        var comparisonAndQuality = new Grid();
        comparisonAndQuality.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(2, GridUnitType.Star) });
        comparisonAndQuality.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(12) });
        comparisonAndQuality.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(3, GridUnitType.Star) });
        Grid.SetRow(comparisonAndQuality, 2);

        Grid.SetRow(comparisonGroup, 0);
        Grid.SetColumn(comparisonGroup, 0);
        comparisonAndQuality.Children.Add(comparisonGroup);

        var qualityPanel = new CandidateQualityPanel();
        Grid.SetRow(qualityPanel, 0);
        Grid.SetColumn(qualityPanel, 2);
        comparisonAndQuality.Children.Add(qualityPanel);
        detailGrid.Children.Add(comparisonAndQuality);

        Grid.SetRow(playbackGroup, 4);
        Grid.SetColumn(playbackGroup, 0);
        detailGrid.Children.Add(playbackGroup);

        Grid.SetRow(reviewGrid, 6);
        Grid.SetColumn(reviewGrid, 0);
        detailGrid.Children.Add(reviewGrid);
    }

    private static void ReplaceSourcePanels(Grid sourceGrid)
    {
        var sourceA = sourceGrid.Children
            .OfType<GroupBox>()
            .FirstOrDefault(item => string.Equals(item.Header?.ToString(), "音源 A", StringComparison.Ordinal));
        var sourceB = sourceGrid.Children
            .OfType<GroupBox>()
            .FirstOrDefault(item => string.Equals(item.Header?.ToString(), "音源 B", StringComparison.Ordinal));

        if (sourceA is not null)
        {
            sourceA.Content = new CandidateSourceSummaryPanel(isTrackA: true);
        }

        if (sourceB is not null)
        {
            sourceB.Content = new CandidateSourceSummaryPanel(isTrackA: false);
        }
    }

    private static void CompactComparisonResult(GroupBox comparisonGroup)
    {
        if (comparisonGroup.Content is not Grid comparisonGrid)
        {
            return;
        }

        foreach (var row in comparisonGrid.RowDefinitions)
        {
            row.Height = GridLength.Auto;
        }

        comparisonGrid.VerticalAlignment = VerticalAlignment.Top;
        comparisonGroup.VerticalContentAlignment = VerticalAlignment.Top;
    }

    private static void ScrollSourcePanelsToTop(Grid sourceGrid)
    {
        foreach (var scrollViewer in FindVisualChildren<ScrollViewer>(sourceGrid))
        {
            scrollViewer.ScrollToTop();
        }
    }

    private void AttachQualityProgressToFooter()
    {
        if (Content is not Grid rootGrid)
        {
            return;
        }

        var footer = rootGrid.Children
            .OfType<Grid>()
            .FirstOrDefault(item => Grid.GetRow(item) == 8);
        if (footer is null)
        {
            return;
        }

        footer.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(16) });
        footer.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        _qualityAnalysisStatusTextBlock = new TextBlock
        {
            Text = "音質解析: 待機中",
            VerticalAlignment = VerticalAlignment.Center,
        };
        Grid.SetColumn(_qualityAnalysisStatusTextBlock, footer.ColumnDefinitions.Count - 1);
        footer.Children.Add(_qualityAnalysisStatusTextBlock);
    }

    private void QualityViewModel_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (_qualityController is null)
        {
            return;
        }

        if (e.PropertyName == nameof(MainWindowViewModel.IsAnalyzing) && _viewModel.IsAnalyzing)
        {
            // Libraryスキャン中に旧Candidateの重いDecodeを並行させない。
            _qualityController.Stop();
            return;
        }

        if (e.PropertyName == nameof(MainWindowViewModel.IsLoading)
            && !_viewModel.IsLoading
            && !_viewModel.IsAnalyzing)
        {
            // Candidate再読込が完了したタイミングで、現在のLibraryを対象に新しい解析Sessionへ切り替える。
            _qualityController.Restart();
        }
    }

    private void QualityWindow_Closed(object? sender, EventArgs e)
    {
        Closed -= QualityWindow_Closed;
        _viewModel.PropertyChanged -= QualityViewModel_PropertyChanged;
        _qualityController?.Dispose();
        _qualityController = null;
    }

    private static IEnumerable<T> FindVisualChildren<T>(DependencyObject root)
        where T : DependencyObject
    {
        for (var index = 0; index < VisualTreeHelper.GetChildrenCount(root); index++)
        {
            var child = VisualTreeHelper.GetChild(root, index);
            if (child is T typed)
            {
                yield return typed;
            }

            foreach (var descendant in FindVisualChildren<T>(child))
            {
                yield return descendant;
            }
        }
    }
}
