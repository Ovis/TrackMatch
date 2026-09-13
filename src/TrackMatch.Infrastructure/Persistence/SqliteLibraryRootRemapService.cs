using Dapper;
using TrackMatch.Core.Libraries;

namespace TrackMatch.Infrastructure.Persistence;

/// <summary>
/// Root保存場所変更をGlobal physical relocationとして事前検証・反映する。
/// </summary>
public sealed class SqliteLibraryRootRemapService(SqliteDatabase database)
{
    /// <summary>
    /// Root relocationを適用せず、移動Track、連動Root、Missing、Path衝突を検証する。
    /// </summary>
    /// <param name="libraryId">ユーザーが変更を指定したLibrary ID</param>
    /// <param name="rootId">ユーザーが変更を指定したRoot ID</param>
    /// <param name="newRootPath">ユーザーが物理移動済みの新Root Path</param>
    public async Task<LibraryRootRemapPreview> PreviewAsync(
        long libraryId,
        long rootId,
        string newRootPath,
        CancellationToken cancellationToken = default)
    {
        var normalizedNewRoot = LibraryValueNormalizer.NormalizeRootPath(newRootPath);
        if (!Directory.Exists(normalizedNewRoot.DisplayPath))
        {
            throw new DirectoryNotFoundException($"新しい対象フォルダが存在しません: {normalizedNewRoot.DisplayPath}");
        }

        await using var connection = await database.OpenConnectionAsync(cancellationToken);
        var plan = await BuildPlanAsync(
            connection,
            transaction: null,
            libraryId,
            rootId,
            normalizedNewRoot.DisplayPath,
            cancellationToken);
        return ToPreview(plan);
    }

    /// <summary>
    /// Previewと同じ条件を再検証し、Global Track Pathと包含される他Library RootをAtomicに更新する。
    /// </summary>
    /// <remarks>
    /// RemapはContent ChangeではないためFingerprint、Comparison、Human Verdictは維持する。
    /// 移転先Pathが既存Global Trackと1件でも衝突する場合は部分適用せず全体を中止する。
    /// また、移動後に上位Rootの範囲外となるMembershipだけを削除し、移転先の別RootへMembershipを自動追加しない。
    /// </remarks>
    public async Task<LibraryRootRemapResult> ApplyAsync(
        long libraryId,
        long rootId,
        string newRootPath,
        CancellationToken cancellationToken = default)
    {
        var normalizedNewRoot = LibraryValueNormalizer.NormalizeRootPath(newRootPath);
        if (!Directory.Exists(normalizedNewRoot.DisplayPath))
        {
            throw new DirectoryNotFoundException($"新しい対象フォルダが存在しません: {normalizedNewRoot.DisplayPath}");
        }

        await using var connection = await database.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        var plan = await BuildPlanAsync(
            connection,
            transaction,
            libraryId,
            rootId,
            normalizedNewRoot.DisplayPath,
            cancellationToken);

        if (plan.Collisions.Count != 0)
        {
            throw new InvalidOperationException(
                $"移転先Pathが既存Global Trackと {plan.Collisions.Count:N0} 件衝突するため、保存場所の変更を中止しました。");
        }

        var affectedRootById = plan.AffectedRoots.ToDictionary(root => root.RootId);
        var movingTrackById = plan.MovingTracks.ToDictionary(track => track.Id);
        var removedMembershipCount = 0;

        // 新Pathが別Library Root配下へ入ってもRemapだけではMembershipを新設しない。
        // 一方、旧上位Root由来のMembershipが移動後Pathを包含しなくなる場合は範囲外状態を残さないため削除する。
        var memberships = await connection.QueryAsync<MembershipRow>(new CommandDefinition(
            """
            SELECT lt.LibraryId, lt.TrackId, lt.RootId, r.Path AS RootPath
            FROM LibraryTracks lt
            INNER JOIN LibraryRoots r ON r.Id = lt.RootId
            WHERE lt.TrackId IN @TrackIds;
            """,
            new { TrackIds = plan.MovingTracks.Select(track => track.Id).ToArray() },
            transaction,
            cancellationToken: cancellationToken));

        foreach (var membership in memberships)
        {
            var newTrack = movingTrackById[membership.TrackId];
            var effectiveRootPath = affectedRootById.TryGetValue(membership.RootId, out var affectedRoot)
                ? affectedRoot.NewRootPath
                : membership.RootPath;
            var rootKey = LibraryValueNormalizer.NormalizeRootPath(effectiveRootPath).Key;
            var trackKey = LibraryValueNormalizer.NormalizeTrackPath(newTrack.NewPath).Key;
            if (IsUnderRoot(trackKey, rootKey))
            {
                continue;
            }

            removedMembershipCount += await connection.ExecuteAsync(new CommandDefinition(
                "DELETE FROM LibraryTracks WHERE LibraryId = @LibraryId AND TrackId = @TrackId;",
                new { membership.LibraryId, membership.TrackId },
                transaction,
                cancellationToken: cancellationToken));
        }

        // PathKeyはGlobal UNIQUEなので、移転先が別の移動Trackの旧Pathと一致するケースでも一時衝突しないよう
        // 全移動TrackをTransaction内の一時Keyへ退避してから最終Pathへ確定する。
        foreach (var track in plan.MovingTracks)
        {
            await connection.ExecuteAsync(new CommandDefinition(
                "UPDATE Tracks SET PathKey = @TemporaryPathKey WHERE Id = @TrackId;",
                new { TrackId = track.Id, TemporaryPathKey = $"__REMAP__{track.Id}__{Guid.NewGuid():N}" },
                transaction,
                cancellationToken: cancellationToken));
        }

        foreach (var track in plan.MovingTracks)
        {
            var normalizedTrack = LibraryValueNormalizer.NormalizeTrackPath(track.NewPath);
            await connection.ExecuteAsync(new CommandDefinition(
                """
                UPDATE Tracks
                SET Path = @Path,
                    PathKey = @PathKey,
                    IsMissing = @IsMissing,
                    UpdatedAtUtcTicks = @UpdatedAtUtcTicks
                WHERE Id = @TrackId;
                """,
                new
                {
                    TrackId = track.Id,
                    Path = normalizedTrack.DisplayPath,
                    PathKey = normalizedTrack.Key,
                    IsMissing = track.IsPresent ? 0 : 1,
                    UpdatedAtUtcTicks = DateTime.UtcNow.Ticks,
                },
                transaction,
                cancellationToken: cancellationToken));
        }

        // 親Rootの物理移転に含まれる他Libraryの同一/子Rootも相対位置を維持して連動させる。
        foreach (var root in plan.AffectedRoots)
        {
            var normalizedRoot = LibraryValueNormalizer.NormalizeRootPath(root.NewRootPath);
            await connection.ExecuteAsync(new CommandDefinition(
                """
                UPDATE LibraryRoots
                SET Path = @Path, PathKey = @PathKey
                WHERE Id = @RootId AND LibraryId = @LibraryId;
                """,
                new
                {
                    Path = normalizedRoot.DisplayPath,
                    PathKey = normalizedRoot.Key,
                    root.RootId,
                    root.LibraryId,
                },
                transaction,
                cancellationToken: cancellationToken));
        }

        await transaction.CommitAsync(cancellationToken);
        return new LibraryRootRemapResult(
            libraryId,
            rootId,
            plan.RequestedRoot.Path,
            plan.NewRootPath,
            plan.MovingTracks.Count,
            plan.MovingTracks.Count(track => track.IsPresent),
            plan.MovingTracks.Count(track => !track.IsPresent),
            plan.UnknownAudioFileCount,
            plan.AffectedRoots.Count,
            removedMembershipCount);
    }

    private static async Task<RemapPlan> BuildPlanAsync(
        Microsoft.Data.Sqlite.SqliteConnection connection,
        System.Data.Common.DbTransaction? transaction,
        long libraryId,
        long rootId,
        string newRootPath,
        CancellationToken cancellationToken)
    {
        var roots = (await connection.QueryAsync<RootRow>(new CommandDefinition(
            """
            SELECT r.Id, r.LibraryId, l.Name AS LibraryName, r.Path, r.PathKey
            FROM LibraryRoots r
            INNER JOIN Libraries l ON l.Id = r.LibraryId
            ORDER BY r.Id;
            """,
            transaction: transaction,
            cancellationToken: cancellationToken))).ToArray();
        var requestedRoot = roots.SingleOrDefault(root => root.Id == rootId && root.LibraryId == libraryId)
            ?? throw new InvalidOperationException("指定した対象フォルダはライブラリに存在しません。");
        var oldRootKey = requestedRoot.PathKey;
        var normalizedNewRoot = LibraryValueNormalizer.NormalizeRootPath(newRootPath);

        var affectedRoots = roots
            .Where(root => IsSameOrUnderRoot(root.PathKey, oldRootKey))
            .Select(root =>
            {
                var relative = string.Equals(root.PathKey, oldRootKey, StringComparison.Ordinal)
                    ? "."
                    : Path.GetRelativePath(requestedRoot.Path, root.Path);
                var mappedPath = relative == "."
                    ? normalizedNewRoot.DisplayPath
                    : Path.GetFullPath(Path.Combine(normalizedNewRoot.DisplayPath, relative));
                return new LibraryRootRemapImpact(
                    root.LibraryId,
                    root.LibraryName,
                    root.Id,
                    root.Path,
                    mappedPath,
                    root.Id == rootId);
            })
            .ToArray();

        EnsureSameLibraryRootLayoutIsValid(roots, affectedRoots);

        var allTracks = (await connection.QueryAsync<TrackPathRow>(new CommandDefinition(
            "SELECT Id, Path, PathKey FROM Tracks ORDER BY Id;",
            transaction: transaction,
            cancellationToken: cancellationToken))).ToArray();
        var movingRows = allTracks.Where(track => IsUnderRoot(track.PathKey, oldRootKey)).ToArray();
        var movingIds = movingRows.Select(track => track.Id).ToHashSet();
        var movingTracks = movingRows.Select(track =>
        {
            var relative = Path.GetRelativePath(requestedRoot.Path, track.Path);
            var newPath = Path.GetFullPath(Path.Combine(normalizedNewRoot.DisplayPath, relative));
            return new MovingTrack(track.Id, track.Path, relative, newPath, File.Exists(newPath));
        }).ToArray();

        var targetKeys = new Dictionary<string, MovingTrack>(StringComparer.Ordinal);
        var collisions = new List<LibraryRootRemapCollision>();
        var existingByKey = allTracks.ToDictionary(track => track.PathKey, StringComparer.Ordinal);
        foreach (var track in movingTracks)
        {
            var targetKey = LibraryValueNormalizer.NormalizeTrackPath(track.NewPath).Key;
            if (targetKeys.TryGetValue(targetKey, out var otherMoving))
            {
                collisions.Add(new LibraryRootRemapCollision(track.Id, track.OldPath, track.NewPath, otherMoving.Id));
                continue;
            }

            targetKeys[targetKey] = track;
            if (existingByKey.TryGetValue(targetKey, out var existing) && !movingIds.Contains(existing.Id))
            {
                collisions.Add(new LibraryRootRemapCollision(track.Id, track.OldPath, track.NewPath, existing.Id));
            }
        }

        var existingAudioRelativePaths = GetExistingAudioRelativePaths(normalizedNewRoot.DisplayPath);
        var registeredRelativePaths = movingTracks
            .Select(track => track.RelativePath)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var unknownAudioFileCount = existingAudioRelativePaths.Count(path => !registeredRelativePaths.Contains(path));

        return new RemapPlan(
            requestedRoot,
            normalizedNewRoot.DisplayPath,
            affectedRoots,
            movingTracks,
            collisions,
            unknownAudioFileCount);
    }

    private static LibraryRootRemapPreview ToPreview(RemapPlan plan)
        => new(
            plan.RequestedRoot.LibraryId,
            plan.RequestedRoot.Id,
            plan.RequestedRoot.Path,
            plan.NewRootPath,
            plan.MovingTracks.Count,
            plan.MovingTracks.Count(track => track.IsPresent),
            plan.MovingTracks.Where(track => !track.IsPresent).Select(track => track.RelativePath).ToArray(),
            plan.UnknownAudioFileCount,
            plan.AffectedRoots,
            plan.Collisions);

    private static void EnsureSameLibraryRootLayoutIsValid(
        IReadOnlyList<RootRow> roots,
        IReadOnlyList<LibraryRootRemapImpact> affectedRoots)
    {
        var mappedById = affectedRoots.ToDictionary(root => root.RootId);
        var proposed = roots.Select(root =>
        {
            var path = mappedById.TryGetValue(root.Id, out var mapped) ? mapped.NewRootPath : root.Path;
            var normalized = LibraryValueNormalizer.NormalizeRootPath(path);
            return new ProposedRoot(root.Id, root.LibraryId, normalized.DisplayPath, normalized.Key);
        });

        foreach (var libraryRoots in proposed.GroupBy(root => root.LibraryId))
        {
            var values = libraryRoots.ToArray();
            for (var i = 0; i < values.Length; i++)
            {
                for (var j = i + 1; j < values.Length; j++)
                {
                    if (LibraryValueNormalizer.Overlaps(values[i].PathKey, values[j].PathKey))
                    {
                        throw new InvalidOperationException(
                            $"保存場所変更後、同じライブラリの対象フォルダが同一または包含関係になります: {values[i].Path} / {values[j].Path}");
                    }
                }
            }
        }
    }

    private static HashSet<string> GetExistingAudioRelativePaths(string rootPath)
    {
        // RemapはMetadata解析を行わず、正式対応しているFLAC/MP3だけを新Rootの対応確認に利用する。
        return Directory.EnumerateFiles(rootPath, "*", SearchOption.AllDirectories)
            .Where(path => string.Equals(Path.GetExtension(path), ".flac", StringComparison.OrdinalIgnoreCase)
                || string.Equals(Path.GetExtension(path), ".mp3", StringComparison.OrdinalIgnoreCase))
            .Select(path => Path.GetRelativePath(rootPath, path))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
    }

    private static bool IsSameOrUnderRoot(string pathKey, string rootKey)
        => string.Equals(pathKey, rootKey, StringComparison.Ordinal) || IsUnderRoot(pathKey, rootKey);

    private static bool IsUnderRoot(string pathKey, string rootKey)
        => pathKey.Length > rootKey.Length
            && pathKey.StartsWith(rootKey, StringComparison.Ordinal)
            && (rootKey.EndsWith('\\') || pathKey[rootKey.Length] == '\\');

    private sealed record RootRow(long Id, long LibraryId, string LibraryName, string Path, string PathKey);
    private sealed record TrackPathRow(long Id, string Path, string PathKey);
    private sealed record MembershipRow(long LibraryId, long TrackId, long RootId, string RootPath);
    private sealed record ProposedRoot(long Id, long LibraryId, string Path, string PathKey);
    private sealed record MovingTrack(long Id, string OldPath, string RelativePath, string NewPath, bool IsPresent);
    private sealed record RemapPlan(
        RootRow RequestedRoot,
        string NewRootPath,
        IReadOnlyList<LibraryRootRemapImpact> AffectedRoots,
        IReadOnlyList<MovingTrack> MovingTracks,
        IReadOnlyList<LibraryRootRemapCollision> Collisions,
        int UnknownAudioFileCount);
}
