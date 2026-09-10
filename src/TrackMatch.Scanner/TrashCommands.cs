using TrackMatch.Core.Trash;
using TrackMatch.Infrastructure.Persistence;
using TrackMatch.Infrastructure.Trash;

internal static class TrashCommands
{
    public static async Task<int> RunAsync(string[] args)
    {
        string? databasePath = null;
        string? libraryRoot = null;
        string? trashRoot = null;
        var execute = false;

        for (var i = 1; i < args.Length; i++)
        {
            if (TryReadStringOption(args, ref i, "--db", out var stringValue))
            {
                databasePath = stringValue;
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

            if (string.Equals(args[i], "--execute", StringComparison.OrdinalIgnoreCase))
            {
                execute = true;
                continue;
            }

            Console.Error.WriteLine($"不明または値が不正なオプション: {args[i]}");
            return 1;
        }

        if (string.IsNullOrWhiteSpace(databasePath)
            || string.IsNullOrWhiteSpace(libraryRoot)
            || string.IsNullOrWhiteSpace(trashRoot))
        {
            Console.Error.WriteLine("--db、--library-root、--trash-root は必須である。");
            return 1;
        }

        try
        {
            var database = new SqliteDatabase(databasePath);
            await database.InitializeAsync();
            var tracks = new SqliteTrackRepository(database);
            var service = new RejectedTrackTrashService(
                new SqliteCandidateReviewRepository(database),
                new SqliteTrackLookupRepository(database),
                tracks,
                new LocalTrackFileOperations());
            var result = await service.ProcessAsync(libraryRoot, trashRoot, execute);

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
}
