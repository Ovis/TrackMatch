using System.Text.Json;
using Dapper;
using TrackMatch.Core.Models;

namespace TrackMatch.Infrastructure.Persistence;

/// <summary>タグ編集後の値だけをTrackへ反映する。ファイル状態は次回Scanまで更新しない。</summary>
public sealed class SqliteTrackTagRepository(SqliteDatabase database)
{
    public async Task UpdateAsync(long trackId, AudioTrackMetadata metadata, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(metadata);
        var pathKey = LibraryValueNormalizer.NormalizeTrackPath(metadata.Path).Key;
        await using var connection = await database.OpenConnectionAsync(cancellationToken);
        var changed = await connection.ExecuteAsync(new CommandDefinition(
            """
            UPDATE Tracks SET
                ArtistsJson = @ArtistsJson,
                Title = @Title,
                Album = @Album,
                TrackNumber = @TrackNumber,
                DiscNumber = @DiscNumber,
                GenresJson = @GenresJson,
                Year = @Year,
                UpdatedAtUtcTicks = @UpdatedAtUtcTicks
            WHERE Id = @TrackId AND PathKey = @PathKey AND IsMissing = 0;
            """,
            new
            {
                TrackId = trackId,
                PathKey = pathKey,
                ArtistsJson = JsonSerializer.Serialize(metadata.Artists),
                metadata.Title,
                metadata.Album,
                TrackNumber = metadata.TrackNumber is null ? (long?)null : metadata.TrackNumber.Value,
                DiscNumber = metadata.DiscNumber is null ? (long?)null : metadata.DiscNumber.Value,
                GenresJson = JsonSerializer.Serialize(metadata.Genres),
                Year = metadata.Year is null ? (long?)null : metadata.Year.Value,
                UpdatedAtUtcTicks = DateTime.UtcNow.Ticks,
            },
            cancellationToken: cancellationToken));
        if (changed != 1)
        {
            throw new InvalidOperationException("音源の管理データが見つかりません。再スキャンしてください。");
        }
    }
}
