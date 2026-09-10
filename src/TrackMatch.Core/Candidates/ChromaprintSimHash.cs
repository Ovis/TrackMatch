namespace TrackMatch.Core.Candidates;

/// <summary>
/// Chromaprintのchromaprint_hash_fingerprintと同じ多数決方式の32-bit SimHashを計算する。
/// </summary>
public static class ChromaprintSimHash
{
    public static uint Compute(IReadOnlyList<uint> values, int offset, int length)
    {
        ArgumentNullException.ThrowIfNull(values);
        if (offset < 0 || length <= 0 || offset > values.Count - length)
        {
            throw new ArgumentOutOfRangeException(nameof(offset));
        }

        // 32個のビット位置ごとのカウンタを32本の整数として持つ代わりに、
        // カウンタの各桁をuintのビット面として保持する。40,000曲規模で32回/itemのループを避けるためである。
        var planeCount = 1;
        while ((1L << planeCount) <= length)
        {
            planeCount++;
        }

        Span<uint> counterPlanes = planeCount <= 16
            ? stackalloc uint[planeCount]
            : new uint[planeCount];
        counterPlanes.Clear();

        for (var i = offset; i < offset + length; i++)
        {
            var carry = values[i];
            var plane = 0;
            while (carry != 0)
            {
                var current = counterPlanes[plane];
                counterPlanes[plane] = current ^ carry;
                carry &= current;
                plane++;
            }
        }

        var threshold = length / 2;
        uint hash = 0;
        for (var bit = 0; bit < 32; bit++)
        {
            var count = 0;
            for (var plane = 0; plane < planeCount; plane++)
            {
                count |= (int)((counterPlanes[plane] >> bit) & 1u) << plane;
            }

            // Chromaprint本体と同じく、ちょうど半数の場合は0側とする。
            if (count > threshold)
            {
                hash |= 1u << bit;
            }
        }

        return hash;
    }
}
