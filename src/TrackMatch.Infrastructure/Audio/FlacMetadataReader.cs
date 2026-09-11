using System.Buffers.Binary;
using System.Text;
using TrackMatch.Core.Models;
using TrackMatch.Core.Scanning;

namespace TrackMatch.Infrastructure.Audio;

/// <summary>
/// FLACファイル先頭のメタデータブロックだけを読み取り、音源情報を取得する。
/// </summary>
/// <remarks>
/// ライブラリ全体の走査では数万ファイルを扱うため、音声フレーム本体を読み込まない。
/// STREAMINFOとVORBIS_COMMENTのみを解釈し、それ以外のブロックは読み飛ばす。
/// </remarks>
public sealed class FlacMetadataReader : IAudioMetadataReader
{
    private const int FlacMagicLength = 4;
    private const int MetadataBlockHeaderLength = 4;
    private const int StreamInfoLength = 34;
    private const byte StreamInfoBlockType = 0;
    private const byte VorbisCommentBlockType = 4;

    private static ReadOnlySpan<byte> FlacMagic => "fLaC"u8;

    /// <inheritdoc />
    public AudioTrackMetadata Read(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        var fileInfo = new FileInfo(path);
        using var stream = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            bufferSize: 4096,
            FileOptions.SequentialScan);

        Span<byte> magic = stackalloc byte[FlacMagicLength];
        stream.ReadExactly(magic);
        if (!magic.SequenceEqual(FlacMagic))
        {
            throw new InvalidDataException("FLACシグネチャが見つからない。");
        }

        FlacStreamInfo? audioInfo = null;
        Dictionary<string, List<string>>? comments = null;
        var isLastBlock = false;
        var blockIndex = 0;
        Span<byte> blockHeader = stackalloc byte[MetadataBlockHeaderLength];
        Span<byte> streamInfo = stackalloc byte[StreamInfoLength];

        while (!isLastBlock)
        {
            stream.ReadExactly(blockHeader);

            isLastBlock = (blockHeader[0] & 0x80) != 0;
            var blockType = (byte)(blockHeader[0] & 0x7f);
            var blockLength = (blockHeader[1] << 16) | (blockHeader[2] << 8) | blockHeader[3];

            if (blockIndex == 0 && blockType != StreamInfoBlockType)
            {
                throw new InvalidDataException("FLACの先頭メタデータブロックがSTREAMINFOではない。");
            }

            switch (blockType)
            {
                case StreamInfoBlockType:
                    if (blockLength != StreamInfoLength)
                    {
                        throw new InvalidDataException($"STREAMINFOのサイズが不正である: {blockLength} bytes");
                    }

                    stream.ReadExactly(streamInfo);
                    audioInfo = ReadStreamInfo(streamInfo);
                    break;

                case VorbisCommentBlockType:
                    var payload = new byte[blockLength];
                    stream.ReadExactly(payload);
                    comments = ReadVorbisComments(payload);
                    break;

                default:
                    SkipExactly(stream, blockLength);
                    break;
            }

            blockIndex++;
        }

        if (audioInfo is null)
        {
            throw new InvalidDataException("STREAMINFOを読み取れなかった。");
        }

        return new AudioTrackMetadata(
            fileInfo.FullName,
            fileInfo.Length,
            fileInfo.LastWriteTimeUtc,
            audioInfo.Duration,
            GetValues(comments, "ARTIST"),
            GetFirstValue(comments, "TITLE"),
            GetFirstValue(comments, "ALBUM"),
            ParseNumber(GetFirstValue(comments, "TRACKNUMBER")),
            ParseNumber(GetFirstValue(comments, "DISCNUMBER")),
            GetValues(comments, "GENRE"),
            Format: "FLAC",
            Codec: "FLAC",
            BitrateKbps: CalculateBitrateKbps(fileInfo.Length, audioInfo.Duration),
            SampleRateHz: audioInfo.SampleRate,
            BitDepth: audioInfo.BitDepth,
            Channels: audioInfo.Channels);
    }

    private static FlacStreamInfo ReadStreamInfo(ReadOnlySpan<byte> streamInfo)
    {
        var sampleRate = (streamInfo[10] << 12) | (streamInfo[11] << 4) | (streamInfo[12] >> 4);
        if (sampleRate == 0)
        {
            throw new InvalidDataException("STREAMINFOのサンプルレートが0である。");
        }

        var channels = ((streamInfo[12] >> 1) & 0x07) + 1;
        var bitDepth = (((streamInfo[12] & 0x01) << 4) | (streamInfo[13] >> 4)) + 1;
        var totalSamples = ((ulong)(streamInfo[13] & 0x0f) << 32)
            | ((ulong)streamInfo[14] << 24)
            | ((ulong)streamInfo[15] << 16)
            | ((ulong)streamInfo[16] << 8)
            | streamInfo[17];
        var duration = TimeSpan.FromSeconds((double)totalSamples / sampleRate);

        return new FlacStreamInfo(duration, sampleRate, bitDepth, channels);
    }

    private static int? CalculateBitrateKbps(long fileSize, TimeSpan duration)
    {
        if (fileSize < 0 || duration <= TimeSpan.Zero)
        {
            return null;
        }

        return checked((int)Math.Round(fileSize * 8d / duration.TotalSeconds / 1000d));
    }

    private static Dictionary<string, List<string>> ReadVorbisComments(ReadOnlySpan<byte> payload)
    {
        var offset = 0;
        var vendorLength = ReadUInt32(payload, ref offset);
        Skip(payload, ref offset, vendorLength);

        var commentCount = ReadUInt32(payload, ref offset);
        var comments = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);

        for (uint i = 0; i < commentCount; i++)
        {
            var commentLength = ReadUInt32(payload, ref offset);
            var commentBytes = ReadSlice(payload, ref offset, commentLength);
            var comment = Encoding.UTF8.GetString(commentBytes);
            var separatorIndex = comment.IndexOf('=');
            if (separatorIndex <= 0)
            {
                continue;
            }

            var key = comment[..separatorIndex];
            var value = comment[(separatorIndex + 1)..];

            if (!comments.TryGetValue(key, out var values))
            {
                values = [];
                comments.Add(key, values);
            }

            values.Add(value);
        }

        return comments;
    }

    private static uint ReadUInt32(ReadOnlySpan<byte> payload, ref int offset)
    {
        if (payload.Length - offset < sizeof(uint))
        {
            throw new InvalidDataException("VORBIS_COMMENTが途中で終了している。");
        }

        var value = BinaryPrimitives.ReadUInt32LittleEndian(payload[offset..]);
        offset += sizeof(uint);
        return value;
    }

    private static ReadOnlySpan<byte> ReadSlice(ReadOnlySpan<byte> payload, ref int offset, uint length)
    {
        if (length > int.MaxValue || payload.Length - offset < (int)length)
        {
            throw new InvalidDataException("VORBIS_COMMENT内の文字列長が不正である。");
        }

        var slice = payload.Slice(offset, (int)length);
        offset += (int)length;
        return slice;
    }

    private static void Skip(ReadOnlySpan<byte> payload, ref int offset, uint length)
    {
        _ = ReadSlice(payload, ref offset, length);
    }

    private static void SkipExactly(Stream stream, int length)
    {
        if (length < 0 || stream.Length - stream.Position < length)
        {
            throw new InvalidDataException("FLACメタデータブロックがファイル終端を超えている。");
        }

        stream.Seek(length, SeekOrigin.Current);
    }

    private static string? GetFirstValue(Dictionary<string, List<string>>? comments, string key)
    {
        return comments is not null
            && comments.TryGetValue(key, out var values)
            && values.Count > 0
                ? values[0]
                : null;
    }

    private static IReadOnlyList<string> GetValues(Dictionary<string, List<string>>? comments, string key)
    {
        return comments is not null && comments.TryGetValue(key, out var values)
            ? values.ToArray()
            : [];
    }

    private static uint? ParseNumber(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var separatorIndex = value.IndexOf('/');
        var numberPart = separatorIndex > 0 ? value[..separatorIndex] : value;
        return uint.TryParse(numberPart.Trim(), out var number) ? number : null;
    }

    private sealed record FlacStreamInfo(TimeSpan Duration, int SampleRate, int BitDepth, int Channels);
}
