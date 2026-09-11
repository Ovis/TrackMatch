using TrackMatch.Infrastructure.Audio;
using Xunit;

namespace TrackMatch.Infrastructure.Tests;

/// <summary>
/// MP3のID3 Tagと音声Propertiesを共通Metadataへ正規化できることを検証する。
/// </summary>
public sealed class Mp3MetadataReaderTests
{
    [Fact]
    public void Read_ValidMp3_ReadsId3AndAudioProperties()
    {
        var path = CreateTemporaryFile(Mp3TestFileBuilder.Create());

        try
        {
            var metadata = new Mp3MetadataReader().Read(path);

            Assert.Equal(["Artist A", "Artist B"], metadata.Artists);
            Assert.Equal("Track Title", metadata.Title);
            Assert.Equal("Album Title", metadata.Album);
            Assert.Equal((uint)5, metadata.TrackNumber);
            Assert.Equal((uint)2, metadata.DiscNumber);
            Assert.Equal(["J-POPS", "Anime"], metadata.Genres);
            Assert.Equal("MP3", metadata.Format);
            Assert.Equal("MPEG Layer III", metadata.Codec);
            Assert.Equal(128, metadata.BitrateKbps);
            Assert.Equal(44100, metadata.SampleRateHz);
            Assert.Null(metadata.BitDepth);
            Assert.Equal(2, metadata.Channels);
            Assert.True(metadata.Duration > TimeSpan.Zero);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void Read_BrokenMp3_ThrowsInvalidDataException()
    {
        var path = CreateTemporaryFile("not mp3"u8.ToArray());

        try
        {
            Assert.Throws<InvalidDataException>(() => new Mp3MetadataReader().Read(path));
        }
        finally
        {
            File.Delete(path);
        }
    }

    private static string CreateTemporaryFile(byte[] bytes)
    {
        var path = Path.Combine(Path.GetTempPath(), $"TrackMatch-{Guid.NewGuid():N}.mp3");
        File.WriteAllBytes(path, bytes);
        return path;
    }
}
