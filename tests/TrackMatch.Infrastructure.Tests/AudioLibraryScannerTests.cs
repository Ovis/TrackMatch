using TrackMatch.Core.Scanning;
using TrackMatch.Infrastructure.Audio;
using TrackMatch.Infrastructure.Scanning;
using Xunit;

namespace TrackMatch.Infrastructure.Tests;

/// <summary>
/// 正式対応Extensionだけを走査し、File単位の失敗をLibrary全体へ波及させないことを検証する。
/// </summary>
public sealed class AudioLibraryScannerTests
{
    [Fact]
    public void Scan_RecursivelyFindsFlacAndMp3AndIgnoresUnsupportedExtension()
    {
        var root = Path.Combine(Path.GetTempPath(), $"TrackMatch-{Guid.NewGuid():N}");
        var nested = Path.Combine(root, "nested");
        Directory.CreateDirectory(nested);
        File.WriteAllBytes(Path.Combine(nested, "track.FLAC"), FlacTestFileBuilder.Create(comments: ["TITLE=FLAC Track"]));
        File.WriteAllBytes(Path.Combine(root, "track.MP3"), Mp3TestFileBuilder.Create());
        File.WriteAllText(Path.Combine(root, "ignore.m4a"), "unsupported");
        File.WriteAllText(Path.Combine(root, "ignore.txt"), "unsupported");

        try
        {
            var scanner = new AudioLibraryScanner(new AudioMetadataReaderDispatcher());
            var results = scanner.Scan(root, TestContext.Current.CancellationToken).ToArray();

            Assert.Equal(2, results.Length);
            Assert.All(results, result => Assert.True(result.IsSuccess));
            Assert.Contains(results, result => result.Metadata!.Format == "FLAC");
            Assert.Contains(results, result => result.Metadata!.Format == "MP3");
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void Scan_BrokenSupportedFile_ReturnsFailureAndContinues()
    {
        var root = Path.Combine(Path.GetTempPath(), $"TrackMatch-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        File.WriteAllText(Path.Combine(root, "broken.mp3"), "broken");
        File.WriteAllBytes(Path.Combine(root, "valid.flac"), FlacTestFileBuilder.Create(comments: ["TITLE=Valid Track"]));

        try
        {
            var scanner = new AudioLibraryScanner(new AudioMetadataReaderDispatcher());
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
    [Fact]
    public void Scan_WhenFastPathMatches_SkipsMetadataReader()
    {
        var root = Path.Combine(Path.GetTempPath(), $"TrackMatch-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        var path = Path.Combine(root, "unchanged.flac");
        File.WriteAllBytes(path, FlacTestFileBuilder.Create(comments: ["TITLE=Unchanged"]));

        try
        {
            var reader = new CountingMetadataReader();
            var scanner = new AudioLibraryScanner(reader);
            var result = Assert.Single(scanner.Scan(root, _ => true, TestContext.Current.CancellationToken));

            Assert.True(result.MetadataSkipped);
            Assert.False(result.IsSuccess);
            Assert.NotNull(result.Snapshot);
            Assert.Equal(0, reader.ReadCount);
            Assert.Equal(new FileInfo(path).Length, result.Snapshot.FileSize);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private sealed class CountingMetadataReader : IAudioMetadataReader
    {
        public int ReadCount { get; private set; }

        public TrackMatch.Core.Models.AudioTrackMetadata Read(string path)
        {
            ReadCount++;
            throw new InvalidOperationException("fast pathではMetadata Readerを呼び出してはならない。");
        }
    }

}
