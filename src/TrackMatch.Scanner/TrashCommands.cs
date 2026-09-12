using TrackMatch.Core.Trash;
using TrackMatch.Infrastructure.Persistence;
using TrackMatch.Infrastructure.Trash;

internal static class TrashCommands
{
    public static async Task<int> RunAsync(string[] args)
    {
        var databasePath = TrackMatchDataPaths.DefaultDatabasePath;
        long? libraryId = null;
        string? libraryRoot = null;
        string? trashRoot = null;
        var execute = false;
        var collisionBehavior = TrashDestinationCollisionBehavior.Skip;

        for (var i = 1; i < args.Length; i++)
        {
            if (TryReadStringOption(args, ref i, "--db", out var stringValue))
            {
                databasePath = stringValue;
                continue;
            }

            if (TryReadLongOption(args, ref i, "--library-id", out var longValue))
            {
                libraryId = longValue;
                continue;
            }

            if (TryReadStringOption(args, ref i, "--library-root", out stringValue))
            {
                libraryRoot = stringValue;
                continue;
            }

            if (TryReadStringOption(args, ref i, "--trash-root", out stringValue))
            {
                trashRoot = stringValue;
                continue;
            }

            if (string.Equals(args[i], "--rename-collision", StringComparison.OrdinalIgnoreCase))
            {
                collisionBehavior = TrashDestinationCollisionBehavior.Rename;
                continue;
            }

            if (string.Equals(args[i], "--execute", StringComparison.OrdinalIgnoreCase))
            {
                execute = true;
                continue;
            }

            Console.Error.WriteLine($"不明または値が不正なオプション: {args[i]}");
            return 1;
        }

        if ((libraryId is null && string.IsNullOrWhiteSpace(libraryRoot)) || string.IsNullOrWhiteSpace(trashRoot))
        {
            Console.Error.WriteLine("--library-id または --library-root のどちらかと、--trash-root は必須である。");
            return 1;
        }

        try
        {
            var database = new SqliteDatabase(databasePath);
            await database.InitializeAsync();

            var libraries = await new SqliteLibraryRepository(database).GetAllAsync();
            var resolvedLibraryId = libraryId;
            if (resolvedLibraryId is null && libraryRoot is not null)
            {
                var normalizedRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(libraryRoot));
                var matches = libraries
                    .Where(library => library.Roots.Any(root =>
                        string.Equals(
                            Path.TrimEndingDirectorySeparator(Path.GetFullPath(root.Path)),
                            normalizedRoot,
                            StringComparison.OrdinalIgnoreCase)))
                    .ToArray();
                if (matches.Length != 1)
                {
                    Console.Error.WriteLine($"--library-root からLibraryを一意に解決できない: {libraryRoot}");
                    return 1;
                }

                // 旧CLI利用者を壊さないため、登録済みRootの完全一致だけをLibraryIdへ変換して新Trash処理へ流す。
                resolvedLibraryId = matches[0].Id;
            }

            if (resolvedLibraryId is null || !libraries.Any(library => library.Id == resolvedLibraryId.Value))
            {
                Console.Error.WriteLine($"Libraryが存在しない: {resolvedLibraryId}");
                return 1;
            }

            // CLI経由でもGUIと同じ安全条件を適用し、他Libraryを含むRootとの包含関係を許可しない。
            TrashPathRules.ValidateRootSeparation(
                trashRoot,
                libraries.SelectMany(library => library.Roots).Select(root => root.Path));

            var tracks = new SqliteTrackRepository(database);
            var service = new RejectedTrackTrashService(
                new SqliteCandidateReviewRepository(database),
                new SqliteTrackLookupRepository(database),
                tracks,
                new LocalTrackFileOperations());
            var result = await service.ProcessAsync(
                resolvedLibraryId.Value,
                trashRoot,
                execute,
                collisionBehavior);

            Console.WriteLine("Status\tTrackId\tSource\tDestination\tMessage");
            foreach (var item in result.Items)
            {
                Console.WriteLine(string.Join(
                    '\t',
                    item.Status,
                    item.TrackId,
                    item.SourcePath ?? string.Empty,
                    item.DestinationPath ?? string.Empty,
                    item.Message ?? string.Empty));
            }

            Console.Error.WriteLine(
                execute
                    ? $"Moved: {result.MovedCount}, Blocked: {result.BlockedCount}"
                    : $"Dry-run Ready: {result.ReadyCount}, Blocked: {result.BlockedCount}. 実行するには --execute を指定する。");
            return result.BlockedCount == 0 ? 0 : 2;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidDataException or InvalidOperationException or ArgumentException)
        {
            Console.Error.WriteLine(exception.Message);
            return 2;
        }
    }

    private static bool TryReadStringOption(string[] args, ref int index, string option, out string value)
    {
        value = string.Empty;
        if (!string.Equals(args[index], option, StringComparison.OrdinalIgnoreCase) || index + 1 >= args.Length)
        {
            return false;
        }

        value = args[++index];
        return !string.IsNullOrWhiteSpace(value);
    }

    private static bool TryReadLongOption(string[] args, ref int index, string option, out long value)
    {
        value = default;
        if (!string.Equals(args[index], option, StringComparison.OrdinalIgnoreCase) || index + 1 >= args.Length)
        {
            return false;
        }

        return long.TryParse(args[++index], out value) && value > 0;
    }
}
