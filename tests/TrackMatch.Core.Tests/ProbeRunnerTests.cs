using TrackMatch.Core.Comparison;
using TrackMatch.Core.Fingerprinting;
using TrackMatch.Core.Probe;
using Xunit;

namespace TrackMatch.Core.Tests;

public sealed class ProbeRunnerTests
{
    [Fact]
    public async Task RunAsync_ReusesFingerprintForSamePath()
    {
        var extractor = new CountingFingerprintExtractor();
        var runner = new ProbeRunner(extractor, new FingerprintComparer());
        var pairs = new[]
        {
            new ProbePair("A-B", "duplicate", "a.flac", "b.flac"),
            new ProbePair("A-C", "tv-size", "a.flac", "c.flac"),
        };

        var results = new List<ProbeResult>();
        await foreach (var result in runner.RunAsync(pairs, TestContext.Current.CancellationToken))
        {
            results.Add(result);
        }

        Assert.Equal(2, results.Count);
        Assert.Equal(1, extractor.GetCount("a.flac"));
        Assert.Equal(1, extractor.GetCount("b.flac"));
        Assert.Equal(1, extractor.GetCount("c.flac"));
    }

    private sealed class CountingFingerprintExtractor : IFingerprintExtractor
    {
        private readonly Dictionary<string, int> _counts = new(StringComparer.OrdinalIgnoreCase);

        public Task<AudioFingerprint> ExtractAsync(string path, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var fullPath = Path.GetFullPath(path);
            _counts[fullPath] = _counts.GetValueOrDefault(fullPath) + 1;

            return Task.FromResult(new AudioFingerprint(
                fullPath,
                TimeSpan.FromSeconds(30),
                [0x12345678u, 0x12345678u, 0x12345678u]));
        }

        public int GetCount(string path) => _counts.GetValueOrDefault(Path.GetFullPath(path));
    }
}
