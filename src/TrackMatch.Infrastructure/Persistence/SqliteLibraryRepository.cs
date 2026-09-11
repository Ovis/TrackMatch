using Dapper;
using TrackMatch.Core.Libraries;
using TrackMatch.Core.Persistence;

namespace TrackMatch.Infrastructure.Persistence;

/// <summary>
/// SQLiteへ論理LibraryとRootを永続化する。
/// </summary>
public sealed class SqliteLibraryRepository(SqliteDatabase database) : ILibraryRepository
{
    /// <inheritdoc />
    public async Task<IReadOnlyList<Library>> GetAllAsync(CancellationToken cancellationToken = default)
    {
        await using var connection = await database.OpenConnectionAsync(cancellationToken);
        var libraries = (await connection.QueryAsync<LibraryRow>(new CommandDefinition(
            "SELECT Id, Name FROM Libraries ORDER BY Name COLLATE NOCASE, Id;",
            cancellationToken: cancellationToken))).ToArray();
        var roots = (await connection.QueryAsync<RootRow>(new CommandDefinition(
            "SELECT Id, LibraryId, Path FROM LibraryRoots ORDER BY Id;",
            cancellationToken: cancellationToken))).ToArray();

        return libraries
            .Select(library => new Library(
                library.Id,
                library.Name,
                roots.Where(root => root.LibraryId == library.Id)
                    .Select(ToModel)
                    .ToArray()))
            .ToArray();
    }

    /// <inheritdoc />
    public async Task<Library?> GetAsync(long libraryId, CancellationToken cancellationToken = default)
    {
        await using var connection = await database.OpenConnectionAsync(cancellationToken);
        var library = await connection.QuerySingleOrDefaultAsync<LibraryRow>(new CommandDefinition(
            "SELECT Id, Name FROM Libraries WHERE Id = @LibraryId;",
            new { LibraryId = libraryId },
            cancellationToken: cancellationToken));
        if (library is null)
        {
            return null;
        }

        var roots = (await connection.QueryAsync<RootRow>(new CommandDefinition(
            "SELECT Id, LibraryId, Path FROM LibraryRoots WHERE LibraryId = @LibraryId ORDER BY Id;",
            new { LibraryId = libraryId },
            cancellationToken: cancellationToken))).Select(ToModel).ToArray();
        return new Library(library.Id, library.Name, roots);
    }

    /// <inheritdoc />
    public async Task<Library> CreateAsync(
        string name,
        IReadOnlyCollection<string> rootPaths,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(rootPaths);
        if (rootPaths.Count == 0)
        {
            throw new ArgumentException("Libraryには1つ以上のRootが必要です。", nameof(rootPaths));
        }

        var normalizedName = LibraryValueNormalizer.NormalizeLibraryName(name);
        var normalizedRoots = rootPaths.Select(LibraryValueNormalizer.NormalizeRootPath).ToArray();
        EnsureNoOverlapWithin(normalizedRoots);

        await using var connection = await database.OpenConnectionAsync(cancellationToken);
        using var transaction = connection.BeginTransaction();
        await EnsureNameAvailableAsync(connection, transaction, normalizedName.Key, null, cancellationToken);
        await EnsureRootsAvailableAsync(connection, transaction, normalizedRoots.Select(x => x.Key), null, cancellationToken);

        var libraryId = await connection.ExecuteScalarAsync<long>(new CommandDefinition(
            """
            INSERT INTO Libraries (Name, NormalizedName)
            VALUES (@Name, @NormalizedName);
            SELECT last_insert_rowid();
            """,
            new { Name = normalizedName.DisplayName, NormalizedName = normalizedName.Key },
            transaction,
            cancellationToken: cancellationToken));

        var roots = new List<LibraryRoot>(normalizedRoots.Length);
        foreach (var root in normalizedRoots)
        {
            var rootId = await connection.ExecuteScalarAsync<long>(new CommandDefinition(
                """
                INSERT INTO LibraryRoots (LibraryId, Path, PathKey)
                VALUES (@LibraryId, @Path, @PathKey);
                SELECT last_insert_rowid();
                """,
                new { LibraryId = libraryId, Path = root.DisplayPath, PathKey = root.Key },
                transaction,
                cancellationToken: cancellationToken));
            roots.Add(new LibraryRoot(rootId, libraryId, root.DisplayPath));
        }

        transaction.Commit();
        return new Library(libraryId, normalizedName.DisplayName, roots);
    }

    /// <inheritdoc />
    public async Task RenameAsync(long libraryId, string name, CancellationToken cancellationToken = default)
    {
        var normalizedName = LibraryValueNormalizer.NormalizeLibraryName(name);
        await using var connection = await database.OpenConnectionAsync(cancellationToken);
        using var transaction = connection.BeginTransaction();
        await EnsureLibraryExistsAsync(connection, transaction, libraryId, cancellationToken);
        await EnsureNameAvailableAsync(connection, transaction, normalizedName.Key, libraryId, cancellationToken);

        await connection.ExecuteAsync(new CommandDefinition(
            "UPDATE Libraries SET Name = @Name, NormalizedName = @NormalizedName WHERE Id = @LibraryId;",
            new { Name = normalizedName.DisplayName, NormalizedName = normalizedName.Key, LibraryId = libraryId },
            transaction,
            cancellationToken: cancellationToken));
        transaction.Commit();
    }

    /// <inheritdoc />
    public async Task<LibraryRoot> AddRootAsync(long libraryId, string rootPath, CancellationToken cancellationToken = default)
    {
        var normalizedRoot = LibraryValueNormalizer.NormalizeRootPath(rootPath);
        await using var connection = await database.OpenConnectionAsync(cancellationToken);
        using var transaction = connection.BeginTransaction();
        await EnsureLibraryExistsAsync(connection, transaction, libraryId, cancellationToken);
        await EnsureRootsAvailableAsync(connection, transaction, [normalizedRoot.Key], null, cancellationToken);

        var rootId = await connection.ExecuteScalarAsync<long>(new CommandDefinition(
            """
            INSERT INTO LibraryRoots (LibraryId, Path, PathKey)
            VALUES (@LibraryId, @Path, @PathKey);
            SELECT last_insert_rowid();
            """,
            new { LibraryId = libraryId, Path = normalizedRoot.DisplayPath, PathKey = normalizedRoot.Key },
            transaction,
            cancellationToken: cancellationToken));
        transaction.Commit();
        return new LibraryRoot(rootId, libraryId, normalizedRoot.DisplayPath);
    }

    /// <inheritdoc />
    public async Task RemoveRootAsync(long libraryId, long rootId, CancellationToken cancellationToken = default)
    {
        await using var connection = await database.OpenConnectionAsync(cancellationToken);
        using var transaction = connection.BeginTransaction();
        await EnsureLibraryExistsAsync(connection, transaction, libraryId, cancellationToken);

        var rootCount = await connection.ExecuteScalarAsync<long>(new CommandDefinition(
            "SELECT COUNT(*) FROM LibraryRoots WHERE LibraryId = @LibraryId;",
            new { LibraryId = libraryId },
            transaction,
            cancellationToken: cancellationToken));
        if (rootCount <= 1)
        {
            throw new InvalidOperationException("Libraryの最後のRootは削除できません。代替Rootを追加するかLibrary自体を削除してください。");
        }

        var affected = await connection.ExecuteAsync(new CommandDefinition(
            "DELETE FROM LibraryRoots WHERE Id = @RootId AND LibraryId = @LibraryId;",
            new { RootId = rootId, LibraryId = libraryId },
            transaction,
            cancellationToken: cancellationToken));
        if (affected == 0)
        {
            throw new InvalidOperationException("指定したRootはLibraryに存在しません。");
        }

        transaction.Commit();
    }

    /// <inheritdoc />
    public async Task DeleteAsync(long libraryId, CancellationToken cancellationToken = default)
    {
        await using var connection = await database.OpenConnectionAsync(cancellationToken);
        var affected = await connection.ExecuteAsync(new CommandDefinition(
            "DELETE FROM Libraries WHERE Id = @LibraryId;",
            new { LibraryId = libraryId },
            cancellationToken: cancellationToken));
        if (affected == 0)
        {
            throw new InvalidOperationException("指定したLibraryは存在しません。");
        }
    }

    private static void EnsureNoOverlapWithin((string DisplayPath, string Key)[] roots)
    {
        for (var i = 0; i < roots.Length; i++)
        {
            for (var j = i + 1; j < roots.Length; j++)
            {
                if (LibraryValueNormalizer.Overlaps(roots[i].Key, roots[j].Key))
                {
                    throw new InvalidOperationException($"Root同士を同一または包含関係にはできません: {roots[i].DisplayPath} / {roots[j].DisplayPath}");
                }
            }
        }
    }

    private static async Task EnsureNameAvailableAsync(
        Microsoft.Data.Sqlite.SqliteConnection connection,
        Microsoft.Data.Sqlite.SqliteTransaction transaction,
        string normalizedName,
        long? excludedLibraryId,
        CancellationToken cancellationToken)
    {
        var count = await connection.ExecuteScalarAsync<long>(new CommandDefinition(
            "SELECT COUNT(*) FROM Libraries WHERE NormalizedName = @NormalizedName AND (@ExcludedLibraryId IS NULL OR Id <> @ExcludedLibraryId);",
            new { NormalizedName = normalizedName, ExcludedLibraryId = excludedLibraryId },
            transaction,
            cancellationToken: cancellationToken));
        if (count != 0)
        {
            throw new InvalidOperationException("同名のLibraryが既に存在します。");
        }
    }

    private static async Task EnsureRootsAvailableAsync(
        Microsoft.Data.Sqlite.SqliteConnection connection,
        Microsoft.Data.Sqlite.SqliteTransaction transaction,
        IEnumerable<string> rootKeys,
        long? excludedRootId,
        CancellationToken cancellationToken)
    {
        var existing = (await connection.QueryAsync<RootKeyRow>(new CommandDefinition(
            "SELECT Id, Path, PathKey FROM LibraryRoots WHERE @ExcludedRootId IS NULL OR Id <> @ExcludedRootId;",
            new { ExcludedRootId = excludedRootId },
            transaction,
            cancellationToken: cancellationToken))).ToArray();

        foreach (var key in rootKeys)
        {
            var conflict = existing.FirstOrDefault(root => LibraryValueNormalizer.Overlaps(key, root.PathKey));
            if (conflict is not null)
            {
                throw new InvalidOperationException($"Rootは既存Rootと同一または包含関係にあります: {conflict.Path}");
            }
        }
    }

    private static async Task EnsureLibraryExistsAsync(
        Microsoft.Data.Sqlite.SqliteConnection connection,
        Microsoft.Data.Sqlite.SqliteTransaction transaction,
        long libraryId,
        CancellationToken cancellationToken)
    {
        var exists = await connection.ExecuteScalarAsync<long>(new CommandDefinition(
            "SELECT COUNT(*) FROM Libraries WHERE Id = @LibraryId;",
            new { LibraryId = libraryId },
            transaction,
            cancellationToken: cancellationToken));
        if (exists == 0)
        {
            throw new InvalidOperationException("指定したLibraryは存在しません。");
        }
    }

    private static LibraryRoot ToModel(RootRow row) => new(row.Id, row.LibraryId, row.Path);

    private sealed record LibraryRow(long Id, string Name);
    private sealed record RootRow(long Id, long LibraryId, string Path);
    private sealed record RootKeyRow(long Id, string Path, string PathKey);
}
