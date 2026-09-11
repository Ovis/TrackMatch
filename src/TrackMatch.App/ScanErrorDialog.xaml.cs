using System.Windows;
using System.Windows.Controls;
using TrackMatch.Core.Scanning;

namespace TrackMatch.App;

/// <summary>
/// Current RunのScan Error一覧と詳細Messageを表示するDialog。
/// </summary>
public partial class ScanErrorDialog : Window
{
    public ScanErrorDialog(IReadOnlyList<IncrementalScanError> errors)
    {
        InitializeComponent();
        ErrorsGrid.ItemsSource = errors ?? throw new ArgumentNullException(nameof(errors));
        ErrorsGrid.SelectedIndex = errors.Count > 0 ? 0 : -1;
    }

    private void ErrorsGrid_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        DetailText.Text = ErrorsGrid.SelectedItem is IncrementalScanError error
            ? error.Message
            : string.Empty;
    }
}
