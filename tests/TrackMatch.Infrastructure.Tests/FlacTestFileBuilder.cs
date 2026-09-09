using System.Buffers.Binary;
using System.Text;

namespace TrackMatch.Infrastructure.Tests;

internal static class FlacTestFileBuilder
{
    public static byte[] Create(
        int sampleRate = 44100,
        int durationSeconds = 125,
        params string[] comments)
    {
        var streamInfo = new byte[34];
        const int channels = 2;
        const int bitsPerSample = 16;
        var totalSamples = (ulong)sampleRate * (ulong)durationSeconds;
        var packed = ((ulong)sampleRate << 44)
            | ((ulong)(channels - 1) << 41)
            | ((ulong)(bitsPerSample - 1) << 36)
            | totalSamples;
        BinaryPrimitives.WriteUInt64BigEndian(streamInfo.AsSpan(10, 8), packed);

        var vorbisComment = CreateVorbisComment(comments);

        using var stream = new MemoryStream();
        stream.Write("fLaC"u8);
        WriteBlock(stream, blockType: 0, isLast: false, streamInfo);
        WriteBlock(stream, blockType: 4, isLast: true, vorbisComment);
        return stream.ToArray();
    }

    private static byte[] CreateVorbisComment(string[] comments)
    {
        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream, Encoding.UTF8, leaveOpen: true);

        var vendor = Encoding.UTF8.GetBytes("TrackMatch.Tests");
        writer.Write((uint)vendor.Length);
        writer.Write(vendor);
        writer.Write((uint)comments.Length);

        foreach (var comment in comments)
        {
            var bytes = Encoding.UTF8.GetBytes(comment);
            writer.Write((uint)bytes.Length);
            writer.Write(bytes);
        }

        return stream.ToArray();
    }

    private static void WriteBlock(Stream stream, byte blockType, bool isLast, byte[] payload)
    {
        stream.WriteByte((byte)(blockType | (isLast ? 0x80 : 0x00)));
        stream.WriteByte((byte)(payload.Length >> 16));
        stream.WriteByte((byte)(payload.Length >> 8));
        stream.WriteByte((byte)payload.Length);
        stream.Write(payload);
    }
}
