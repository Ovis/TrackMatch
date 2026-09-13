using Dapper;
using TrackMatch.Core.Libraries;

namespace TrackMatch.Infrastructure.Persistence;

/// <summary>
/// Root保存場所変更の事前検証とDB更新を行い、Track IDや解析結果を維持する。
/// </summary>
public sealed class SqliteLibraryRootRemapService(SqliteDatabase database)
{
    /// <summary>
    /// Root保存場所変更を適用せず、新Rootとの相対Path対応状況を検証する。
    /// </summary>
    /// <param name="libraryId">対象Library ID</param>
    /// <param name="rootId">保存場所を変更するRoot ID</param>
    /// <param name="newRootPath">ユーザーがAudio Fileを移動済みの新Root</param>
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
        var root = await GetRequiredRootAsync(connection, libraryId, rootId, cancellationToken);
        await EnsureRootPathAvailableAsync(connection, rootId, normalizedNewRoot.Key, cancellationToken);
        var relativePaths = (await connection.QueryAsync<string>(new CommandDefinition(
            "SELECT RelativePath FROM Tracks WHERE RootId = @RootId ORDER BY RelativePath COLLATE NOCASE;",
            new { RootId = rootId },
            cancellationToken: cancellationToken))).ToArray();

        return BuildPreview(libraryId, root, normalizedNewRoot.DisplayPath, relativePaths);
    }

    /// <summary>
    /// Root保存場所変更を適用し、既存Trackを相対Pathのまま新Rootへ付け替える。
    /// </summary>
    /// <remarks>
    /// FingerprintやReview等を維持するためRoot/Trackを作り直さず、既存IDのPathだけを更新する。
    /// Remap直後の自動Scanは行わず、Missing Trackも新Root基準の期待Pathへ更新する。
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
        var root = await GetRequiredRootAsync(connection, libraryId, rootId, transaction, cancellationToken);
        await EnsureRootPathAvailableAsync(connection, rootId, normalizedNewRoot.Key, transaction, cancellationToken);
        var tracks = (await connection.QueryAsync<TrackPathRow>(new CommandDefinition(
            "SELECT Id, RelativePath FROM Tracks WHERE RootId = @RootId ORDER BY Id;",
            new { RootId = rootId },
            transaction,
            cancellationToken: cancellationToken))).ToArray();

        var existingRelativePaths = GetExistingAudioRelativePaths(normalizedNewRoot.DisplayPath);
        var registeredRelativePaths = tracks
            .Select(track => track.RelativePath)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var matchedTrackCount = 0;

        foreach (var track in tracks)
        {
            var isPresent = existingRelativePaths.Contains(track.RelativePath);
            if (isPresent)
            {
                matchedTrackCount++;
            }

            var newPath = Path.GetFullPath(Path.Combine(normalizedNewRoot.DisplayPath, track.RelativePath));
            await connection.ExecuteAsync(new CommandDefinition(
                """
                UPDATE Tracks
                SET Path = @Path,
                    IsMissing = @IsMissing,
                    UpdatedAtUtcTicks = @UpdatedAtUtcTicks
                WHERE Id = @TrackId;
                """,
                new
                {
                    Path = newPath,
                    IsMissing = isPresent ? 0 : 1,
                    UpdatedAtUtcTicks = DateTime.UtcNow.Ticks,
                    TrackId = track.Id,
                },
                transaction,
                cancellationToken: cancellationToken));
        }

        await connection.ExecuteAsync(new CommandDefinition(
            "UPDATE LibraryRoots SET Path = @Path, PathKey = @PathKey WHERE Id = @RootId AND LibraryId = @LibraryId;",
            new
            {
                Path = normalizedNewRoot.DisplayPath,
                PathKey = normalizedNewRoot.Key,
                RootId = rootId,
                LibraryId = libraryId,
            },
            transaction,
            cancellationToken: cancellationToken));

        await transaction.CommitAsync(cancellationToken);
        var unknownAudioFileCount = existingRelativePaths.Count(path => !registeredRelativePaths.Contains(path));
        return new LibraryRootRemapResult(
            libraryId,
            rootId,
            root.Path,
            normalizedNewRoot.DisplayPath,
            tracks.Length,
            matchedTrackCount,
            tracks.Length - matchedTrackCount,
            unknownAudioFileCount);
    }

    private static LibraryRootRemapPreview BuildPreview(
        long libraryId,
        RootRow root,
        string newRootPath,
        IReadOnlyList<string> registeredRelativePaths)
    {
        var existingRelativePaths = GetExistingAudioRelativePaths(newRootPath);
        var registered = registeredRelativePaths.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var missing = registeredRelativePaths
            .Where(path => !existingRelativePaths.Contains(path))
            .ToArray();
        var unknownCount = existingRelativePaths.Count(path => !registered.Contains(path));

        return new LibraryRootRemapPreview(
            libraryId,
            root.Id,
            root.Path,
            newRootPath,
            registeredRelativePaths.Count,
            registeredRelativePaths.Count - missing.Length,
            missing,
            unknownCount);
    }

    private static HashSet<string> GetExistingAudioRelativePaths(string rootPath)
    {
        // RemapはMetadata解析を行わず、正式対応予定の拡張子だけを相対Path対応確認に利用する。
        return Directory.EnumerateFiles(rootPath, "*", SearchOption.AllDirectories)
            .Where(path => string.Equals(Path.GetExtension(path), ".flac", StringComparison.OrdinalIgnoreCase)
                || string.Equals(Path.GetExtension(path), ".mp3", StringComparison.OrdinalIgnoreCase))
            .Select(path => Path.GetRelativePath(rootPath, path))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
    }

    private static async Task EnsureRootPathAvailableAsync(
        Microsoft.Data.Sqlite.SqliteConnection connection,
        long excludedRootId,
        string newRootKey,
        CancellationToken cancellationToken)
    {
        var roots = await connection.QueryAsync<RootKeyRow>(new CommandDefinition(
            "SELECT Id, Path, PathKey FROM LibraryRoots WHERE Id <> @ExcludedRootId;",
            new { ExcludedRootId = excludedRootId },
            cancellationToken: cancellationToken));
        EnsureNoOverlap(newRootKey, roots);
    }

    private static async Task EnsureRootPathAvailableAsync(
        Microsoft.Data.Sqlite.SqliteConnection connection,
        long excludedRootId,
        string newRootKey,
        System.Data.Common.DbTransaction transaction,
        CancellationToken cancellationToken)
    {
        var roots = await connection.QueryAsync<RootKeyRow>(new CommandDefinition(
            "SELECT Id, Path, PathKey FROM LibraryRoots WHERE Id <> @ExcludedRootId;",
            new { ExcludedRootId = excludedRootId },
            transaction,
            cancellationToken: cancellationToken));
        EnsureNoOverlap(newRootKey, roots);
    }

    private static void EnsureNoOverlap(string newRootKey, IEnumerable<RootKeyRow> roots)
    {
        var conflict = roots.FirstOrDefault(root => LibraryValueNormalizer.Overlaps(newRootKey, root.PathKey));
        if (conflict is not null)
        {
            throw new InvalidOperationException($"新しい対象フォルダは既存の対象フォルダと同一または包含関係にあります: {conflict.Path}");
        }
    }

    private static async Task<RootRow> GetRequiredRootAsync(
        Microsoft.Data.Sqlite.SqliteConnection connection,
        long libraryId,
        long rootId,
        CancellationToken cancellationToken)
        => await connection.QuerySingleOrDefaultAsync<RootRow>(new CommandDefinition(
            "SELECT Id, LibraryId, Path FROM LibraryRoots WHERE Id = @RootId AND LibraryId = @LibraryId;",
            new { RootId = rootId, LibraryId = libraryId },
            cancellationToken: cancellationToken))
            ?? throw new InvalidOperationException("指定した対象フォルダはライブラリに存在しません。");

    private static async Task<RootRow> GetRequiredRootAsync(
        Microsoft.Data.Sqlite.SqliteConnection connection,
        long libraryId,
        long rootId,
        System.Data.Common.DbTransaction transaction,
        CancellationToken cancellationToken)
        => await connection.QuerySingleOrDefaultAsync<RootRow>(new CommandDefinition(
            "SELECT Id, LibraryId, Path FROM LibraryRoots WHERE Id = @RootId AND LibraryId = @LibraryId;",
            new { RootId = rootId, LibraryId = libraryId },
            transaction,
            cancellationToken: cancellationToken))
            ?? throw new InvalidOperationException("指定した対象フォルダはライブラリに存在しません。");

    private sealed record RootRow(long Id, long LibraryId, string Path);
    private sealed record RootKeyRow(long Id, string Path, string PathKey);
    private sealed record TrackPathRow(long Id, string RelativePath);
}