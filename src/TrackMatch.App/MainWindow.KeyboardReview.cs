using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
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
                await window.ExecuteKeyboardReviewAsync(CandidateReviewDecision.NotDuplicate, preferredTrackId: null);
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

    private async Task ExecuteKeyboardReviewAsync(CandidateReviewDecision decision, long? preferredTrackId)
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

        if (preferredTrackId is not { } preferred)
        {
            return;
        }

        Func<Task> action = preferred == _viewModel.SelectedCandidate.TrackIdA
            ? _viewModel.ConfirmDuplicateKeepAAsync
            : _viewModel.ConfirmDuplicateKeepBAsync;
        await _viewModel.ExecuteReviewWithUndoAsync(() => ConfirmDuplicateWithImpactAsync(preferred, action));
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
}
