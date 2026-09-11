using TagLib;
using TrackMatch.Core.Models;
using TrackMatch.Core.Scanning;

namespace TrackMatch.Infrastructure.Audio;

/// <summary>
/// TagLibSharpを利用してMP3のID3タグとMPEG音声情報を共通メタデータへ変換する。
/// </summary>
/// <remarks>
/// Core/ApplicationへID3やTagLibSharp型を漏らさないため、Format固有処理はInfrastructure境界で完結させる。
/// MP3のBit DepthはPCMのような意味を持たないため取得せずnullとする。
/// </remarks>
public sealed class Mp3MetadataReader : IAudioMetadataReader
{
    /// <inheritdoc />
    public AudioTrackMetadata Read(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        var fileInfo = new FileInfo(path);
        try
        {
            using var file = TagLib.File.Create(fileInfo.FullName);
            return new AudioTrackMetadata(
                fileInfo.FullName,
                fileInfo.Length,
                fileInfo.LastWriteTimeUtc,
                file.Properties.Duration,
                NormalizeValues(file.Tag.Performers),
                NullIfWhiteSpace(file.Tag.Title),
                NullIfWhiteSpace(file.Tag.Album),
                ZeroToNull(file.Tag.Track),
                ZeroToNull(file.Tag.Disc),
                NormalizeValues(file.Tag.Genres),
                Format: "MP3",
                Codec: "MPEG Layer III",
                BitrateKbps: PositiveToNull(file.Properties.AudioBitrate),
                SampleRateHz: PositiveToNull(file.Properties.AudioSampleRate),
                BitDepth: null,
                Channels: PositiveToNull(file.Properties.AudioChannels));
        }
        catch (CorruptFileException exception)
        {
            throw new InvalidDataException($"MP3を読み取れなかった: {exception.Message}", exception);
        }
        catch (UnsupportedFormatException exception)
        {
            throw new InvalidDataException($"MP3として認識できなかった: {exception.Message}", exception);
        }
    }

    private static IReadOnlyList<string> NormalizeValues(IEnumerable<string>? values)
        => values?
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Select(value => value.Trim())
            .ToArray()
            ?? [];

    private static string? NullIfWhiteSpace(string? value)
        => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static uint? ZeroToNull(uint value) => value == 0 ? null : value;

    private static int? PositiveToNull(int value) => value > 0 ? value : null;
}
