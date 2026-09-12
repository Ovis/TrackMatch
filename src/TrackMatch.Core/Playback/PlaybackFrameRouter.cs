namespace TrackMatch.Core.Playback;

/// <summary>
/// A/B Stereo Sample FrameへGainとPlayback Modeを適用する純粋Logicを提供する。
/// </summary>
public static class PlaybackFrameRouter
{
    /// <summary>
    /// A/B各Stereo Frameを指定Modeで1組のStereo出力へ変換する。
    /// </summary>
    public static StereoSampleFrame Route(
        StereoSampleFrame a,
        StereoSampleFrame b,
        float gainA,
        float gainB,
        SynchronizedPlaybackMode mode)
    {
        ValidateGain(gainA, nameof(gainA));
        ValidateGain(gainB, nameof(gainB));

        var aLeft = a.Left * gainA;
        var aRight = a.Right * gainA;
        var bLeft = b.Left * gainB;
        var bRight = b.Right * gainB;

        return mode switch
        {
            SynchronizedPlaybackMode.AOnly => new StereoSampleFrame(aLeft, aRight),
            SynchronizedPlaybackMode.BOnly => new StereoSampleFrame(bLeft, bRight),
            // Loudness normalizationは仕様外なので、Gain適用後のStereoをそのまま加算する。
            SynchronizedPlaybackMode.StereoOverlay => new StereoSampleFrame(aLeft + bLeft, aRight + bRight),
            // 等量平均で各StereoをMono化し、AをLeft、BをRightへ配置する。
            SynchronizedPlaybackMode.SplitLeftRight => new StereoSampleFrame(
                (aLeft + aRight) * 0.5f,
                (bLeft + bRight) * 0.5f),
            _ => throw new ArgumentOutOfRangeException(nameof(mode)),
        };
    }

    private static void ValidateGain(float value, string parameterName)
    {
        if (!float.IsFinite(value) || value < 0f)
        {
            throw new ArgumentOutOfRangeException(parameterName);
        }
    }
}

/// <summary>
/// 32-bit float PCMのStereo Sample Frameを表す。
/// </summary>
public readonly record struct StereoSampleFrame(float Left, float Right);
