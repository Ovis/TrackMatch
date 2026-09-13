using System.Windows;

namespace TrackMatch.App;

/// <summary>
/// TrackMatch共通テーマで確認操作を提示するModal Dialog。
/// </summary>
public partial class ConfirmationDialog : Window
{
    /// <summary>
    /// 確認ダイアログを生成する。
    /// </summary>
    /// <param name="title">Window title</param>
    /// <param name="heading">Dialog内で強調表示する見出し</param>
    /// <param name="message">確認理由や注意事項を説明する本文</param>
    /// <param name="confirmText">確定Buttonに表示する文字列</param>
    public ConfirmationDialog(string title, string heading, string message, string confirmText)
    {
        InitializeComponent();
        Title = title;
        HeadingTextBlock.Text = heading;
        MessageTextBlock.Text = message;
        ConfirmButton.Content = confirmText;
    }

    private void Confirm_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = true;
        Close();
    }

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }
}
