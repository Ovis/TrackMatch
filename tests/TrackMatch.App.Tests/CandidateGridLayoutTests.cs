using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Microsoft.Data.Sqlite;
using TrackMatch.App.Playback;
using TrackMatch.App.Quality;
using TrackMatch.Application;
using TrackMatch.Core.Candidates;
using TrackMatch.Core.Libraries;
using TrackMatch.Core.Playback;
using TrackMatch.Infrastructure.Persistence;
using Xunit;

namespace TrackMatch.App.Tests;

/// <summary>
/// Main Windowの候補一覧レイアウトと、処理中に状態不整合を起こさない主要UI Bindingを検証する。
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
                SynchronizationContext.SetSynchronizationContext(
                    new DispatcherSynchronizationContext(Dispatcher.CurrentDispatcher));
                var application = CreateApplication();

                using var viewModel = new MainWindowViewModel(new FakeSynchronizedPlaybackService());
                var window = new MainWindow(viewModel) { Width = 1480, Height = 1040 };
                window.Show();
                window.UpdateLayout();

                var libraryComboBox = (ComboBox)window.FindName("LibraryComboBox");
                var isEnabledBinding = BindingOperations.GetBinding(libraryComboBox, UIElement.IsEnabledProperty);
                var tagEditButton = FindVisualChildren<Button>(window)
                    .Single(item => Equals(item.Content, "曲情報を編集..."));
                var tagEditBinding = BindingOperations.GetBinding(tagEditButton, UIElement.IsEnabledProperty);
                var sourceA = FindVisualChildren<GroupBox>(window)
                    .Single(item => Equals(item.Header, "音源 A"));
                var tagEditPosition = tagEditButton.TranslatePoint(new Point(), window);
                var sourceAPosition = sourceA.TranslatePoint(new Point(), window);
                var candidateGrid = (DataGrid)window.FindName("CandidateGrid");
                candidateGrid.ItemsSource = Enumerable.Range(0, 100).Select(index => new CandidateRow(index));
                candidateGrid.SelectedIndex = 0;
                candidateGrid.UpdateLayout();

                var verticalScrollBar = FindVisualChildren<ScrollBar>(candidateGrid)
                    .Single(item => item.Orientation == Orientation.Vertical && item.Visibility == Visibility.Visible);
                var headerCornerOverlay = (Border)window.FindName("CandidateGridHeaderCornerOverlay");
                var headerCornerBottomBorder = (Border)window.FindName("CandidateGridHeaderCornerBottomBorder");
                var rightEdgeOverlay = (Border)window.FindName("CandidateGridRightEdgeOverlay");
                var verticalScrollBarPosition = verticalScrollBar.TranslatePoint(new Point(), window);
                var headerCornerOverlayPosition = headerCornerOverlay.TranslatePoint(new Point(), window);
                var verticalScrollBarWidth = verticalScrollBar.ActualWidth;
                var headerCornerOverlayWidth = headerCornerOverlay.ActualWidth;
                var scrollUnselectedRow = FindVisualChildren<DataGridRow>(candidateGrid)
                    .First(item => !item.IsSelected);
                var scrollUnselectedRowPosition = scrollUnselectedRow.TranslatePoint(new Point(), window);
                var scrollBarTrackPixel = ReadPixel(
                    RenderWindow(window),
                    (int)Math.Floor(verticalScrollBarPosition.X) + 2,
                    (int)Math.Floor(scrollUnselectedRowPosition.Y) + 2);
                var headerTexts = FindVisualChildren<DataGridColumnHeader>(candidateGrid)
                    .Where(item => item.Column is not null)
                    .Select(item => item.Column!.Header)
                    .Cast<string>()
                    .ToArray();
                var headerTop = FindVisualChildren<DataGridColumnHeader>(candidateGrid)
                    .First(item => item.Column is null)
                    .TranslatePoint(new Point(), window).Y;
                var selectedCandidate = candidateGrid.SelectedItem;

                candidateGrid.ItemsSource = Enumerable.Range(0, 3).Select(index => new CandidateRow(index));
                candidateGrid.SelectedIndex = 0;
                candidateGrid.UpdateLayout();
                var noScrollVerticalScrollBar = FindVisualChildren<ScrollBar>(candidateGrid)
                    .Single(item => item.Orientation == Orientation.Vertical);
                var noScrollGridPosition = candidateGrid.TranslatePoint(new Point(), window);
                var noScrollOverlayPosition = headerCornerOverlay.TranslatePoint(new Point(), window);
                var noScrollGridRight = noScrollGridPosition.X + candidateGrid.ActualWidth;
                var noScrollOverlayRight = noScrollOverlayPosition.X + headerCornerOverlay.ActualWidth;
                var noScrollHeaderFiller = FindVisualChildren<DataGridColumnHeader>(candidateGrid)
                    .Single(item => item.Column is null);
                var noScrollHeaderFillerPosition = noScrollHeaderFiller.TranslatePoint(new Point(), window);
                var noScrollHeaderFillerBottom = noScrollHeaderFillerPosition.Y + noScrollHeaderFiller.ActualHeight;
                var noScrollHeaderCornerBottomBorderPosition = headerCornerBottomBorder.TranslatePoint(new Point(), window);
                var noScrollRightEdgeOverlayPosition = rightEdgeOverlay.TranslatePoint(new Point(), window);
                var noScrollSelectedRow = FindVisualChildren<DataGridRow>(candidateGrid)
                    .Single(item => item.IsSelected);
                var noScrollSelectedRowPosition = noScrollSelectedRow.TranslatePoint(new Point(), window);
                var noScrollHeaderCornerFirstRowPixel = ReadPixel(
                    RenderWindow(window),
                    (int)Math.Floor(noScrollOverlayPosition.X),
                    (int)Math.Floor(noScrollSelectedRowPosition.Y) + 1);

                window.Close();
                VerifyQualityPanelScroll();
                VerifyTagEditorLayout();
                VerifyQualityRestartDoesNotBlockUi();
                application.Shutdown();

                // Library切替は候補読込・分析と同時に行うとSelectedLibraryと表示内容がずれるため、
                // 設定ボタンと同じbusy-state Bindingで操作自体を禁止する。
                Assert.NotNull(isEnabledBinding);
                Assert.Equal(nameof(MainWindowViewModel.CanManageLibraries), isEnabledBinding.Path.Path);
                Assert.NotNull(tagEditBinding);
                Assert.Equal("DataContext.CanEditTags", tagEditBinding.Path.Path);
                Assert.True(tagEditPosition.Y + tagEditButton.ActualHeight <= sourceAPosition.Y,
                    "曲情報編集ボタンが音源A/B情報欄より下に配置されています。");
                Assert.Equal(verticalScrollBarWidth, headerCornerOverlayWidth, precision: 5);
                Assert.Equal(Colors.White, scrollBarTrackPixel);
                // DataGridの外枠1px分だけScrollBar本体とOverlayのX座標がずれるため、
                // 完全一致ではなく同じ右端予約領域を覆っていることを1px許容で検証する。
                Assert.InRange(Math.Abs(verticalScrollBarPosition.X - headerCornerOverlayPosition.X), 0, 1.1);
                Assert.Equal(headerTop, headerCornerOverlayPosition.Y, precision: 5);
                Assert.IsType<CandidateRow>(selectedCandidate);
                Assert.Equal(["分類", "A", "B", "一致度", "レビュー結果"], headerTexts);
                Assert.NotEqual(Visibility.Visible, noScrollVerticalScrollBar.Visibility);
                Assert.Equal(noScrollGridRight, noScrollOverlayRight, precision: 5);
                Assert.Equal(1, headerCornerOverlay.BorderThickness.Right, precision: 5);
                Assert.Equal(noScrollGridRight, noScrollRightEdgeOverlayPosition.X, precision: 5);
                Assert.True(
                    noScrollSelectedRowPosition.X + noScrollSelectedRow.ActualWidth <= noScrollRightEdgeOverlayPosition.X,
                    "選択行が右端境界の描画領域まで到達しています。");
                Assert.Equal(Color.FromRgb(0x0F, 0x6C, 0xBD), noScrollHeaderCornerFirstRowPixel);
                Assert.Equal(candidateGrid.ActualHeight, rightEdgeOverlay.ActualHeight, precision: 5);
                Assert.False(rightEdgeOverlay.IsHitTestVisible);
                // Fillerヘッダーの下辺より下に線を置くと、DPI丸めで右端だけ1px欠ける。
                // 1pxまで重ねて、ヘッダー下端に未描画の隙間を作らないことを保証する。
                Assert.InRange(noScrollHeaderCornerBottomBorderPosition.Y - noScrollHeaderFillerBottom, -1, 0);

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

    private static void VerifyTagEditorLayout()
    {
        var directory = Path.Combine(Path.GetTempPath(), "TrackMatch.Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var pathA = Path.Combine(directory, "a.flac");
        var pathB = Path.Combine(directory, "b.flac");
        File.WriteAllBytes(pathA, TrackTagEditingServiceTests.CreateFlac());
        File.WriteAllBytes(pathB, TrackTagEditingServiceTests.CreateFlac());
        try
        {
            var databasePath = Path.Combine(directory, "trackmatch.db");
            var service = new TrackTagEditingService(databasePath);
            var (trackIdA, trackIdB) = Task.Run(async () =>
            {
                var database = new SqliteDatabase(databasePath);
                await database.InitializeAsync();
                var repository = new SqliteTrackRepository(database);
                var a = await repository.UpsertMetadataAsync(service.Read(pathA));
                var b = await repository.UpsertMetadataAsync(service.Read(pathB));
                return (a, b);
            }).GetAwaiter().GetResult();
            var candidate = new CandidateReviewItemViewModel(new CandidateReviewReportRow(
                trackIdA, trackIdB, null, null, 0.95, 0.9, 0.9, 1,
                TimeSpan.Zero, TimeSpan.FromSeconds(10), pathA, pathB,
                [], [], "Old", "Old", null, null, [], [],
                TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(10),
                100, 100, "FLAC", "FLAC", "FLAC", "FLAC",
                null, null, 44100, 44100, 16, 16, 2, 2));
            var editor = new TrackTagEditorWindow(service, candidate);
            editor.Show();
            var rows = (ItemsControl)editor.FindName("RowsItems");
            var started = DateTime.UtcNow;
            while (rows.Items.Count != 7 && DateTime.UtcNow - started < TimeSpan.FromSeconds(5))
            {
                var frame = new DispatcherFrame();
                Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.Background,
                    new Action(() => frame.Continue = false));
                Dispatcher.PushFrame(frame);
            }

            editor.UpdateLayout();
            Assert.Equal(7, rows.Items.Count);
            Assert.Equal(7, FindVisualChildren<Button>(editor).Count(item => Equals(item.Content, "←")));
            Assert.Equal(7, FindVisualChildren<Button>(editor).Count(item => Equals(item.Content, "→")));
            var firstRow = (ContentPresenter)rows.ItemContainerGenerator.ContainerFromIndex(0)!;
            var inputs = FindVisualChildren<TextBox>(firstRow).ToArray();
            Assert.Equal(2, inputs.Length);
            inputs[1].Text = "Copied";
            FindVisualChildren<Button>(firstRow).Single(item => Equals(item.Content, "←"))
                .RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Assert.Equal("Copied", inputs[0].Text);
            inputs[0].Text = "Old";
            inputs[1].Text = "Old";

            inputs[0].Text = "Changed";
            ((Button)editor.FindName("SaveButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            started = DateTime.UtcNow;
            while (editor.IsVisible && DateTime.UtcNow - started < TimeSpan.FromSeconds(5))
            {
                var frame = new DispatcherFrame();
                Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.Background,
                    new Action(() => frame.Continue = false));
                Dispatcher.PushFrame(frame);
            }
            Assert.False(editor.IsVisible);
            Assert.True(editor.SavedAny);
            Assert.Equal("Changed", service.Read(pathA).Title);
            Assert.Equal("Old", service.Read(pathB).Title);

            var secondEditor = new TrackTagEditorWindow(service, candidate);
            secondEditor.Show();
            var secondRows = (ItemsControl)secondEditor.FindName("RowsItems");
            started = DateTime.UtcNow;
            while (secondRows.Items.Count != 7 && DateTime.UtcNow - started < TimeSpan.FromSeconds(5))
            {
                var frame = new DispatcherFrame();
                Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.Background,
                    new Action(() => frame.Continue = false));
                Dispatcher.PushFrame(frame);
            }

            Assert.Equal(7, secondRows.Items.Count);
            secondEditor.UpdateLayout();
            var secondTitleRow = (ContentPresenter)secondRows.ItemContainerGenerator.ContainerFromIndex(0)!;
            FindVisualChildren<TextBox>(secondTitleRow).First().Text = "Unsaved";
            var discardPromptShown = false;
            var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(10) };
            timer.Tick += (_, _) =>
            {
                var prompt = System.Windows.Application.Current.Windows
                    .OfType<ConfirmationDialog>()
                    .FirstOrDefault(item => item.IsVisible && item.Title == "未保存の曲情報");
                if (prompt is null) return;
                discardPromptShown = true;
                timer.Stop();
                ((Button)prompt.FindName("SecondaryButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            };
            timer.Start();
            secondEditor.Close();
            timer.Stop();
            started = DateTime.UtcNow;
            while (secondEditor.IsVisible && DateTime.UtcNow - started < TimeSpan.FromSeconds(5))
            {
                var frame = new DispatcherFrame();
                Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.Background,
                    new Action(() => frame.Continue = false));
                Dispatcher.PushFrame(frame);
            }
            Assert.True(discardPromptShown);
            Assert.False(secondEditor.IsVisible);
            Assert.Equal("Changed", service.Read(pathA).Title);

            var owner = new Window { Width = 320, Height = 240 };
            owner.Show();
            foreach (var save in new[] { false, true })
            {
                var modalEditor = new TrackTagEditorWindow(service, candidate) { Owner = owner };
                var modalTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(10) };
                var discardTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(10) };
                var modalActionCompleted = false;
                var discarded = false;
                discardTimer.Tick += (_, _) =>
                {
                    var prompt = System.Windows.Application.Current.Windows
                        .OfType<ConfirmationDialog>()
                        .FirstOrDefault(item => item.IsVisible && item.Title == "未保存の曲情報");
                    if (prompt is null) return;
                    discardTimer.Stop();
                    ((Button)prompt.FindName("SecondaryButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                    discarded = true;
                };
                modalTimer.Tick += (_, _) =>
                {
                    var modalRows = (ItemsControl)modalEditor.FindName("RowsItems");
                    if (modalRows.Items.Count != 7) return;
                    modalTimer.Stop();
                    modalEditor.UpdateLayout();
                    var titleRow = (ContentPresenter)modalRows.ItemContainerGenerator.ContainerFromIndex(0)!;
                    FindVisualChildren<TextBox>(titleRow).First().Text = save ? "Modal saved" : "Modal unsaved";

                    var buttonName = save ? "SaveButton" : "CancelButton";
                    ((Button)modalEditor.FindName(buttonName)).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                    modalActionCompleted = true;
                };
                modalTimer.Start();
                if (!save) discardTimer.Start();
                modalEditor.ShowDialog();
                modalTimer.Stop();
                discardTimer.Stop();
                Assert.True(modalActionCompleted);
                if (!save) Assert.True(discarded);
                Assert.True(owner.IsEnabled, "モーダル編集画面を閉じても親ウィンドウが無効のままです。");
                Assert.False(modalEditor.IsVisible);
            }

            Assert.Equal("Modal saved", service.Read(pathA).Title);
            owner.Close();
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            Directory.Delete(directory, recursive: true);
        }
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

    private static void VerifyQualityRestartDoesNotBlockUi()
    {
        var directory = Path.Combine(Path.GetTempPath(), "TrackMatch.Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var databasePath = Path.Combine(directory, "trackmatch.db");
        try
        {
            Task.Run(() => new SqliteDatabase(databasePath).InitializeAsync()).GetAwaiter().GetResult();
            using (var blocker = new SqliteConnection($"Data Source={databasePath};Pooling=False"))
            {
                blocker.Open();
                using var transaction = blocker.BeginTransaction();
                using var command = blocker.CreateCommand();
                command.Transaction = transaction;
                command.CommandText = "CREATE TABLE RestartLockProbe (Id INTEGER);";
                command.ExecuteNonQuery();

                using var viewModel = new MainWindowViewModel(new FakeSynchronizedPlaybackService());
                viewModel.SelectedLibrary = new Library(1, "Test", []);
                using var controller = new MainWindowQualityAnalysisController(viewModel, _ => { }, databasePath);
                var stopwatch = Stopwatch.StartNew();
                controller.Restart();
                stopwatch.Stop();
                Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(1),
                    $"DB待機中の品質解析再開がUIスレッドを{stopwatch.Elapsed.TotalMilliseconds:N0}ms塞ぎました。");
                transaction.Rollback();
                var stopping = controller.StopAndWaitAsync();
                while (!stopping.IsCompleted)
                {
                    var frame = new DispatcherFrame();
                    Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.Background,
                        new Action(() => frame.Continue = false));
                    Dispatcher.PushFrame(frame);
                }

                stopping.GetAwaiter().GetResult();
            }
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            Directory.Delete(directory, recursive: true);
        }
    }

    private static IEnumerable<T> FindVisualChildren<T>(DependencyObject root) where T : DependencyObject
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

    private static RenderTargetBitmap RenderWindow(Window window)
    {
        var bitmap = new RenderTargetBitmap(
            (int)Math.Ceiling(window.ActualWidth),
            (int)Math.Ceiling(window.ActualHeight),
            96,
            96,
            PixelFormats.Pbgra32);
        bitmap.Render(window);
        return bitmap;
    }

    private static Color ReadPixel(BitmapSource bitmap, int x, int y)
    {
        var pixels = new byte[4];
        bitmap.CopyPixels(new Int32Rect(x, y, 1, 1), pixels, 4, 0);
        return Color.FromArgb(pixels[3], pixels[2], pixels[1], pixels[0]);
    }

    private sealed class FakeSynchronizedPlaybackService : ISynchronizedPlaybackService
    {
        public event EventHandler? PlaybackEnded { add { } remove { } }
        public event Action<string>? PlaybackFailed { add { } remove { } }
        public TimeSpan Position { get; private set; }
        public TimeSpan Duration { get; private set; }
        public PlaybackOffsets Offsets { get; private set; } = new(TimeSpan.Zero, TimeSpan.Zero);
        public SynchronizedPlaybackMode Mode { get; set; } = SynchronizedPlaybackMode.StereoOverlay;
        public float VolumeA { get; set; } = 1f;
        public float VolumeB { get; set; } = 1f;
        public bool IsPlaying { get; private set; }
        public bool IsPaused { get; private set; }

        public void Load(string pathA, string pathB, TimeSpan bestOffset, TimeSpan durationA, TimeSpan durationB)
        {
            Position = TimeSpan.Zero;
            Duration = TimeSpan.FromMinutes(3);
            Offsets = PlaybackOffsets.Normalize(TimeSpan.Zero, bestOffset);
        }

        public void Play() { IsPlaying = true; IsPaused = false; }
        public void Pause() { IsPlaying = false; IsPaused = true; }
        public void Stop() { IsPlaying = false; IsPaused = false; Position = TimeSpan.Zero; }
        public void Seek(TimeSpan position) => Position = position;
        public void SetOffsets(TimeSpan offsetA, TimeSpan offsetB) => Offsets = PlaybackOffsets.Normalize(offsetA, offsetB);
        public void Dispose() { }
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
