using R128Net;
using TrackMatch.Core.Quality;

namespace TrackMatch.Infrastructure.Audio;

/// <summary>
/// Decode済みFloat PCMを1回受け取り、Track単体の品質測定値を同時に集計する。
/// </summary>
internal sealed class PcmTrackQualityAccumulator : IDisposable
{
    private const float PeakNearThreshold = 0.999f;
    private readonly int _channels;
    private readonly int _sampleRate;
    private readonly LoudnessMeter _meter;
    private readonly SpectrumFeatureAccumulator _spectrum;
    private long _peakNearSampleCount;
    private long _clippingRunCount;
    private long _clippingTotalFrames;
    private long _clippingLongestFrames;
    private long _currentClippingRunFrames;
    private double _leftSquareSum;
    private double _rightSquareSum;
    private long _frameCount;
    private bool _disposed;

    public PcmTrackQualityAccumulator(int channels, int sampleRate)
    {
        if (channels <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(channels));
        }

        if (sampleRate <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(sampleRate));
        }

        _channels = channels;
        _sampleRate = sampleRate;
        var modes = LoudnessModes.Integrated | LoudnessModes.LoudnessRange | LoudnessModes.TruePeak;
        _meter = new LoudnessMeter(channels, sampleRate, modes);
        _spectrum = new SpectrumFeatureAccumulator(sampleRate);
    }

    /// <summary>
    /// インターリーブされたPCMを追加する。
    /// </summary>
    public void AddFrames(ReadOnlySpan<float> samples)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (samples.Length % _channels != 0)
        {
            throw new ArgumentException("PCMサンプル数はチャンネル数の倍数である必要があります。", nameof(samples));
        }

        if (samples.IsEmpty)
        {
            return;
        }

        _meter.AddFrames(samples);
        var frames = samples.Length / _channels;
        for (var frame = 0; frame < frames; frame++)
        {
            var baseIndex = frame * _channels;
            var framePeakNear = false;
            double mono = 0;

            for (var channel = 0; channel < _channels; channel++)
            {
                var sample = samples[baseIndex + channel];
                mono += sample;
                if (Math.Abs(sample) >= PeakNearThreshold)
                {
                    _peakNearSampleCount++;
                    framePeakNear = true;
                }
            }

            var left = samples[baseIndex];
            _leftSquareSum += left * left;
            if (_channels >= 2)
            {
                var right = samples[baseIndex + 1];
                _rightSquareSum += right * right;
            }

            _spectrum.AddSample(mono / _channels);
            _frameCount++;

            if (framePeakNear)
            {
                _currentClippingRunFrames++;
                _clippingTotalFrames++;
            }
            else
            {
                CompleteClippingRun();
            }
        }
    }

    /// <summary>
    /// 現在までの測定結果から永続化用モデルを生成する。
    /// </summary>
    public TrackQualityAnalysis Build(long trackId, DateTime analyzedAtUtc)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (trackId <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(trackId));
        }

        if (analyzedAtUtc.Kind != DateTimeKind.Utc)
        {
            throw new ArgumentException("解析完了日時はUTCで指定する必要があります。", nameof(analyzedAtUtc));
        }

        CompleteClippingRun();

        var truePeakLinear = 0d;
        for (var channel = 0; channel < _channels; channel++)
        {
            truePeakLinear = Math.Max(truePeakLinear, _meter.GetTruePeak(channel));
        }

        var integrated = NormalizeFinite(_meter.IntegratedLoudness);
        var truePeak = truePeakLinear > 0 ? 20 * Math.Log10(truePeakLinear) : double.NegativeInfinity;
        var lra = NormalizeFinite(_meter.LoudnessRange);
        double? plr = integrated is not null && double.IsFinite(truePeak)
            ? truePeak - integrated.Value
            : null;
        var spectrum = _spectrum.Build();

        return new TrackQualityAnalysis(
            trackId,
            QualityAnalysisVersions.TrackQualityAnalysis,
            QualityAnalysisStatus.Analyzed,
            integrated,
            double.IsFinite(truePeak) ? truePeak : null,
            lra,
            plr,
            _peakNearSampleCount,
            _clippingRunCount,
            FramesToTimeSpan(_clippingTotalFrames),
            FramesToTimeSpan(_clippingLongestFrames),
            CalculateLeftRightDifference(),
            spectrum.EffectiveUpperFrequencyHz,
            spectrum.HasHighFrequencyCutoff,
            spectrum.HighFrequencyCutoffHz,
            spectrum.HighFrequencyEnergyRatio,
            spectrum.HighFrequencyConsistency,
            analyzedAtUtc,
            null);
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _meter.Dispose();
    }

    private void CompleteClippingRun()
    {
        if (_currentClippingRunFrames <= 0)
        {
            return;
        }

        _clippingRunCount++;
        _clippingLongestFrames = Math.Max(_clippingLongestFrames, _currentClippingRunFrames);
        _currentClippingRunFrames = 0;
    }

    private double? CalculateLeftRightDifference()
    {
        if (_channels < 2 || _frameCount <= 0)
        {
            return null;
        }

        var leftRms = Math.Sqrt(_leftSquareSum / _frameCount);
        var rightRms = Math.Sqrt(_rightSquareSum / _frameCount);

        // DBへInfinityを保存すると比較や表示が扱いづらいため、片側が実質無音なら有限の上限値へ丸める。
        const double nearSilenceRms = 1e-12;
        if (leftRms <= nearSilenceRms && rightRms <= nearSilenceRms)
        {
            return 0;
        }

        if (leftRms <= nearSilenceRms)
        {
            return -120;
        }

        if (rightRms <= nearSilenceRms)
        {
            return 120;
        }

        return 20 * Math.Log10(leftRms / rightRms);
    }

    private TimeSpan FramesToTimeSpan(long frames)
        => TimeSpan.FromSeconds(frames / (double)_sampleRate);

    private static double? NormalizeFinite(double value)
        => double.IsFinite(value) ? value : null;
}
