using TrackMatch.Infrastructure.Audio;
using Xunit;

namespace TrackMatch.Infrastructure.Tests;

public sealed class FlacMetadataReaderTests
{
    [Fact]
    public void Read_ValidFlac_ReadsStreamInfoAndVorbisComment()
    {
        var path = CreateTemporaryFile(FlacTestFileBuilder.Create(
            comments:
            [
                "ARTIST=Artist A",
                "TITLE=Track Title",
                "ALBUM=Album Title",
                "TRACKNUMBER=5/12",
                "DISCNUMBER=2/3",
                "GENRE=J-POPS",
                "GENRE=Anime",
            ]));

        try
        {
            var metadata = new FlacMetadataReader().Read(path);

            Assert.Equal(TimeSpan.FromSeconds(125), metadata.Duration);
            Assert.Single(metadata.Artists);
            Assert.Equal("Artist A", metadata.Artists[0]);
            Assert.Equal("Track Title", metadata.Title);
            Assert.Equal("Album Title", metadata.Album);
            Assert.Equal((uint)5, metadata.TrackNumber);
            Assert.Equal((uint)2, metadata.DiscNumber);
            Assert.Equal(2, metadata.Genres.Count);
            Assert.Equal("J-POPS", metadata.Genres[0]);
            Assert.Equal("Anime", metadata.Genres[1]);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void Read_InvalidSignature_ThrowsInvalidDataException()
    {
        var path = CreateTemporaryFile("not flac"u8.ToArray());

        try
        {
            Assert.Throws<InvalidDataException>(() => new FlacMetadataReader().Read(path));
        }
        finally
        {
            File.Delete(path);
        }
    }

    private static string CreateTemporaryFile(byte[] bytes)
    {
        var path = Path.Combine(Path.GetTempPath(), $"TrackMatch-{Guid.NewGuid():N}.flac");
        File.WriteAllBytes(path, bytes);
        return path;
    }
}
