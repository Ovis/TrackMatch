using System.Text;

namespace TrackMatch.Infrastructure.Tests;

/// <summary>
/// TagLibSharpがID3v2とMPEG Audio Propertiesを読める最小限のMP3 Test Dataを生成する。
/// </summary>
internal static class Mp3TestFileBuilder
{
    private const int FrameLength = 417;

    public static byte[] Create()
    {
        using var tagPayload = new MemoryStream();
        WriteTextFrame(tagPayload, "TPE1", "Artist A\0Artist B");
        WriteTextFrame(tagPayload, "TIT2", "Track Title");
        WriteTextFrame(tagPayload, "TALB", "Album Title");
        WriteTextFrame(tagPayload, "TRCK", "5/12");
        WriteTextFrame(tagPayload, "TPOS", "2/3");
        WriteTextFrame(tagPayload, "TCON", "J-POPS\0Anime");

        using var stream = new MemoryStream();
        stream.Write("ID3"u8);
        stream.WriteByte(4);
        stream.WriteByte(0);
        stream.WriteByte(0);
        WriteSynchsafeInt(stream, checked((int)tagPayload.Length));
        tagPayload.Position = 0;
        tagPayload.CopyTo(stream);

        // MPEG-1 Layer III / 128 kbps / 44.1 kHz / stereo。複数Frameを置き、Properties算出を可能にする。
        for (var frameIndex = 0; frameIndex < 100; frameIndex++)
        {
            var frame = new byte[FrameLength];
            frame[0] = 0xff;
            frame[1] = 0xfb;
            frame[2] = 0x90;
            frame[3] = 0x00;
            stream.Write(frame);
        }

        return stream.ToArray();
    }

    private static void WriteTextFrame(Stream stream, string frameId, string text)
    {
        var textBytes = Encoding.UTF8.GetBytes(text);
        var payloadLength = checked(textBytes.Length + 1);
        stream.Write(Encoding.ASCII.GetBytes(frameId));
        WriteSynchsafeInt(stream, payloadLength);
        stream.WriteByte(0);
        stream.WriteByte(0);
        stream.WriteByte(3);
        stream.Write(textBytes);
    }

    private static void WriteSynchsafeInt(Stream stream, int value)
    {
        stream.WriteByte((byte)((value >> 21) & 0x7f));
        stream.WriteByte((byte)((value >> 14) & 0x7f));
        stream.WriteByte((byte)((value >> 7) & 0x7f));
        stream.WriteByte((byte)(value & 0x7f));
    }
}
