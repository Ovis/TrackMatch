using System.Globalization;
using System.Windows.Data;
using TrackMatch.Core.Playback;

namespace TrackMatch.App.Playback;

/// <summary>
/// 同期再生モードの内部値を、利用者が用途を理解しやすい日本語表示へ変換する。
/// </summary>
public sealed class PlaybackModeLabelConverter : IValueConverter
{
    /// <inheritdoc />
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        => value switch
        {
            SynchronizedPlaybackMode.AOnly => "Aのみ再生",
            SynchronizedPlaybackMode.BOnly => "Bのみ再生",
            SynchronizedPlaybackMode.StereoOverlay => "A/Bを重ねて再生",
            SynchronizedPlaybackMode.SplitLeftRight => "Aを左・Bを右で再生",
            _ => value?.ToString() ?? string.Empty,
        };

    /// <inheritdoc />
    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => throw new NotSupportedException("表示専用の変換であるため、逆変換は行わない。");
}
