using System.Buffers.Binary;
using Dapper;
using TrackMatch.Core.Fingerprinting;
using TrackMatch.Core.Persistence;

namespace TrackMatch.Infrastructure.Persistence;

/// <summary>
/// Candidate Generation向けに有効なTrackのFingerprintをSQLiteから読み出す。
/// </summary>
public sealed class SqliteFingerprintCatalogRepository(SqliteDatabase database) : IFingerprintCatalogRepository
{
    public async Task<IReadOnlyList<StoredFingerprint>> GetActiveAsync(
        int algorithm,
        CancellationToken cancellationToken = default)
    {
        const string sql = """
            SELECT f.TrackId, f.Algorithm, t.Path, t.DurationTicks, f.ValuesBlob, f.ExtractedAtUtcTicks
            FROM Fingerprints f
            INNER JOIN Tracks t ON t.Id = f.TrackId
            WHERE t.IsMissing = 0 AND f.Algorithm = @Algorithm
            ORDER BY f.TrackId;
            """;

        await using var connection = await database.OpenConnectionAsync(cancellationToken);
        var rows = await connection.QueryAsync<FingerprintRow>(new CommandDefinition(
            sql,
            new { Algorithm = algorithm },
            cancellationToken: cancellationToken));
        return rows
            .Select(row => new StoredFingerprint(
                row.TrackId,
                checked((int)row.Algorithm),
                new AudioFingerprint(row.Path, TimeSpan.FromTicks(row.DurationTicks), Decode(row.ValuesBlob)),
                new DateTime(row.ExtractedAtUtcTicks, DateTimeKind.Utc)))
            .ToArray();
    }

    private static IReadOnlyList<uint> Decode(byte[] bytes)
    {
        if (bytes.Length % sizeof(uint) != 0)
        {
            throw new InvalidDataException("Fingerprint BLOBの長さが4byte境界ではない。");
        }

        var values = new uint[bytes.Length / sizeof(uint)];
        for (var i = 0; i < values.Length; i++)
        {
            values[i] = BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(i * sizeof(uint), sizeof(uint)));
        }

        return values;
    }

    // SQLite INTEGERはInt64として返るため、DapperのコンストラクタMaterialize境界ではlongで受ける。
    private sealed record FingerprintRow(
        long TrackId,
        long Algorithm,
        string Path,
        long DurationTicks,
        byte[] ValuesBlob,
        long ExtractedAtUtcTicks);
}
