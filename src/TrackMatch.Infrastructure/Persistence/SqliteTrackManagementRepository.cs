using System.Text.Json;
using Dapper;
using TrackMatch.Core.Management;

namespace TrackMatch.Infrastructure.Persistence;

/// <summary>
/// Global Track管理画面向けの一覧取得、Force Reanalysis、管理データ削除をSQLiteへ適用する。
/// </summary>
public sealed class SqliteTrackManagementRepository(SqliteDatabase database)
{
    /// <summary>
    /// 指定Filterに該当するGlobal TrackをLibrary Membership名付きで取得する。
    /// </summary>
    public async Task<IReadOnlyList<ManagedTrack>> GetTracksAsync(
        TrackManagementFilter filter,
        CancellationToken cancellationToken = default)
    {
        const string sql = """
            SELECT t.Id, t.Path, t.Title, t.ArtistsJson, t.IsMissing,
                   l.Id AS LibraryId, l.Name AS LibraryName
            FROM Tracks t
            LEFT JOIN LibraryTracks lt ON lt.TrackId = t.Id
            LEFT JOIN Libraries l ON l.Id = lt.LibraryId
            ORDER BY t.Path COLLATE NOCASE, l.Name COLLATE NOCASE;
            """;
        await using var connection = await database.OpenConnectionAsync(cancellationToken);
        var rows = (await connection.QueryAsync<ManagedTrackRow>(new CommandDefinition(
            sql,
            cancellationToken: cancellationToken))).ToArray();

        var tracks = rows
            .GroupBy(row => new { row.Id, row.Path, row.Title, row.ArtistsJson, row.IsMissing })
            .Select(group =>
            {
                var first = group.Key;
                var artists = JsonSerializer.Deserialize<string[]>(first.ArtistsJson) ?? [];
                var title = string.IsNullOrWhiteSpace(first.Title)
                    ? Path.GetFileName(first.Path)
                    : first.Title;
                var libraries = group
                    .Where(row => row.LibraryId is not null && row.LibraryName is not null)
                    .OrderBy(row => row.LibraryName, StringComparer.OrdinalIgnoreCase)
                    .Select(row => row.LibraryName!)
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToArray();
                return new ManagedTrack(
                    first.Id,
                    first.Path,
                    title,
                    artists.Length == 0 ? string.Empty : string.Join(" & ", artists),
                    first.IsMissing != 0,
                    libraries);
            })
            .Where(track => filter switch
            {
                TrackManagementFilter.Missing => track.IsMissing,
                TrackManagementFilter.Unowned => track.IsUnowned,
                _ => true,
            })
            .ToArray();
        return tracks;
    }

    /// <summary>
    /// 指定Global Trackの機械解析Current StateとHuman Verdict Current Stateを無効化する。
    /// Human Verdictは履歴へ退避し、全MembershipをCandidate Generation Pendingへ戻す。
    /// </summary>
    public Task<ForceReanalysisResult> ForceReanalysisTrackAsync(
        long trackId,
        CancellationToken cancellationToken = default)
        => ForceReanalysisTracksAsync([trackId], cancellationToken);

    /// <summary>
    /// 選択した複数Global Trackを1 TransactionでForce Reanalysisする。
    /// </summary>
    public Task<ForceReanalysisResult> ForceReanalysisTracksAsync(
        IReadOnlyCollection<long> trackIds,
        CancellationToken cancellationToken = default)
        => ForceReanalysisAsync(
            ForceReanalysisScope.Track,
            trackIds,
            "ForceReanalysisTrack",
            cancellationToken);

    /// <summary>
    /// 指定Root由来Membershipに属するGlobal TrackをForce Reanalysisする。
    /// </summary>
    public async Task<ForceReanalysisResult> ForceReanalysisRootAsync(
        long rootId,
        CancellationToken cancellationToken = default)
    {
        if (rootId <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(rootId));
        }

        await using var connection = await database.OpenConnectionAsync(cancellationToken);
        var trackIds = (await connection.QueryAsync<long>(new CommandDefinition(
            "SELECT TrackId FROM LibraryTracks WHERE RootId = @RootId ORDER BY TrackId;",
            new { RootId = rootId },
            cancellationToken: cancellationToken))).Distinct().ToArray();
        return await ForceReanalysisAsync(
            ForceReanalysisScope.Root,
            trackIds,
            "ForceReanalysisRoot",
            cancellationToken);
    }

    /// <summary>
    /// 指定LibraryのMembershipに属するGlobal TrackをForce Reanalysisする。
    /// Shared TrackはGlobal解析を共有するため、他Libraryにも同じ無効化が反映される。
    /// </summary>
    public async Task<ForceReanalysisResult> ForceReanalysisLibraryAsync(
        long libraryId,
        CancellationToken cancellationToken = default)
    {
        if (libraryId <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(libraryId));
        }

        await using var connection = await database.OpenConnectionAsync(cancellationToken);
        var trackIds = (await connection.QueryAsync<long>(new CommandDefinition(
            "SELECT TrackId FROM LibraryTracks WHERE LibraryId = @LibraryId ORDER BY TrackId;",
            new { LibraryId = libraryId },
            cancellationToken: cancellationToken))).Distinct().ToArray();
        return await ForceReanalysisAsync(
            ForceReanalysisScope.Library,
            trackIds,
            "ForceReanalysisLibrary",
            cancellationToken);
    }

    /// <summary>
    /// Global TrackのTrackMatch管理データだけを完全削除する。元Audio Fileは操作しない。
    /// </summary>
    /// <remarks>
    /// HistoryテーブルはTrack FKを持たず履歴として残る設計なので、明示的な管理データ削除ではCurrentとHistoryを両方消す。
    /// 同じPathが後日Scanで再発見された場合は新規Track IDとして登録される。
    /// </remarks>
    public async Task<int> DeleteTracksAsync(
        IReadOnlyCollection<long> trackIds,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(trackIds);
        var ids = trackIds.Where(id => id > 0).Distinct().ToArray();
        if (ids.Length == 0)
        {
            return 0;
        }

        await using var connection = await database.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);

        await connection.ExecuteAsync(new CommandDefinition(
            "DELETE FROM CandidateReviewHistory WHERE TrackIdA IN @TrackIds OR TrackIdB IN @TrackIds;",
            new { TrackIds = ids },
            transaction,
            cancellationToken: cancellationToken));

        // Keep HistoryはFKを持たず、KeepTrackIdだけでなくGraphKeySnapshotにもTrack IDを保持する。
        // 明示的な「完全削除」でIdentityを履歴側へ残さないよう、Graph Keyを構造として解析して該当履歴を削除する。
        var deleteIdSet = ids.ToHashSet();
        var keepHistoryRows = (await connection.QueryAsync<KeepHistoryRow>(new CommandDefinition(
            "SELECT Id, GraphKeySnapshot, KeepTrackId FROM LibraryDuplicateGroupKeepHistory;",
            transaction: transaction,
            cancellationToken: cancellationToken))).ToArray();
        var keepHistoryIds = keepHistoryRows
            .Where(row => row.KeepTrackId is { } keepTrackId && deleteIdSet.Contains(keepTrackId)
                || ParseGraphKey(row.GraphKeySnapshot).Any(deleteIdSet.Contains))
            .Select(row => row.Id)
            .ToArray();
        if (keepHistoryIds.Length > 0)
        {
            await connection.ExecuteAsync(new CommandDefinition(
                "DELETE FROM LibraryDuplicateGroupKeepHistory WHERE Id IN @HistoryIds;",
                new { HistoryIds = keepHistoryIds },
                transaction,
                cancellationToken: cancellationToken));
        }

        // KeepTrackIdのFKはON DELETE SET NULLだが、Status='Selected'のままNULL化すると不正なCurrent Stateになる。
        // 完全削除では対象TrackのIdentity自体を破棄するため、そのTrackをKeepとしていたCurrent Stateも先に除去する。
        // Groupが残る場合は後続のGlobal同期・ProjectionでUnselectedとして扱い、古いKeepを自動復元しない。
        await connection.ExecuteAsync(new CommandDefinition(
            "DELETE FROM LibraryDuplicateGroupKeepStates WHERE KeepTrackId IN @TrackIds;",
            new { TrackIds = ids },
            transaction,
            cancellationToken: cancellationToken));

        // Track配下のCurrent StateはFK CASCADEを正本とし、依存順序を個別コードへ複製しない。
        var deleted = await connection.ExecuteAsync(new CommandDefinition(
            "DELETE FROM Tracks WHERE Id IN @TrackIds;",
            new { TrackIds = ids },
            transaction,
            cancellationToken: cancellationToken));

        // Track削除のCASCADEで構成Trackが1件以下になったGroupは有効なGlobal Groupではない。
        // 後続再同期の入力へ孤立Groupを残さないため、このTransaction内でGroup本体も整理する。
        await connection.ExecuteAsync(new CommandDefinition(
            """
            DELETE FROM DuplicateGroups
            WHERE Id IN (
                SELECT g.Id
                FROM DuplicateGroups g
                LEFT JOIN DuplicateGroupTracks gt ON gt.DuplicateGroupId = g.Id
                GROUP BY g.Id
                HAVING COUNT(gt.TrackId) < 2
            );
            """,
            transaction: transaction,
            cancellationToken: cancellationToken));

        await transaction.CommitAsync(cancellationToken);
        return deleted;
    }

    private async Task<ForceReanalysisResult> ForceReanalysisAsync(
        ForceReanalysisScope scope,
        IReadOnlyCollection<long> trackIds,
        string invalidationReason,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(trackIds);
        var ids = trackIds.Where(id => id > 0).Distinct().ToArray();
        if (ids.Length == 0)
        {
            return new ForceReanalysisResult(scope, 0, 0);
        }

        await using var connection = await database.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        var existingCount = await connection.ExecuteScalarAsync<long>(new CommandDefinition(
            "SELECT COUNT(*) FROM Tracks WHERE Id IN @TrackIds;",
            new { TrackIds = ids },
            transaction,
            cancellationToken: cancellationToken));
        if (existingCount != ids.Length)
        {
            throw new InvalidOperationException("Force Reanalysis対象に存在しないGlobal Trackが含まれています。");
        }

        var archivedReviewCount = await connection.ExecuteAsync(new CommandDefinition(
            """
            INSERT INTO CandidateReviewHistory (
                TrackIdA, TrackIdB, Decision, Note, SourceLibraryId, SourceLibraryNameSnapshot,
                ChangedAtUtcTicks, ChangeKind, InvalidationReason)
            SELECT r.TrackIdA, r.TrackIdB, r.Decision, r.Note, r.SourceLibraryId, r.SourceLibraryNameSnapshot,
                   @ChangedAtUtcTicks, 'ForceReanalysis', @InvalidationReason
            FROM CandidateReviews r
            WHERE r.TrackIdA IN @TrackIds OR r.TrackIdB IN @TrackIds;
            """,
            new
            {
                TrackIds = ids,
                ChangedAtUtcTicks = DateTime.UtcNow.Ticks,
                InvalidationReason = invalidationReason,
            },
            transaction,
            cancellationToken: cancellationToken));

        await connection.ExecuteAsync(new CommandDefinition(
            "DELETE FROM CandidateReviews WHERE TrackIdA IN @TrackIds OR TrackIdB IN @TrackIds;",
            new { TrackIds = ids },
            transaction,
            cancellationToken: cancellationToken));
        await connection.ExecuteAsync(new CommandDefinition(
            "DELETE FROM CandidatePairs WHERE TrackIdA IN @TrackIds OR TrackIdB IN @TrackIds;",
            new { TrackIds = ids },
            transaction,
            cancellationToken: cancellationToken));
        await connection.ExecuteAsync(new CommandDefinition(
            "DELETE FROM CandidateComparisons WHERE TrackIdA IN @TrackIds OR TrackIdB IN @TrackIds;",
            new { TrackIds = ids },
            transaction,
            cancellationToken: cancellationToken));
        await connection.ExecuteAsync(new CommandDefinition(
            "DELETE FROM CandidateSegmentSketches WHERE TrackId IN @TrackIds;",
            new { TrackIds = ids },
            transaction,
            cancellationToken: cancellationToken));
        await connection.ExecuteAsync(new CommandDefinition(
            "DELETE FROM TrackQualityAnalyses WHERE TrackId IN @TrackIds;",
            new { TrackIds = ids },
            transaction,
            cancellationToken: cancellationToken));
        await connection.ExecuteAsync(new CommandDefinition(
            "DELETE FROM Fingerprints WHERE TrackId IN @TrackIds;",
            new { TrackIds = ids },
            transaction,
            cancellationToken: cancellationToken));
        await connection.ExecuteAsync(new CommandDefinition(
            """
            UPDATE LibraryTracks
            SET CandidateGenerationPending = 1,
                CandidateGenerationVersion = NULL
            WHERE TrackId IN @TrackIds;
            """,
            new { TrackIds = ids },
            transaction,
            cancellationToken: cancellationToken));

        await transaction.CommitAsync(cancellationToken);
        return new ForceReanalysisResult(scope, ids.Length, archivedReviewCount);
    }

    private static IEnumerable<long> ParseGraphKey(string graphKey)
    {
        foreach (var token in graphKey.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (!long.TryParse(token, out var trackId) || trackId <= 0)
            {
                throw new InvalidDataException($"Duplicate Group履歴のGraph Keyが不正です: {graphKey}");
            }

            yield return trackId;
        }
    }

    private sealed record KeepHistoryRow(long Id, string GraphKeySnapshot, long? KeepTrackId);

    private sealed record ManagedTrackRow(
        long Id,
        string Path,
        string? Title,
        string ArtistsJson,
        long IsMissing,
        long? LibraryId,
        string? LibraryName);
}
