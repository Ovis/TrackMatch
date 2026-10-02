using System.Buffers.Binary;
using System.Text;
using Microsoft.Data.Sqlite;
using TrackMatch.Application;
using TrackMatch.Infrastructure.Persistence;
using Xunit;

namespace TrackMatch.App.Tests;

public sealed class TrackTagEditingServiceTests : IAsyncLifetime
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "TrackMatch.Tests", Guid.NewGuid().ToString("N"));
    private string _path = null!;
    private string _databasePath = null!;
    private long _trackId;

    public async ValueTask InitializeAsync()
    {
        Directory.CreateDirectory(_directory);
        _path = Path.Combine(_directory, "source.flac");
        _databasePath = Path.Combine(_directory, "trackmatch.db");
        File.WriteAllBytes(_path, CreateFlac());
        var database = new SqliteDatabase(_databasePath);
        await database.InitializeAsync(TestContext.Current.CancellationToken);
        var original = new TrackTagEditingService(_databasePath).Read(_path);
        _trackId = await new SqliteTrackRepository(database).UpsertMetadataAsync(
            original, TestContext.Current.CancellationToken);
    }

    public ValueTask DisposeAsync()
    {
        SqliteConnection.ClearAllPools();
        Directory.Delete(_directory, recursive: true);
        return ValueTask.CompletedTask;
    }

    [Fact]
    public async Task SaveAsync_UpdatesFileAndTagsButLeavesScanSnapshotStale()
    {
        var service = new TrackTagEditingService(_databasePath);
        var original = service.Read(_path);
        var saved = await service.SaveAsync(
            _trackId,
            original,
            original with { Title = "Updated", Artists = ["New Artist"] },
            TestContext.Current.CancellationToken);

        var stored = await new SqliteTrackRepository(new SqliteDatabase(_databasePath))
            .GetByPathAsync(_path, TestContext.Current.CancellationToken);
        Assert.Equal("Updated", saved.Title);
        Assert.Equal("Updated", service.Read(_path).Title);
        Assert.Equal("Updated", stored!.Metadata.Title);
        Assert.Equal(["New Artist"], stored.Metadata.Artists);
        Assert.Equal(original.FileSize, stored.Metadata.FileSize);
        Assert.Equal(original.LastWriteTimeUtc, stored.Metadata.LastWriteTimeUtc);
        Assert.True(saved.FileSize != original.FileSize || saved.LastWriteTimeUtc != original.LastWriteTimeUtc);
    }

    [Fact]
    public async Task SaveAsync_WhenFileChangedAfterOpen_DoesNotOverwriteIt()
    {
        var service = new TrackTagEditingService(_databasePath);
        var original = service.Read(_path);
        File.SetLastWriteTimeUtc(_path, original.LastWriteTimeUtc.AddSeconds(10));

        await Assert.ThrowsAsync<InvalidOperationException>(() => service.SaveAsync(
            _trackId,
            original,
            original with { Title = "Updated" },
            TestContext.Current.CancellationToken));
        Assert.Equal("Old", service.Read(_path).Title);
    }

    internal static byte[] CreateFlac()
    {
        var streamInfo = new byte[34];
        const int sampleRate = 44100;
        var packed = ((ulong)sampleRate << 44)
            | ((ulong)1 << 41)
            | ((ulong)15 << 36)
            | (ulong)(sampleRate * 10);
        BinaryPrimitives.WriteUInt64BigEndian(streamInfo.AsSpan(10, 8), packed);

        using var comments = new MemoryStream();
        using (var writer = new BinaryWriter(comments, Encoding.UTF8, leaveOpen: true))
        {
            var vendor = Encoding.UTF8.GetBytes("TrackMatch.Tests");
            var title = Encoding.UTF8.GetBytes("TITLE=Old");
            writer.Write((uint)vendor.Length);
            writer.Write(vendor);
            writer.Write((uint)1);
            writer.Write((uint)title.Length);
            writer.Write(title);
        }

        using var file = new MemoryStream();
        file.Write("fLaC"u8);
        WriteBlock(file, 0, isLast: false, streamInfo);
        WriteBlock(file, 4, isLast: true, comments.ToArray());
        return file.ToArray();
    }

    private static void WriteBlock(Stream stream, byte type, bool isLast, byte[] content)
    {
        stream.WriteByte((byte)(type | (isLast ? 0x80 : 0)));
        stream.WriteByte((byte)(content.Length >> 16));
        stream.WriteByte((byte)(content.Length >> 8));
        stream.WriteByte((byte)content.Length);
        stream.Write(content);
    }
}
