using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;

namespace TrackMatch.App.Quality;

/// <summary>
/// Candidateの片側音源について、識別情報と技術メタデータをコンパクトに表示する。
/// </summary>
internal sealed class CandidateSourceSummaryPanel : Grid
{
    private readonly bool _isTrackA;

    /// <summary>
    /// 指定したCandidate側の音源情報を表示するパネルを構築する。
    /// </summary>
    /// <param name="isTrackA">A側を表示する場合はtrue、B側を表示する場合はfalse</param>
    internal CandidateSourceSummaryPanel(bool isTrackA)
    {
        _isTrackA = isTrackA;
        BuildLayout();
    }

    private void BuildLayout()
    {
        RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        RowDefinitions.Add(new RowDefinition { Height = new GridLength(6) });
        RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        RowDefinitions.Add(new RowDefinition { Height = new GridLength(6) });
        RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        var identity = new StackPanel();
        identity.Children.Add(CreateBoundTextBlock(Property("Title"), fontSize: 18, fontWeight: FontWeights.SemiBold));
        identity.Children.Add(CreateBoundTextBlock(Property("Artist"), margin: new Thickness(0, 4, 0, 0)));
        identity.Children.Add(CreateBoundTextBlock(Property("Album"), margin: new Thickness(0, 2, 0, 0)));
        identity.Children.Add(CreateBoundTextBlock(Property("Genre"), margin: new Thickness(0, 2, 0, 0)));
        Children.Add(identity);

        var separator = new Separator();
        SetRow(separator, 1);
        Children.Add(separator);

        var metadata = new StackPanel();
        metadata.Children.Add(CreateMetadataLine(
            ("形式 / コーデック: ", Property("FormatCodec"), null),
            (null, Property("SampleRate"), null),
            (null, Property("Channels"), " ch"),
            (null, Property("BitDepth"), null)));
        metadata.Children.Add(CreateMetadataLine(
            ("再生時間: ", Property("Duration"), null),
            ("ファイルサイズ: ", Property("FileSize"), null),
            ("ビットレート: ", Property("Bitrate"), null)));
        SetRow(metadata, 2);
        Children.Add(metadata);

        var bottom = new Grid();
        bottom.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        bottom.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(8) });
        bottom.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var pathText = CreateBoundTextBlock(PathProperty());
        pathText.TextTrimming = TextTrimming.CharacterEllipsis;
        pathText.VerticalAlignment = VerticalAlignment.Center;
        pathText.Opacity = 0.75;
        pathText.SetBinding(ToolTipProperty, new Binding(PathProperty()));
        bottom.Children.Add(pathText);

        var openFolderButton = new Button
        {
            Content = "フォルダーを開く",
            Padding = new Thickness(12, 3, 12, 3),
            MinWidth = 110,
        };
        openFolderButton.SetBinding(TagProperty, new Binding(PathProperty()));
        openFolderButton.Click += OpenFolderButton_Click;
        SetColumn(openFolderButton, 2);
        bottom.Children.Add(openFolderButton);

        SetRow(bottom, 4);
        Children.Add(bottom);
    }

    private WrapPanel CreateMetadataLine(params (string? Prefix, string Path, string? Suffix)[] items)
    {
        var panel = new WrapPanel { Margin = new Thickness(0, 1, 0, 0) };
        for (var index = 0; index < items.Length; index++)
        {
            if (index > 0)
            {
                panel.Children.Add(new TextBlock
                {
                    Text = "  |  ",
                    Opacity = 0.55,
                    VerticalAlignment = VerticalAlignment.Center,
                });
            }

            var (prefix, path, suffix) = items[index];
            panel.Children.Add(CreateBoundTextBlock(path, prefix, suffix));
        }

        return panel;
    }

    private static TextBlock CreateBoundTextBlock(
        string path,
        string? prefix = null,
        string? suffix = null,
        double? fontSize = null,
        FontWeight? fontWeight = null,
        Thickness? margin = null)
    {
        var textBlock = new TextBlock
        {
            TextWrapping = TextWrapping.Wrap,
            Margin = margin ?? default,
        };
        if (fontSize is not null)
        {
            textBlock.FontSize = fontSize.Value;
        }

        if (fontWeight is not null)
        {
            textBlock.FontWeight = fontWeight.Value;
        }

        var binding = new Binding(path);
        if (prefix is not null || suffix is not null)
        {
            binding.StringFormat = $"{prefix}{{0}}{suffix}";
        }

        textBlock.SetBinding(TextBlock.TextProperty, binding);
        return textBlock;
    }

    private string Property(string baseName)
        => $"{baseName}{(_isTrackA ? "A" : "B")}";

    private string PathProperty()
        => $"Row.Path{(_isTrackA ? "A" : "B")}";

    private static void OpenFolderButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: string path } button || string.IsNullOrWhiteSpace(path))
        {
            return;
        }

        try
        {
            if (File.Exists(path))
            {
                // 対象ファイルを見失わないよう、単に親フォルダーを開くのではなくExplorer上で選択状態にする。
                Process.Start(new ProcessStartInfo
                {
                    FileName = "explorer.exe",
                    Arguments = $"/select,\"{path}\"",
                    UseShellExecute = true,
                });
                return;
            }

            var directory = Directory.Exists(path) ? path : Path.GetDirectoryName(path);
            if (!string.IsNullOrWhiteSpace(directory) && Directory.Exists(directory))
            {
                Process.Start(new ProcessStartInfo
                {
                    FileName = directory,
                    UseShellExecute = true,
                });
                return;
            }

            new ConfirmationDialog(
                "フォルダーを開く",
                "ファイルまたは保存先フォルダーが見つかりません",
                path,
                "閉じる",
                kind: AppDialogKind.Warning)
            { Owner = Window.GetWindow(button) }.ShowDialog();
        }
        catch (Exception exception) when (exception is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            new ConfirmationDialog(
                "フォルダーを開けませんでした",
                "Explorerを起動できませんでした",
                exception.Message,
                "閉じる",
                kind: AppDialogKind.Error)
            { Owner = Window.GetWindow(button) }.ShowDialog();
        }
    }
}
