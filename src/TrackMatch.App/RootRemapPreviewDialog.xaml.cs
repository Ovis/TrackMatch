using System.Windows;
using TrackMatch.Core.Libraries;

namespace TrackMatch.App;

/// <summary>
/// Root保存場所変更の対応結果を適用前に確認するDialog。
/// </summary>
public partial class RootRemapPreviewDialog : Window
{
    public RootRemapPreviewDialog(LibraryRootRemapPreview preview)
    {
        InitializeComponent();
        DataContext = preview ?? throw new ArgumentNullException(nameof(preview));
    }

    private void Apply_Click(object sender, RoutedEventArgs e)
        => DialogResult = true;
}
