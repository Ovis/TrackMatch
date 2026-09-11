using System.Buffers;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;
using TrackMatch.Core.Playback;

namespace TrackMatch.App.Playback;

/// <summary>
/// A/Bを同一Common Frameで読み出し、1本のStereo PCM streamへRoutingする。
/// </summary>
internal sealed class SynchronizedPairSampleProvider : ISampleProvider
{
    private readonly TimelineSource _sourceA;
    private readonly TimelineSource _sourceB;
    private long _remainingFrames;
    private int _mode = (int)SynchronizedPlaybackMode.StereoOverlay;
    private float _volumeA = 1f;
    private float _volumeB = 1f;

    public SynchronizedPairSampleProvider(
        ISampleProvider sourceA,
        long leadingSilenceAFrames,
        ISampleProvider sourceB,
        long leadingSilenceBFrames,
        int sampleRate,
        long remainingFrames)
    {
        if (sampleRate <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(sampleRate));
        }

        if (remainingFrames < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(remainingFrames));
        }

        WaveFormat = WaveFormat.CreateIeeeFloatWaveFormat(sampleRate, 2);
        _sourceA = new TimelineSource(sourceA, leadingSilenceAFrames, sampleRate);
        _sourceB = new TimelineSource(sourceB, leadingSilenceBFrames, sampleRate);
        _remainingFrames = remainingFrames;
    }

    public WaveFormat WaveFormat { get; }

    public SynchronizedPlaybackMode Mode
    {
        get => (SynchronizedPlaybackMode)Volatile.Read(ref _mode);
        set => Volatile.Write(ref _mode, (int)value);
    }

    public float VolumeA
    {
        get => Volatile.Read(ref _volumeA);
        set => Volatile.Write(ref _volumeA, ValidateVolume(value, nameof(value)));
    }

    public float VolumeB
    {
        get => Volatile.Read(ref _volumeB);
        set => Volatile.Write(ref _volumeB, ValidateVolume(value, nameof(value)));
    }

    /// <inheritdoc />
    public int Read(Span<float> buffer)
    {
        var requestedFrames = buffer.Length / 2;
        if (requestedFrames == 0 || _remainingFrames == 0)
        {
            return 0;
        }

        var frames = (int)Math.Min(requestedFrames, _remainingFrames);
        var sampleCount = frames * 2;
        var rentedA = ArrayPool<float>.Shared.Rent(sampleCount);
        var rentedB = ArrayPool<float>.Shared.Rent(sampleCount);
        try
        {
            var samplesA = rentedA.AsSpan(0, sampleCount);
            var samplesB = rentedB.AsSpan(0, sampleCount);
            samplesA.Clear();
            samplesB.Clear();
            _sourceA.Read(samplesA);
            _sourceB.Read(samplesB);

            var mode = Mode;
            var gainA = VolumeA;
            var gainB = VolumeB;
            for (var frame = 0; frame < frames; frame++)
            {
                var index = frame * 2;
                var output = PlaybackFrameRouter.Route(
                    new StereoSampleFrame(samplesA[index], samplesA[index + 1]),
                    new StereoSampleFrame(samplesB[index], samplesB[index + 1]),
                    gainA,
                    gainB,
                    mode);
                buffer[index] = output.Left;
                buffer[index + 1] = output.Right;
            }

            _remainingFrames -= frames;
            return sampleCount;
        }
        finally
        {
            ArrayPool<float>.Shared.Return(rentedA);
            ArrayPool<float>.Shared.Return(rentedB);
        }
    }

    private static float ValidateVolume(float value, string parameterName)
    {
        if (!float.IsFinite(value) || value < 0f)
        {
            throw new ArgumentOutOfRangeException(parameterName);
        }

        return value;
    }

    /// <summary>
    /// Common Timeline開始前のSilenceをSource消費なしで挿入する。
    /// </summary>
    private sealed class TimelineSource(ISampleProvider source, long leadingSilenceFrames, int sampleRate)
    {
        private readonly ISampleProvider _source = source.WaveFormat.SampleRate == sampleRate
            ? source
            : new WdlResamplingSampleProvider(source, sampleRate);
        private long _leadingSilenceFrames = Math.Max(0, leadingSilenceFrames);

        public void Read(Span<float> destination)
        {
            var requestedFrames = destination.Length / 2;
            var silentFrames = (int)Math.Min(requestedFrames, _leadingSilenceFrames);
            _leadingSilenceFrames -= silentFrames;
            var startSample = silentFrames * 2;
            if (startSample >= destination.Length)
            {
                return;
            }

            // Source終端後は呼出元がZero clearした領域を残し、Union Timelineの片側Silenceとして扱う。
            _source.Read(destination[startSample..]);
        }
    }
}

/// <summary>
/// Decoder出力のChannel数を同期Mixer用Stereoへ正規化する。
/// </summary>
internal sealed class StereoChannelNormalizer : ISampleProvider
{
    private readonly ISampleProvider _source;

    public StereoChannelNormalizer(ISampleProvider source)
    {
        _source = source ?? throw new ArgumentNullException(nameof(source));
        WaveFormat = WaveFormat.CreateIeeeFloatWaveFormat(source.WaveFormat.SampleRate, 2);
    }

    public WaveFormat WaveFormat { get; }

    /// <inheritdoc />
    public int Read(Span<float> buffer)
    {
        var frames = buffer.Length / 2;
        if (frames == 0)
        {
            return 0;
        }

        var channels = _source.WaveFormat.Channels;
        var sourceSamples = frames * channels;
        var rented = ArrayPool<float>.Shared.Rent(sourceSamples);
        try
        {
            var input = rented.AsSpan(0, sourceSamples);
            var read = _source.Read(input);
            var readFrames = read / channels;
            for (var frame = 0; frame < readFrames; frame++)
            {
                var inputIndex = frame * channels;
                float left;
                float right;
                if (channels == 1)
                {
                    left = right = input[inputIndex];
                }
                else if (channels == 2)
                {
                    left = input[inputIndex];
                    right = input[inputIndex + 1];
                }
                else
                {
                    // Multi-channel音源は今回の主対象ではないため、全Channel等量Mixで情報落ちを偏らせずStereo化する。
                    var sum = 0f;
                    for (var channel = 0; channel < channels; channel++)
                    {
                        sum += input[inputIndex + channel];
                    }

                    left = right = sum / channels;
                }

                var outputIndex = frame * 2;
                buffer[outputIndex] = left;
                buffer[outputIndex + 1] = right;
            }

            return readFrames * 2;
        }
        finally
        {
            ArrayPool<float>.Shared.Return(rented);
        }
    }
}
