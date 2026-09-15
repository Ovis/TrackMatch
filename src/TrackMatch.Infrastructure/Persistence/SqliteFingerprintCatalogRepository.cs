using System.Buffers.Binary;
using Dapper;
using TrackMatch.Core.Fingerprinting;
using TrackMatch.Core.Persistence;

namespace TrackMatch.Infrastructure.Persistence;

/// <summary>
/// Candidate Generation向けに有効なGlobal TrackのFingerprintをSQLiteから読み出す。
/// </summary>
public sealed class SqliteFingerprintCatalogRepository(
    SqliteDatabase database,
    long? libraryId = null) : IFingerprintCatalogRepository
{
    public async Task<IReadOnlyList<StoredFingerprint>> GetActiveAsync(
        int algorithm,
        CancellationToken cancellationToken = default)
    {
        const string sql = """
            SELECT f.TrackId, f.Algorithm, t.Path, t.DurationTicks, f.ValuesBlob, f.ExtractedAtUtcTicks
            FROM Fingerprints f
            INNER JOIN Tracks t ON t.Id = f.TrackId
            WHERE t.IsMissing = 0
              AND f.Algorithm = @Algorithm
              AND (
                    @LibraryId IS NULL
                 OR EXISTS (
                        SELECT 1 FROM LibraryTracks lt
                        WHERE lt.LibraryId = @LibraryId AND lt.TrackId = t.Id))
            ORDER BY f.TrackId;
            """;

        await using var connection = await database.OpenConnectionAsync(cancellationToken);
        var rows = await connection.QueryAsync<FingerprintRow>(new CommandDefinition(
            sql,
            new { Algorithm = algorithm, LibraryId = libraryId },
            cancellationToken: cancellationToken));
        return rows.Select(ToStoredFingerprint).ToArray();
    }

    public async Task<IReadOnlyList<StoredFingerprintState>> GetActiveStatesAsync(
        int algorithm,
        CancellationToken cancellationToken = default)
    {
        const string sql = """
            SELECT f.TrackId, f.Algorithm, f.ExtractedAtUtcTicks
            FROM Fingerprints f
            INNER JOIN Tracks t ON t.Id = f.TrackId
            WHERE t.IsMissing = 0
              AND f.Algorithm = @Algorithm
              AND (
                    @LibraryId IS NULL
                 OR EXISTS (
                        SELECT 1 FROM LibraryTracks lt
                        WHERE lt.LibraryId = @LibraryId AND lt.TrackId = t.Id))
            ORDER BY f.TrackId;
            """;

        await using var connection = await database.OpenConnectionAsync(cancellationToken);
        var rows = await connection.QueryAsync<FingerprintStateRow>(new CommandDefinition(
            sql,
            new { Algorithm = algorithm, LibraryId = libraryId },
            cancellationToken: cancellationToken));
        return rows
            .Select(row => new StoredFingerprintState(
                row.TrackId,
                checked((int)row.Algorithm),
                new DateTime(row.ExtractedAtUtcTicks, DateTimeKind.Utc)))
            .ToArray();
    }

    public async Task<IReadOnlyList<StoredFingerprint>> GetActiveByTrackIdsAsync(
        int algorithm,
        IReadOnlyCollection<long> trackIds,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(trackIds);
        if (trackIds.Count == 0)
        {
            return [];
        }

        const string sql = """
            SELECT f.TrackId, f.Algorithm, t.Path, t.DurationTicks, f.ValuesBlob, f.ExtractedAtUtcTicks
            FROM Fingerprints f
            INNER JOIN Tracks t ON t.Id = f.TrackId
            WHERE t.IsMissing = 0
              AND f.Algorithm = @Algorithm
              AND (
                    @LibraryId IS NULL
                 OR EXISTS (
                        SELECT 1 FROM LibraryTracks lt
                        WHERE lt.LibraryId = @LibraryId AND lt.TrackId = t.Id))
              AND f.TrackId IN @TrackIds
            ORDER BY f.TrackId;
            """;

        var result = new List<StoredFingerprint>(trackIds.Count);
        await using var connection = await database.OpenConnectionAsync(cancellationToken);
        foreach (var batch in trackIds.Chunk(500))
        {
            var rows = await connection.QueryAsync<FingerprintRow>(new CommandDefinition(
                sql,
                new { Algorithm = algorithm, LibraryId = libraryId, TrackIds = batch },
                cancellationToken: cancellationToken));
            result.AddRange(rows.Select(ToStoredFingerprint));
        }

        return result.OrderBy(item => item.TrackId).ToArray();
    }

    private static StoredFingerprint ToStoredFingerprint(FingerprintRow row)
        => new(
            row.TrackId,
            checked((int)row.Algorithm),
            new AudioFingerprint(row.Path, TimeSpan.FromTicks(row.DurationTicks), Decode(row.ValuesBlob)),
            new DateTime(row.ExtractedAtUtcTicks, DateTimeKind.Utc));

    private static IReadOnlyList<uint> Decode(byte[] bytes)
    {
        if (bytes.Length % sizeof(uint) != 0)
        {
            throw new InvalidDataException("Fingerprint BLOBの長さが4byte境界ではありません。");
        }

        var values = new uint[bytes.Length / sizeof(uint)];
        for (var i = 0; i < values.Length; i++)
        {
            values[i] = BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(i * sizeof(uint), sizeof(uint)));
        }

        return values;
    }

    private sealed record FingerprintRow(
        long TrackId,
        long Algorithm,
        string Path,
        long DurationTicks,
        byte[] ValuesBlob,
        long ExtractedAtUtcTicks);

    private sealed record FingerprintStateRow(
        long TrackId,
        long Algorithm,
        long ExtractedAtUtcTicks);
}
