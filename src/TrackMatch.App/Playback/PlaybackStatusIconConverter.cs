using System.Globalization;
using System.Windows.Data;

namespace TrackMatch.App.Playback;

/// <summary>
/// 同期再生の状態表示を、再生／一時停止ボタン用のアイコンへ変換する。
/// </summary>
public sealed class PlaybackStatusIconConverter : IValueConverter
{
    /// <inheritdoc />
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        => string.Equals(value as string, "再生中", StringComparison.Ordinal)
            ? "Ⅱ"
            : "▶";

    /// <inheritdoc />
    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => throw new NotSupportedException("表示専用の変換であるため、逆変換は行わない。");
}
