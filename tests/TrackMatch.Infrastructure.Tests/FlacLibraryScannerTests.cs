using TrackMatch.Infrastructure.Audio;
using TrackMatch.Infrastructure.Scanning;
using Xunit;

namespace TrackMatch.Infrastructure.Tests;

public sealed class FlacLibraryScannerTests
{
    [Fact]
    public void Scan_RecursivelyFindsOnlyFlacFiles()
    {
        var root = Path.Combine(Path.GetTempPath(), $"TrackMatch-{Guid.NewGuid():N}");
        var nested = Path.Combine(root, "nested");
        Directory.CreateDirectory(nested);
        File.WriteAllBytes(Path.Combine(nested, "track.FLAC"), FlacTestFileBuilder.Create(comments: ["TITLE=Nested Track"]));
        File.WriteAllText(Path.Combine(root, "ignore.txt"), "ignore");

        try
        {
            var scanner = new FlacLibraryScanner(new FlacMetadataReader());
            var results = scanner.Scan(root, TestContext.Current.CancellationToken).ToArray();

            Assert.Single(results);
            Assert.True(results[0].IsSuccess);
            Assert.Equal("Nested Track", results[0].Metadata!.Title);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void Scan_InvalidFlac_ReturnsFailureAndContinues()
    {
        var root = Path.Combine(Path.GetTempPath(), $"TrackMatch-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        File.WriteAllText(Path.Combine(root, "broken.flac"), "broken");
        File.WriteAllBytes(Path.Combine(root, "valid.flac"), FlacTestFileBuilder.Create(comments: ["TITLE=Valid Track"]));

        try
        {
            var scanner = new FlacLibraryScanner(new FlacMetadataReader());
            var results = scanner.Scan(root, TestContext.Current.CancellationToken).ToArray();

            Assert.Equal(2, results.Length);
            Assert.Single(results, result => result.IsSuccess);
            Assert.Single(results, result => !result.IsSuccess);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }
}
