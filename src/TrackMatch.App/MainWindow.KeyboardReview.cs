using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using TrackMatch.Core.Candidates;

namespace TrackMatch.App;

public partial class MainWindow
{
    /// <summary>
    /// XAML側の通常操作を変えず、MainWindowへレビュー用キーボード操作を追加する。
    /// </summary>
    [ModuleInitializer]
    internal static void RegisterReviewKeyboardHandlers()
    {
        EventManager.RegisterClassHandler(
            typeof(MainWindow),
            Keyboard.PreviewKeyDownEvent,
            new KeyEventHandler(MainWindow_ReviewPreviewKeyDown),
            handledEventsToo: true);
        EventManager.RegisterClassHandler(
            typeof(MainWindow),
            ButtonBase.ClickEvent,
            new RoutedEventHandler(MainWindow_ReviewButtonClick),
            handledEventsToo: true);
        EventManager.RegisterClassHandler(
            typeof(MainWindow),
            FrameworkElement.LoadedEvent,
            new RoutedEventHandler(MainWindow_ReviewShortcutLoaded));
    }

    private static async void MainWindow_ReviewPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (sender is not MainWindow window || e.Handled || IsReviewShortcutBlockedByFocus())
        {
            return;
        }

        var modifiers = Keyboard.Modifiers;
        try
        {
            if (modifiers == ModifierKeys.Control && e.Key == Key.D1)
            {
                e.Handled = true;
                await window.ExecuteKeyboardReviewAsync(CandidateReviewDecision.NotDuplicate, keepTrackId: null);
                return;
            }

            if (modifiers == ModifierKeys.Control && e.Key == Key.D2)
            {
                e.Handled = true;
                await window.ExecuteKeyboardReviewAsync(CandidateReviewDecision.ConfirmedDuplicate, window._viewModel.SelectedCandidate?.TrackIdA);
                return;
            }

            if (modifiers == ModifierKeys.Control && e.Key == Key.D3)
            {
                e.Handled = true;
                await window.ExecuteKeyboardReviewAsync(CandidateReviewDecision.ConfirmedDuplicate, window._viewModel.SelectedCandidate?.TrackIdB);
                return;
            }

            if (modifiers == ModifierKeys.Control && e.Key == Key.Z)
            {
                e.Handled = true;
                await window._viewModel.UndoLastReviewAsync();
                return;
            }

            if (modifiers != ModifierKeys.None)
            {
                return;
            }

            if (e.Key == Key.Space)
            {
                e.Handled = true;
                window._viewModel.Playback.TogglePlayPause();
                return;
            }

            if (e.Key is Key.Up or Key.Down)
            {
                e.Handled = window.MoveCandidateSelection(e.Key == Key.Down ? 1 : -1);
            }
        }
        catch (Exception exception) when (exception is IOException or InvalidDataException or InvalidOperationException or ArgumentException)
        {
            window.ShowReviewError(exception);
        }
    }

    private static async void MainWindow_ReviewButtonClick(object sender, RoutedEventArgs e)
    {
        if (sender is not MainWindow window || e.Handled || e.Source is not Button button || button.Content is not string label)
        {
            return;
        }

        // 既存Click Handlerを迂回すると確認Dialogの安全策が失われるため、レビュー3操作だけを同じ処理経路へ明示的に流す。
        if (label.StartsWith("重複ではない", StringComparison.Ordinal))
        {
            e.Handled = true;
            await window._viewModel.ExecuteReviewWithUndoAsync(
                () => window.ExecuteReviewActionAsync(CandidateReviewDecision.NotDuplicate, window._viewModel.MarkNotDuplicateAsync));
            return;
        }

        var selected = window._viewModel.SelectedCandidate;
        if (selected is null)
        {
            return;
        }

        if (label.StartsWith("重複 / Aを残す", StringComparison.Ordinal))
        {
            e.Handled = true;
            await window._viewModel.ExecuteReviewWithUndoAsync(
                () => window.ConfirmDuplicateWithImpactAsync(selected.TrackIdA, window._viewModel.ConfirmDuplicateKeepAAsync));
        }
        else if (label.StartsWith("重複 / Bを残す", StringComparison.Ordinal))
        {
            e.Handled = true;
            await window._viewModel.ExecuteReviewWithUndoAsync(
                () => window.ConfirmDuplicateWithImpactAsync(selected.TrackIdB, window._viewModel.ConfirmDuplicateKeepBAsync));
        }
    }

    private static void MainWindow_ReviewShortcutLoaded(object sender, RoutedEventArgs e)
    {
        if (sender is not MainWindow window || !ReferenceEquals(e.Source, window))
        {
            return;
        }

        // 操作を覚えなくても発見できるよう、既存ボタンの文言へショートカットだけを追記する。
        foreach (var button in EnumerateVisualChildren<Button>(window))
        {
            if (button.Content is not string label || label.Contains("Ctrl+", StringComparison.Ordinal))
            {
                continue;
            }

            button.Content = label switch
            {
                "重複ではない" => "重複ではない  [Ctrl+1]",
                "重複 / Aを残す" => "重複 / Aを残す  [Ctrl+2]",
                "重複 / Bを残す" => "重複 / Bを残す  [Ctrl+3]",
                _ => button.Content,
            };
        }
    }

    private async Task ExecuteKeyboardReviewAsync(CandidateReviewDecision decision, long? keepTrackId)
    {
        if (!_viewModel.CanReview || _viewModel.SelectedCandidate is null)
        {
            return;
        }

        if (decision == CandidateReviewDecision.NotDuplicate)
        {
            await _viewModel.ExecuteReviewWithUndoAsync(
                () => ExecuteReviewActionAsync(decision, _viewModel.MarkNotDuplicateAsync));
            return;
        }

        if (keepTrackId is not { } keep)
        {
            return;
        }

        var action = keep == _viewModel.SelectedCandidate.TrackIdA
            ? _viewModel.ConfirmDuplicateKeepAAsync
            : _viewModel.ConfirmDuplicateKeepBAsync;
        await _viewModel.ExecuteReviewWithUndoAsync(() => ConfirmDuplicateWithImpactAsync(keep, action));
    }

    private bool MoveCandidateSelection(int offset)
    {
        var selected = _viewModel.SelectedCandidate;
        if (selected is null || _viewModel.Candidates.Count == 0)
        {
            return false;
        }

        var currentIndex = _viewModel.Candidates.IndexOf(selected);
        if (currentIndex < 0)
        {
            return false;
        }

        var nextIndex = Math.Clamp(currentIndex + offset, 0, _viewModel.Candidates.Count - 1);
        if (nextIndex == currentIndex)
        {
            return false;
        }

        _viewModel.SelectedCandidate = _viewModel.Candidates[nextIndex];
        return true;
    }

    private static bool IsReviewShortcutBlockedByFocus()
    {
        var focused = Keyboard.FocusedElement;
        return focused is TextBoxBase
            or PasswordBox
            or ComboBox
            or Slider
            or ButtonBase
            or MenuItem;
    }

    private static IEnumerable<T> EnumerateVisualChildren<T>(DependencyObject root)
        where T : DependencyObject
    {
        for (var index = 0; index < VisualTreeHelper.GetChildrenCount(root); index++)
        {
            var child = VisualTreeHelper.GetChild(root, index);
            if (child is T match)
            {
                yield return match;
            }

            foreach (var descendant in EnumerateVisualChildren<T>(child))
            {
                yield return descendant;
            }
        }
    }
}
