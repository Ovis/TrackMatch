using System.Windows;
using System.Windows.Media;

namespace TrackMatch.App;

/// <summary>
/// TrackMatch共通テーマで確認・通知・エラーを提示するModal Dialog。
/// </summary>
public partial class ConfirmationDialog : Window
{
    /// <summary>
    /// 共通ダイアログを生成する。
    /// </summary>
    /// <param name="title">Window title</param>
    /// <param name="heading">Dialog内で強調表示する見出し</param>
    /// <param name="message">確認理由や注意事項を説明する本文</param>
    /// <param name="primaryText">主操作Buttonに表示する文字列</param>
    /// <param name="secondaryText">副操作Buttonに表示する文字列。不要ならnull</param>
    /// <param name="tertiaryText">第3操作Buttonに表示する文字列。不要ならnull</param>
    /// <param name="kind">警告・エラー等の表示種別</param>
    public ConfirmationDialog(
        string title,
        string heading,
        string message,
        string primaryText,
        string? secondaryText = null,
        string? tertiaryText = null,
        AppDialogKind kind = AppDialogKind.Warning)
    {
        InitializeComponent();
        Title = title;
        HeadingTextBlock.Text = heading;
        MessageTextBlock.Text = message;
        PrimaryButton.Content = primaryText;
        ConfigureOptionalButton(SecondaryButton, secondaryText);
        ConfigureOptionalButton(TertiaryButton, tertiaryText);
        ApplyKind(kind);
    }

    /// <summary>
    /// ダイアログで選択された操作を取得する。
    /// </summary>
    public AppDialogResult SelectedResult { get; private set; } = AppDialogResult.None;

    private static void ConfigureOptionalButton(System.Windows.Controls.Button button, string? text)
    {
        button.Content = text ?? string.Empty;
        button.Visibility = string.IsNullOrWhiteSpace(text) ? Visibility.Collapsed : Visibility.Visible;
    }

    private void ApplyKind(AppDialogKind kind)
    {
        var (background, foreground, icon) = kind switch
        {
            AppDialogKind.Information => ("#EAF3FC", "#0F6CBD", "i"),
            AppDialogKind.Error => ("#FDE7E9", "#B42318", "×"),
            AppDialogKind.Question => ("#EAF3FC", "#0F6CBD", "?"),
            _ => ("#FFF4CE", "#8A5A00", "!"),
        };

        IconBackground.Background = (Brush)new BrushConverter().ConvertFromString(background)!;
        IconTextBlock.Foreground = (Brush)new BrushConverter().ConvertFromString(foreground)!;
        IconTextBlock.Text = icon;
    }

    private void Primary_Click(object sender, RoutedEventArgs e)
    {
        SelectedResult = AppDialogResult.Primary;
        DialogResult = true;
    }

    private void Secondary_Click(object sender, RoutedEventArgs e)
    {
        SelectedResult = AppDialogResult.Secondary;
        DialogResult = false;
    }

    private void Tertiary_Click(object sender, RoutedEventArgs e)
    {
        SelectedResult = AppDialogResult.Tertiary;
        DialogResult = false;
    }
}

/// <summary>
/// 共通ダイアログの視覚的な意味を表す。
/// </summary>
public enum AppDialogKind
{
    Information,
    Warning,
    Error,
    Question,
}

/// <summary>
/// 共通ダイアログで選択されたButtonを表す。
/// </summary>
public enum AppDialogResult
{
    None,
    Primary,
    Secondary,
    Tertiary,
}
