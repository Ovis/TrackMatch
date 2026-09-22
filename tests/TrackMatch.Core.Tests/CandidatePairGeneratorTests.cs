using TrackMatch.Core.Candidates;
using TrackMatch.Core.Fingerprinting;
using TrackMatch.Core.Persistence;
using Xunit;

namespace TrackMatch.Core.Tests;

public sealed class CandidatePairGeneratorTests
{
    [Fact]
    public void SimHash_UsesChromaprintMajorityRule()
    {
        var values = new uint[] { 0xffffffffu, 0xffffffffu, 0u };

        var hash = ChromaprintSimHash.Compute(values, 0, values.Length);

        Assert.Equal(0xffffffffu, hash);
    }

    [Fact]
    public void SimHash_TieBitsAreZero()
    {
        var values = new uint[] { 0xffffffffu, 0u };

        var hash = ChromaprintSimHash.Compute(values, 0, values.Length);

        Assert.Equal(0u, hash);
    }

    [Fact]
    public void Generate_FindsDistanceThreePairAndRejectsDistanceFourPair()
    {
        var fingerprints = new[]
        {
            CreateFingerprint(1, 0xaaaaaaaa),
            CreateFingerprint(2, 0xaaaaaaa9), // bit difference = 2
            CreateFingerprint(3, 0xaaaaaaa5), // Track 1との差は4bit
        };
        var generator = new CandidatePairGenerator(new FingerprintSegmentSketcher());

        var result = generator.Generate(fingerprints, new CandidateGenerationOptions());

        Assert.Contains(result.Pairs, pair => pair.TrackIdA == 1 && pair.TrackIdB == 2 && pair.MinimumSegmentHashDistance == 2);
        Assert.DoesNotContain(result.Pairs, pair => pair.TrackIdA == 1 && pair.TrackIdB == 3);
    }

    [Fact]
    public void Sketcher_IncludesTailSegmentWhenStrideDoesNotReachEnd()
    {
        var values = Enumerable.Repeat(0x12345678u, 600).ToArray();
        var fingerprint = new StoredFingerprint(
            1,
            2,
            new AudioFingerprint("track.flac", TimeSpan.FromMinutes(2), values));
        var options = new CandidateGenerationOptions
        {
            SegmentLengthItems = 256,
            SegmentStrideItems = 200,
        };

        var sketches = new FingerprintSegmentSketcher().Create(fingerprint, options);

        Assert.Equal(3, sketches.Count);
    }

    [Fact]
    public void GenerateFromSketches_RequiresThreeHitsAtSameOffset()
    {
        var sketches = new[]
        {
            new FingerprintSegmentSketch(1, 0, 0u),
            new FingerprintSegmentSketch(1, 1, 0u),
            new FingerprintSegmentSketch(1, 2, 0u),
            new FingerprintSegmentSketch(2, 0, 0u),
            new FingerprintSegmentSketch(2, 1, 0u),
        };
        var generator = new CandidatePairGenerator(new FingerprintSegmentSketcher());

        var pairs = generator.GenerateFromSketches(sketches, null, new CandidateGenerationOptions());

        Assert.Empty(pairs);
    }

    [Fact]
    public void GenerateFromSketches_DispersedHitsAcrossOffsetsDoNotFormCandidate()
    {
        var sketches = new[]
        {
            new FingerprintSegmentSketch(1, 0, 0x00000000u),
            new FingerprintSegmentSketch(1, 10, 0xffffffffu),
            new FingerprintSegmentSketch(1, 20, 0xaaaaaaaau),
            new FingerprintSegmentSketch(2, 0, 0x00000000u),
            new FingerprintSegmentSketch(2, 9, 0xffffffffu),
            new FingerprintSegmentSketch(2, 18, 0xaaaaaaaau),
        };
        var generator = new CandidatePairGenerator(new FingerprintSegmentSketcher());

        var pairs = generator.GenerateFromSketches(sketches, null, new CandidateGenerationOptions());

        Assert.Empty(pairs);
    }

    [Fact]
    public void GenerateFromSketches_NonConsecutiveThreeHitsAtSameOffsetFormCandidate()
    {
        var sketches = new[]
        {
            new FingerprintSegmentSketch(1, 2, 0u),
            new FingerprintSegmentSketch(1, 10, 1u),
            new FingerprintSegmentSketch(1, 18, 3u),
            new FingerprintSegmentSketch(2, 0, 0u),
            new FingerprintSegmentSketch(2, 8, 0u),
            new FingerprintSegmentSketch(2, 16, 0u),
        };
        var generator = new CandidatePairGenerator(new FingerprintSegmentSketcher());

        var pair = Assert.Single(generator.GenerateFromSketches(sketches, null, new CandidateGenerationOptions()));

        Assert.Equal(1, pair.TrackIdA);
        Assert.Equal(2, pair.TrackIdB);
        Assert.Equal(0, pair.MinimumSegmentHashDistance);
    }

    [Fact]
    public void GenerateFromSketches_DominantOffsetUsesMostHitsBeforeMinimumDistance()
    {
        var sketches = new[]
        {
            // offset=0は3hitかつ最小距離0。
            new FingerprintSegmentSketch(1, 0, 0x00000000u),
            new FingerprintSegmentSketch(1, 1, 0xffffffffu),
            new FingerprintSegmentSketch(1, 2, 0xaaaaaaaau),
            new FingerprintSegmentSketch(2, 0, 0x00000000u),
            new FingerprintSegmentSketch(2, 1, 0xfffffffcu),
            new FingerprintSegmentSketch(2, 2, 0xaaaaaaa9u),
            // offset=5は4hitで、すべて距離1。hit数を優先してこちらを採用する。
            new FingerprintSegmentSketch(1, 10, 0x55555554u),
            new FingerprintSegmentSketch(1, 11, 0xcccccccdu),
            new FingerprintSegmentSketch(1, 12, 0x33333332u),
            new FingerprintSegmentSketch(1, 13, 0xf0f0f0f1u),
            new FingerprintSegmentSketch(2, 5, 0x55555555u),
            new FingerprintSegmentSketch(2, 6, 0xccccccccu),
            new FingerprintSegmentSketch(2, 7, 0x33333333u),
            new FingerprintSegmentSketch(2, 8, 0xf0f0f0f0u),
        };
        var generator = new CandidatePairGenerator(new FingerprintSegmentSketcher());

        var pair = Assert.Single(generator.GenerateFromSketches(sketches, null, new CandidateGenerationOptions()));

        Assert.Equal(1, pair.MinimumSegmentHashDistance);
    }

    [Fact]
    public void GenerateFromSketches_DominantOffsetTieUsesSmallerMinimumDistance()
    {
        var sketches = new[]
        {
            // offset=0は3hit、最小距離2。
            new FingerprintSegmentSketch(1, 0, 0x00000003u),
            new FingerprintSegmentSketch(1, 1, 0x0000000cu),
            new FingerprintSegmentSketch(1, 2, 0x00000030u),
            new FingerprintSegmentSketch(2, 0, 0u),
            new FingerprintSegmentSketch(2, 1, 0u),
            new FingerprintSegmentSketch(2, 2, 0u),
            // offset=10も3hitだが最小距離1なので、こちらをdominant offsetとして採用する。
            new FingerprintSegmentSketch(1, 20, 0x00000100u),
            new FingerprintSegmentSketch(1, 21, 0x00000600u),
            new FingerprintSegmentSketch(1, 22, 0x00001800u),
            new FingerprintSegmentSketch(2, 10, 0u),
            new FingerprintSegmentSketch(2, 11, 0u),
            new FingerprintSegmentSketch(2, 12, 0u),
        };
        var generator = new CandidatePairGenerator(new FingerprintSegmentSketcher());

        var pair = Assert.Single(generator.GenerateFromSketches(sketches, null, new CandidateGenerationOptions()));

        Assert.Equal(1, pair.MinimumSegmentHashDistance);
    }

    [Fact]
    public void GenerateFromSketches_ReversedTrackInputKeepsOffsetSignCanonical()
    {
        var sketches = new[]
        {
            new FingerprintSegmentSketch(2, 0, 0u),
            new FingerprintSegmentSketch(2, 8, 0u),
            new FingerprintSegmentSketch(2, 16, 0u),
            new FingerprintSegmentSketch(1, 2, 0u),
            new FingerprintSegmentSketch(1, 10, 0u),
            new FingerprintSegmentSketch(1, 18, 0u),
        };
        var generator = new CandidatePairGenerator(new FingerprintSegmentSketcher());

        var pair = Assert.Single(generator.GenerateFromSketches(sketches, null, new CandidateGenerationOptions()));

        Assert.Equal(1, pair.TrackIdA);
        Assert.Equal(2, pair.TrackIdB);
    }

    private static StoredFingerprint CreateFingerprint(long trackId, uint value)
        => new(
            trackId,
            2,
            new AudioFingerprint($"{trackId}.flac", TimeSpan.FromMinutes(4), Enumerable.Repeat(value, 256).ToArray()));
}
