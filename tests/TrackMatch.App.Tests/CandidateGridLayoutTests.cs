using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using TrackMatch.App;
using TrackMatch.App.Quality;
using Xunit;

namespace TrackMatch.App.Tests;

/// <summary>
/// 候補一覧のヘッダー右端と縦スクロールバーのレイアウトが連続していることを検証する。
/// </summary>
public sealed class CandidateGridLayoutTests
{
    [Fact]
    public void CandidateGrid_HeaderOverlayAndQualityPanelScrollerRenderCorrectly()
    {
        Exception? exception = null;
        var thread = new Thread(() =>
        {
            try
            {
                var application = CreateApplication();

                var window = new MainWindow { Width = 1480, Height = 1040 };
                window.Show();
                window.UpdateLayout();

                var candidateGrid = (DataGrid)window.FindName("CandidateGrid");
                candidateGrid.ItemsSource = Enumerable.Range(0, 100).Select(index => new CandidateRow(index));
                candidateGrid.SelectedIndex = 0;
                candidateGrid.UpdateLayout();

                var verticalScrollBar = FindVisualChildren<ScrollBar>(candidateGrid)
                    .Single(item => item.Orientation == Orientation.Vertical && item.Visibility == Visibility.Visible);
                var headerCornerOverlay = (Border)window.FindName("CandidateGridHeaderCornerOverlay");
                var verticalScrollBarPosition = verticalScrollBar.TranslatePoint(new Point(), window);
                var headerCornerOverlayPosition = headerCornerOverlay.TranslatePoint(new Point(), window);
                var headerTexts = FindVisualChildren<DataGridColumnHeader>(candidateGrid)
                    .Where(item => item.Column is not null)
                    .Select(item => item.Column!.Header)
                    .Cast<string>()
                    .ToArray();
                var headerTop = FindVisualChildren<DataGridColumnHeader>(candidateGrid)
                    .First(item => item.Column is null)
                    .TranslatePoint(new Point(), window).Y;

                window.Close();
                VerifyQualityPanelScroll();
                application.Shutdown();

                Assert.Equal(verticalScrollBar.ActualWidth, headerCornerOverlay.ActualWidth, precision: 5);
                Assert.Equal(verticalScrollBarPosition.X, headerCornerOverlayPosition.X, precision: 5);
                Assert.Equal(headerTop, headerCornerOverlayPosition.Y, precision: 5);
                Assert.IsType<CandidateRow>(candidateGrid.SelectedItem);
                Assert.Equal(["分類", "A", "B", "音響一致度", "レビュー結果"], headerTexts);
            }
            catch (Exception caught)
            {
                exception = caught;
            }
        });

        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();

        Assert.Null(exception);
    }

    private static void VerifyQualityPanelScroll()
    {
        var panel = new CandidateQualityPanel { DataContext = new QualityPanelRow() };
        var window = new Window { Content = panel, Width = 520, Height = 300 };
        window.Show();
        window.UpdateLayout();

        var scrollViewer = (ScrollViewer)panel.FindName("QualityScrollViewer");
        scrollViewer.ScrollToVerticalOffset(20);
        scrollViewer.UpdateLayout();
        var verticalOffset = scrollViewer.VerticalOffset;
        var scrollableHeight = scrollViewer.ScrollableHeight;
        var canContentScroll = scrollViewer.CanContentScroll;

        window.Close();

        Assert.False(canContentScroll);
        Assert.True(scrollableHeight > 100,
            $"スクロール領域が不足しています: extent={scrollViewer.ExtentHeight}, viewport={scrollViewer.ViewportHeight}, scrollable={scrollableHeight}");
        Assert.Equal(20, verticalOffset, precision: 5);
    }

    private static System.Windows.Application CreateApplication()
    {
        var application = new System.Windows.Application
        {
            ShutdownMode = ShutdownMode.OnExplicitShutdown,
        };
        application.Resources.MergedDictionaries.Add(new ResourceDictionary
        {
            Source = new Uri("/TrackMatch.App;component/Styles/TrackMatchTheme.xaml", UriKind.Relative),
        });
        application.Resources.MergedDictionaries.Add(new ResourceDictionary
        {
            Source = new Uri("/TrackMatch.App;component/Styles/ScrollBars.xaml", UriKind.Relative),
        });
        application.Resources.MergedDictionaries.Add(new ResourceDictionary
        {
            Source = new Uri("/TrackMatch.App;component/Styles/DataGridCorrections.xaml", UriKind.Relative),
        });
        return application;
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

    private sealed record CandidateRow(int Index)
    {
        public string Kind => "候補";
        public string TitleA => $"A {Index}";
        public string TitleB => $"B {Index}";
        public string Similarity => "99%";
        public string ReviewResult => "未レビュー";
    }

    private sealed class QualityPanelRow
    {
        public string QualityStatusText => "音質解析: 完了";
        public string QualitySummaryLine1 => "音質比較の概要";
        public string QualitySummaryLine2 => "詳細な測定値を確認できます。";
        public string QualitySummaryLine3 => "スクロールの動作確認用データです。";
        public IReadOnlyList<CandidateQualityFindingViewModel> QualityFindings => Enumerable.Range(0, 20)
            .Select(index => new CandidateQualityFindingViewModel("注意", $"確認事項 {index}"))
            .ToArray();
        public IReadOnlyList<CandidateQualityMeasurementRowViewModel> QualityMeasurements => Enumerable.Range(0, 20)
            .Select(index => new CandidateQualityMeasurementRowViewModel($"測定値 {index}", "A", "B"))
            .ToArray();
        public bool CanReanalyzeQuality => true;
    }
}
