namespace TrackMatch.Core.Playback;

/// <summary>
/// A/B同期再生で使用する出力Routingを表す。
/// </summary>
public enum SynchronizedPlaybackMode
{
    AOnly,
    BOnly,
    StereoOverlay,
    SplitLeftRight,
}

/// <summary>
/// Common Timeline上へA/B Audioを配置する非負Offsetを表す。
/// </summary>
public sealed record PlaybackOffsets(TimeSpan A, TimeSpan B)
{
    /// <summary>
    /// 相対Offsetを維持したまま、少なくとも片側が0になるよう正規化する。
    /// </summary>
    public static PlaybackOffsets Normalize(TimeSpan a, TimeSpan b)
    {
        var minimum = a <= b ? a : b;
        try
        {
            return new PlaybackOffsets(a - minimum, b - minimum);
        }
        catch (OverflowException)
        {
            throw new ArgumentOutOfRangeException(nameof(a), "Offset差がTimeSpanで表現可能な範囲を超えている。");
        }
    }
}

/// <summary>
/// A/B同期再生のCommon TimelineをSample Frame単位で計算する純粋Logicを提供する。
/// </summary>
public static class PlaybackTimeline
{
    /// <summary>
    /// TimeSpanを指定Sample Rate上の最も近い整数Sample Frameへ変換する。
    /// </summary>
    public static long ToFrame(TimeSpan value, int sampleRate)
    {
        if (sampleRate <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(sampleRate));
        }

        var frames = value.Ticks * (decimal)sampleRate / TimeSpan.TicksPerSecond;
        if (frames > long.MaxValue || frames < long.MinValue)
        {
            throw new ArgumentOutOfRangeException(nameof(value), "Sample Frameへ変換可能な範囲を超えている。");
        }

        // Fractional-delayは採用しないため、PCM境界では最も近い整数Frameへ丸める。
        return checked((long)Math.Round(frames, MidpointRounding.AwayFromZero));
    }

    /// <summary>
    /// Sample FrameをTimeSpanへ変換する。
    /// </summary>
    public static TimeSpan FromFrame(long frame, int sampleRate)
    {
        if (sampleRate <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(sampleRate));
        }

        var ticks = frame * (decimal)TimeSpan.TicksPerSecond / sampleRate;
        if (ticks > TimeSpan.MaxValue.Ticks || ticks < TimeSpan.MinValue.Ticks)
        {
            throw new ArgumentOutOfRangeException(nameof(frame));
        }

        return TimeSpan.FromTicks(checked((long)Math.Round(ticks, MidpointRounding.AwayFromZero)));
    }

    /// <summary>
    /// Offset適用後のA/B AudioのUnion終端Frameを返す。
    /// </summary>
    public static long GetUnionLengthFrames(
        long durationAFrames,
        long durationBFrames,
        long offsetAFrames,
        long offsetBFrames)
    {
        ValidateNonNegative(durationAFrames, nameof(durationAFrames));
        ValidateNonNegative(durationBFrames, nameof(durationBFrames));
        ValidateNonNegative(offsetAFrames, nameof(offsetAFrames));
        ValidateNonNegative(offsetBFrames, nameof(offsetBFrames));

        return Math.Max(
            checked(offsetAFrames + durationAFrames),
            checked(offsetBFrames + durationBFrames));
    }

    /// <summary>
    /// Common PositionをUnion Timelineの有効範囲へClampする。
    /// </summary>
    public static long ClampCommonPosition(long frame, long unionLengthFrames)
    {
        ValidateNonNegative(unionLengthFrames, nameof(unionLengthFrames));
        return Math.Clamp(frame, 0, unionLengthFrames);
    }

    /// <summary>
    /// Common Frameから片側SourceのFrame位置を求める。Audio開始前は負値、終了後はDuration以上になり得る。
    /// </summary>
    public static long ToSourceFrame(long commonFrame, long sourceOffsetFrames)
    {
        ValidateNonNegative(commonFrame, nameof(commonFrame));
        ValidateNonNegative(sourceOffsetFrames, nameof(sourceOffsetFrames));
        return checked(commonFrame - sourceOffsetFrames);
    }

    /// <summary>
    /// 指定Common FrameでSource Audioが実データを持つか判定する。
    /// </summary>
    public static bool HasAudio(long commonFrame, long sourceOffsetFrames, long sourceDurationFrames)
    {
        ValidateNonNegative(commonFrame, nameof(commonFrame));
        ValidateNonNegative(sourceOffsetFrames, nameof(sourceOffsetFrames));
        ValidateNonNegative(sourceDurationFrames, nameof(sourceDurationFrames));
        var sourceFrame = ToSourceFrame(commonFrame, sourceOffsetFrames);
        return sourceFrame >= 0 && sourceFrame < sourceDurationFrames;
    }

    private static void ValidateNonNegative(long value, string parameterName)
    {
        if (value < 0)
        {
            throw new ArgumentOutOfRangeException(parameterName);
        }
    }
}
