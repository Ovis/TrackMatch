using TrackMatch.Core.Comparison;
using TrackMatch.Core.Fingerprinting;
using Xunit;

namespace TrackMatch.Core.Tests;

public sealed class FingerprintComparerTests
{
    [Fact]
    public void Compare_IdenticalFingerprints_ReturnsPerfectMatch()
    {
        var values = CreateValues(100);
        var a = CreateFingerprint("a.flac", values);
        var b = CreateFingerprint("b.flac", values);

        var result = new FingerprintComparer().Compare(a, b);

        Assert.Equal(1d, result.Similarity, 12);
        Assert.Equal(0, result.BestOffsetItems);
        Assert.Equal(100, result.MatchedItems);
        Assert.Equal(1d, result.CoverageA, 12);
        Assert.Equal(1d, result.CoverageB, 12);
    }

    [Fact]
    public void Compare_ShorterFingerprintContainedInLonger_FindsOffsetAndCoverage()
    {
        var common = CreateValues(100);
        var longer = CreateValues(5, seed: 1000)
            .Concat(common)
            .Concat(CreateValues(5, seed: 2000))
            .ToArray();

        var a = CreateFingerprint("long.flac", longer);
        var b = CreateFingerprint("short.flac", common);

        var result = new FingerprintComparer().Compare(a, b);

        Assert.Equal(1d, result.Similarity, 12);
        Assert.Equal(5, result.BestOffsetItems);
        Assert.Equal(100, result.MatchedItems);
        Assert.Equal(100d / 110d, result.CoverageA, 12);
        Assert.Equal(1d, result.CoverageB, 12);
    }

    [Fact]
    public void Compare_OneBitDifferentPerItem_UsesFixed32BitHammingSimilarity()
    {
        var aValues = Enumerable.Repeat(0u, 100).ToArray();
        var bValues = Enumerable.Repeat(1u, 100).ToArray();

        var result = new FingerprintComparer().Compare(
            CreateFingerprint("a.flac", aValues),
            CreateFingerprint("b.flac", bValues));

        Assert.Equal(31d / 32d, result.Similarity, 12);
    }

    private static AudioFingerprint CreateFingerprint(string path, IReadOnlyList<uint> values)
    {
        return new AudioFingerprint(path, TimeSpan.FromMinutes(3), values);
    }

    private static uint[] CreateValues(int count, int seed = 0)
    {
        return Enumerable.Range(seed, count)
            .Select(static value => unchecked((uint)value * 2654435761u))
            .ToArray();
    }
}
