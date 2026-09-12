using System.Numerics;

namespace TrackMatch.Infrastructure.Audio;

/// <summary>
/// PCMを時間窓ごとにFFTし、高域の特徴だけを小さな集計値として保持する。
/// </summary>
internal sealed class SpectrumFeatureAccumulator
{
    private const int FftSize = 4096;
    private const int HopSize = FftSize / 2;
    private const double MinimumCutoffHz = 12000;
    private const double HighFrequencyStartHz = 15000;
    private readonly int _sampleRate;
    private readonly double[] _window;
    private readonly double[] _samples = new double[FftSize];
    private readonly Complex[] _fft = new Complex[FftSize];
    private readonly double[] _powerSum = new double[(FftSize / 2) + 1];
    private readonly List<double> _windowCutoffs = [];
    private int _sampleCount;
    private int _windowCount;

    public SpectrumFeatureAccumulator(int sampleRate)
    {
        if (sampleRate <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(sampleRate));
        }

        _sampleRate = sampleRate;
        _window = Enumerable.Range(0, FftSize)
            .Select(index => 0.5 - (0.5 * Math.Cos((2 * Math.PI * index) / (FftSize - 1))))
            .ToArray();
    }

    /// <summary>
    /// チャンネル合成済みの1フレーム分サンプルを追加する。
    /// </summary>
    public void AddSample(double sample)
    {
        _samples[_sampleCount++] = sample;
        if (_sampleCount < FftSize)
        {
            return;
        }

        ProcessWindow();
        Array.Copy(_samples, HopSize, _samples, 0, FftSize - HopSize);
        _sampleCount = FftSize - HopSize;
    }

    /// <summary>
    /// 解析済み窓からDBへ保存するスペクトル特徴を生成する。
    /// </summary>
    public SpectrumFeatures Build()
    {
        if (_windowCount == 0)
        {
            return new(null, null, null, null, null);
        }

        var averaged = new double[_powerSum.Length];
        var maxPower = 0d;
        var totalPower = 0d;
        for (var i = 0; i < averaged.Length; i++)
        {
            averaged[i] = _powerSum[i] / _windowCount;
            maxPower = Math.Max(maxPower, averaged[i]);
            totalPower += averaged[i];
        }

        if (maxPower <= 0 || totalPower <= 0)
        {
            return new(null, false, null, 0, null);
        }

        // 微小ノイズを「有効な高域」と誤認しないよう、最大成分から60dB以内を有効成分とみなす。
        var effectiveThreshold = maxPower * 1e-6;
        var effectiveBin = 0;
        for (var i = 1; i < averaged.Length; i++)
        {
            if (averaged[i] >= effectiveThreshold)
            {
                effectiveBin = i;
            }
        }

        var highStartBin = FrequencyToBin(HighFrequencyStartHz);
        var highPower = 0d;
        for (var i = highStartBin; i < averaged.Length; i++)
        {
            highPower += averaged[i];
        }

        var cutoff = EstimateCutoff(averaged, maxPower);
        double? consistency = null;
        if (cutoff is not null && _windowCutoffs.Count > 0)
        {
            var consistentWindows = _windowCutoffs.Count(value => Math.Abs(value - cutoff.Value) <= 1000);
            consistency = consistentWindows / (double)_windowCount;
        }

        return new(
            BinToFrequency(effectiveBin),
            cutoff is not null,
            cutoff,
            highPower / totalPower,
            consistency);
    }

    private void ProcessWindow()
    {
        for (var i = 0; i < FftSize; i++)
        {
            _fft[i] = new Complex(_samples[i] * _window[i], 0);
        }

        ForwardFft(_fft);
        _windowCount++;

        var windowPower = new double[_powerSum.Length];
        var maxPower = 0d;
        for (var i = 0; i < windowPower.Length; i++)
        {
            var power = _fft[i].Magnitude * _fft[i].Magnitude;
            windowPower[i] = power;
            _powerSum[i] += power;
            maxPower = Math.Max(maxPower, power);
        }

        var cutoff = EstimateCutoff(windowPower, maxPower);
        if (cutoff is not null)
        {
            _windowCutoffs.Add(cutoff.Value);
        }
    }

    private double? EstimateCutoff(IReadOnlyList<double> power, double maxPower)
    {
        if (maxPower <= 0)
        {
            return null;
        }

        var nyquist = _sampleRate / 2d;
        var maximumCutoffHz = Math.Min(22000, nyquist * 0.95);
        if (maximumCutoffHz <= MinimumCutoffHz)
        {
            return null;
        }

        var beforeWidth = Math.Max(1, FrequencyToBin(1000));
        var afterWidth = Math.Max(1, FrequencyToBin(2000));
        var minimumBin = FrequencyToBin(MinimumCutoffHz);
        var maximumBin = Math.Min(power.Count - afterWidth - 1, FrequencyToBin(maximumCutoffHz));
        var minimumMeaningfulPower = maxPower * 1e-5;
        double? strongestCutoff = null;
        var strongestDrop = 0d;

        for (var bin = minimumBin; bin <= maximumBin; bin++)
        {
            var before = Average(power, Math.Max(1, bin - beforeWidth), bin);
            var after = Average(power, bin + 1, Math.Min(power.Count, bin + 1 + afterWidth));
            if (before < minimumMeaningfulPower)
            {
                continue;
            }

            var drop = before / Math.Max(after, double.Epsilon);
            if (drop >= 100 && drop > strongestDrop)
            {
                strongestDrop = drop;
                strongestCutoff = BinToFrequency(bin);
            }
        }

        return strongestCutoff;
    }

    private int FrequencyToBin(double frequencyHz)
        => Math.Clamp((int)Math.Round(frequencyHz * FftSize / _sampleRate), 0, FftSize / 2);

    private double BinToFrequency(int bin)
        => bin * _sampleRate / (double)FftSize;

    private static double Average(IReadOnlyList<double> values, int startInclusive, int endExclusive)
    {
        if (endExclusive <= startInclusive)
        {
            return 0;
        }

        var sum = 0d;
        for (var i = startInclusive; i < endExclusive; i++)
        {
            sum += values[i];
        }

        return sum / (endExclusive - startInclusive);
    }

    private static void ForwardFft(Complex[] values)
    {
        var length = values.Length;
        for (int i = 1, j = 0; i < length; i++)
        {
            var bit = length >> 1;
            for (; (j & bit) != 0; bit >>= 1)
            {
                j ^= bit;
            }

            j ^= bit;
            if (i < j)
            {
                (values[i], values[j]) = (values[j], values[i]);
            }
        }

        for (var size = 2; size <= length; size <<= 1)
        {
            var angle = -2 * Math.PI / size;
            var root = new Complex(Math.Cos(angle), Math.Sin(angle));
            for (var start = 0; start < length; start += size)
            {
                var factor = Complex.One;
                var half = size / 2;
                for (var offset = 0; offset < half; offset++)
                {
                    var even = values[start + offset];
                    var odd = values[start + offset + half] * factor;
                    values[start + offset] = even + odd;
                    values[start + offset + half] = even - odd;
                    factor *= root;
                }
            }
        }
    }
}

/// <summary>
/// Track単体解析で保存する高域スペクトル特徴を表す。
/// </summary>
internal sealed record SpectrumFeatures(
    double? EffectiveUpperFrequencyHz,
    bool? HasHighFrequencyCutoff,
    double? HighFrequencyCutoffHz,
    double? HighFrequencyEnergyRatio,
    double? HighFrequencyConsistency);
