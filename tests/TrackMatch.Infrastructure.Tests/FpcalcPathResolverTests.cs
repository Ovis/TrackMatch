using TrackMatch.Infrastructure.Chromaprint;
using Xunit;

namespace TrackMatch.Infrastructure.Tests;

public sealed class FpcalcPathResolverTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "TrackMatch.Tests", Guid.NewGuid().ToString("N"));

    public FpcalcPathResolverTests()
    {
        Directory.CreateDirectory(_directory);
    }

    public void Dispose()
    {
        Directory.Delete(_directory, recursive: true);
    }

    [Fact]
    public void Resolve_ExplicitPathTakesPriority()
    {
        var explicitPath = Path.Combine(_directory, "custom", "fpcalc.exe");

        var resolved = FpcalcPathResolver.Resolve(explicitPath, _directory);

        Assert.Equal(explicitPath, resolved);
    }

    [Fact]
    public void Resolve_UsesBundledFpcalcBeforePathFallback()
    {
        var bundledDirectory = Path.Combine(_directory, "fpcalc");
        Directory.CreateDirectory(bundledDirectory);
        var bundled = Path.Combine(bundledDirectory, "fpcalc.exe");
        File.WriteAllText(bundled, string.Empty);

        var resolved = FpcalcPathResolver.Resolve("fpcalc", _directory);

        Assert.Equal(bundled, resolved);
    }

    [Fact]
    public void Resolve_FallsBackToPathCommandWhenBundleIsMissing()
    {
        var resolved = FpcalcPathResolver.Resolve("fpcalc", _directory);

        Assert.Equal("fpcalc", resolved);
    }
}
