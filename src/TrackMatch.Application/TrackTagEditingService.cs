using TrackMatch.Core.Models;
using TrackMatch.Infrastructure.Audio;
using TrackMatch.Infrastructure.Persistence;

namespace TrackMatch.Application;

/// <summary>音源ファイルのタグを保存し、表示用DBへ変更したタグだけを反映する。</summary>
public sealed class TrackTagEditingService
{
    private readonly string _databasePath;
    private readonly AudioMetadataReaderDispatcher _reader = new();
    private readonly AudioTagWriter _writer = new();
    private readonly GlobalMutationGate _mutationGate;

    public TrackTagEditingService(string databasePath, GlobalMutationGate? mutationGate = null)
    {
        _databasePath = string.IsNullOrWhiteSpace(databasePath)
            ? throw new ArgumentException("Database path is required.", nameof(databasePath))
            : databasePath;
        _mutationGate = mutationGate ?? GlobalMutationGate.Shared;
    }

    public AudioTrackMetadata Read(string path) => _reader.Read(path);

    public async Task<AudioTrackMetadata> SaveAsync(
        long trackId,
        AudioTrackMetadata original,
        AudioTrackMetadata edited,
        CancellationToken cancellationToken = default)
    {
        if (trackId <= 0) throw new ArgumentOutOfRangeException(nameof(trackId));
        ArgumentNullException.ThrowIfNull(original);
        ArgumentNullException.ThrowIfNull(edited);
        if (!string.Equals(original.Path, edited.Path, StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException("編集対象のファイルが変わっています。", nameof(edited));
        }

        using var gate = await _mutationGate.EnterAsync(cancellationToken);
        var database = new SqliteDatabase(_databasePath);
        await database.InitializeAsync(cancellationToken);
        var stored = await new SqliteTrackRepository(database).GetByPathAsync(original.Path, cancellationToken);
        if (stored is null || stored.Id != trackId || stored.IsMissing)
        {
            throw new InvalidOperationException("選択した音源の管理データが変わっています。再読み込みしてください。");
        }

        var current = _reader.Read(original.Path);
        if (current.FileSize != original.FileSize
            || current.LastWriteTimeUtc != original.LastWriteTimeUtc
            || !TagsEqual(current, original))
        {
            throw new InvalidOperationException("編集中に音源ファイルが変更されました。画面を開き直してください。");
        }

        if (TagsEqual(current, edited))
        {
            return current;
        }

        cancellationToken.ThrowIfCancellationRequested();
        _writer.Write(current.Path, edited);
        try
        {
            var saved = _reader.Read(current.Path);

            // Timestamp精度の粗い保存先でも次回Scanが変更を検出できるようにする。
            // DBのサイズ・mtimeは旧値のまま保持し、音声同一性の確認を通常Scanへ委ねる。
            if (saved.FileSize == stored.Metadata.FileSize
                && saved.LastWriteTimeUtc == stored.Metadata.LastWriteTimeUtc)
            {
                var nextTime = new DateTime(
                    Math.Max(DateTime.UtcNow.Ticks, stored.Metadata.LastWriteTimeUtc.Ticks + TimeSpan.FromSeconds(2).Ticks),
                    DateTimeKind.Utc);
                File.SetLastWriteTimeUtc(saved.Path, nextTime);
                saved = _reader.Read(saved.Path);
            }

            await new SqliteTrackTagRepository(database).UpdateAsync(trackId, saved, CancellationToken.None);
            return saved;
        }
        catch (Exception exception)
        {
            throw new IOException(
                "ファイルのタグは書き込まれましたが、TrackMatchの表示用データを更新できませんでした。スキャン・分析で確認してください。",
                exception);
        }
    }

    private static bool TagsEqual(AudioTrackMetadata left, AudioTrackMetadata right)
        => left.Artists.SequenceEqual(right.Artists)
            && string.Equals(left.Title, right.Title, StringComparison.Ordinal)
            && string.Equals(left.Album, right.Album, StringComparison.Ordinal)
            && left.Genres.SequenceEqual(right.Genres)
            && left.Year == right.Year
            && left.TrackNumber == right.TrackNumber
            && left.DiscNumber == right.DiscNumber;
}
