using TrackMatch.Infrastructure.Audio;
using Xunit;

namespace TrackMatch.Infrastructure.Tests;

public sealed class AudioTagWriterTests
{
    [Fact]
    public void Write_Mp3_UpdatesOnlySelectedTagsAndKeepsAudioFrames()
    {
        var originalBytes = Mp3TestFileBuilder.Create();
        var path = CreateTemporaryFile(".mp3", originalBytes);
        try
        {
            var reader = new Mp3MetadataReader();
            var original = reader.Read(path);
            new AudioTagWriter().Write(path, original with
            {
                Artists = ["New Artist", "Guest"],
                Title = "New Title",
                Album = "New Album",
                Genres = ["Pop", "Anime"],
                Year = 2024,
                TrackNumber = 7,
                DiscNumber = 3,
            });

            var saved = reader.Read(path);
            Assert.Equal(["New Artist", "Guest"], saved.Artists);
            Assert.Equal("New Title", saved.Title);
            Assert.Equal("New Album", saved.Album);
            Assert.Equal(["Pop", "Anime"], saved.Genres);
            Assert.Equal((uint)2024, saved.Year);
            Assert.Equal((uint)7, saved.TrackNumber);
            Assert.Equal((uint)3, saved.DiscNumber);

            var afterBytes = File.ReadAllBytes(path);
            var audioMarker = new byte[] { 0xff, 0xfb, 0x90, 0x00 };
            var originalStart = originalBytes.AsSpan().IndexOf(audioMarker);
            var afterStart = afterBytes.AsSpan().IndexOf(audioMarker);
            Assert.True(originalStart >= 0 && afterStart >= 0);
            Assert.Equal(
                originalBytes.AsSpan(originalStart, 417 * 100).ToArray(),
                afterBytes.AsSpan(afterStart, 417 * 100).ToArray());
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void Write_Flac_RoundTripsTagsAndPreservesUnrelatedComment()
    {
        var audioBytes = Enumerable.Range(0, 1024).Select(index => (byte)(index % 251)).ToArray();
        var path = CreateTemporaryFile(".flac", FlacTestFileBuilder.Create(
            comments: ["TITLE=Old", "ARTIST=Old Artist", "DATE=2020", "TRACKNUMBER=5", "DISCNUMBER=2", "COMMENT=Keep this"])
            .Concat(audioBytes).ToArray());
        try
        {
            var reader = new FlacMetadataReader();
            var original = reader.Read(path);
            new AudioTagWriter().Write(path, original with
            {
                Artists = ["New Artist", "Guest"],
                Title = "New Title",
                Album = "New Album",
                Genres = ["Pop", "Anime"],
                Year = 2024,
                TrackNumber = 7,
                DiscNumber = 3,
            });

            var saved = reader.Read(path);
            Assert.Equal(["New Artist", "Guest"], saved.Artists);
            Assert.Equal("New Title", saved.Title);
            Assert.Equal("New Album", saved.Album);
            Assert.Equal(["Pop", "Anime"], saved.Genres);
            Assert.Equal((uint)2024, saved.Year);
            Assert.Equal((uint)7, saved.TrackNumber);
            Assert.Equal((uint)3, saved.DiscNumber);
            Assert.Contains("COMMENT=Keep this", System.Text.Encoding.UTF8.GetString(File.ReadAllBytes(path)));
            Assert.True(File.ReadAllBytes(path).AsSpan().EndsWith(audioBytes));

            new AudioTagWriter().Write(path, saved with
            {
                Artists = [],
                Title = null,
                Album = null,
                Genres = [],
                Year = null,
                TrackNumber = null,
                DiscNumber = null,
            });
            var cleared = reader.Read(path);
            Assert.Empty(cleared.Artists);
            Assert.Null(cleared.Title);
            Assert.Null(cleared.Album);
            Assert.Empty(cleared.Genres);
            Assert.Null(cleared.Year);
            Assert.Null(cleared.TrackNumber);
            Assert.Null(cleared.DiscNumber);
            Assert.Contains("COMMENT=Keep this", System.Text.Encoding.UTF8.GetString(File.ReadAllBytes(path)));
            Assert.True(File.ReadAllBytes(path).AsSpan().EndsWith(audioBytes));
        }
        finally
        {
            File.Delete(path);
        }
    }

    private static string CreateTemporaryFile(string extension, byte[] bytes)
    {
        var path = Path.Combine(Path.GetTempPath(), $"TrackMatch-{Guid.NewGuid():N}{extension}");
        File.WriteAllBytes(path, bytes);
        return path;
    }
}
