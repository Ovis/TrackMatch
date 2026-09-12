using System.Globalization;
using System.Windows.Data;

namespace TrackMatch.App.Playback;

/// <summary>
/// 正規化済みA/Bオフセットから、B-Aの符号付き相対オフセットを表示する。
/// </summary>
public sealed class RelativeOffsetConverter : IMultiValueConverter
{
    /// <inheritdoc />
    public object Convert(object[] values, Type targetType, object parameter, CultureInfo culture)
    {
        if (values.Length < 2 || values[0] is not double offsetA || values[1] is not double offsetB)
        {
            return "0.000 s";
        }

        return $"{offsetB - offsetA:+0.000;-0.000;0.000} s";
    }

    /// <inheritdoc />
    public object[] ConvertBack(object value, Type[] targetTypes, object parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
