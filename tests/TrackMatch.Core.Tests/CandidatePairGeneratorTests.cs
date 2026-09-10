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

    private static StoredFingerprint CreateFingerprint(long trackId, uint value)
        => new(
            trackId,
            2,
            new AudioFingerprint($"{trackId}.flac", TimeSpan.FromMinutes(4), Enumerable.Repeat(value, 256).ToArray()));
}
