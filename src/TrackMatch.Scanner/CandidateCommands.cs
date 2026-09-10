using TrackMatch.Core.Candidates;
using TrackMatch.Core.Comparison;
using TrackMatch.Infrastructure.Persistence;

internal static class CandidateCommands
{
    public static async Task<int> RunGenerateAsync(string[] args)
    {
        string? databasePath = null;
        var algorithm = 2;
        var segmentLength = 256;
        var stride = 128;
        var maximumDistance = 3;

        for (var i = 1; i < args.Length; i++)
        {
            if (TryReadStringOption(args, ref i, "--db", out var stringValue))
            {
                databasePath = stringValue;
                continue;
            }

            if (TryReadIntOption(args, ref i, "--algorithm", out var intValue))
            {
                algorithm = intValue;
                continue;
            }

            if (TryReadIntOption(args, ref i, "--segment-length", out intValue))
            {
                segmentLength = intValue;
                continue;
            }

            if (TryReadIntOption(args, ref i, "--stride", out intValue))
            {
                stride = intValue;
                continue;
            }

            if (TryReadIntOption(args, ref i, "--max-distance", out intValue))
            {
                maximumDistance = intValue;
                continue;
            }

            Console.Error.WriteLine($"不明または値が不正なオプション: {args[i]}");
            return 1;
        }

        if (string.IsNullOrWhiteSpace(databasePath))
        {
            Console.Error.WriteLine("--db <trackmatch.db> は必須である。");
            return 1;
        }

        try
        {
            var options = new CandidateGenerationOptions
            {
                SegmentLengthItems = segmentLength,
                SegmentStrideItems = stride,
                MaximumSegmentHashHammingDistance = maximumDistance,
            };
            options.Validate();

            var database = new SqliteDatabase(databasePath);
            await database.InitializeAsync();
            var service = new CandidateGenerationService(
                new SqliteFingerprintCatalogRepository(database),
                new SqliteCandidatePairRepository(database),
                new CandidatePairGenerator(new FingerprintSegmentSketcher()));
            var result = await service.GenerateAsync(algorithm, options);

            Console.WriteLine($"Tracks: {result.TrackCount}");
            Console.WriteLine($"Segments: {result.SegmentCount}");
            Console.WriteLine($"Candidate pairs: {result.Pairs.Count}");
            return 0;
        }
        catch (Exception exception) when (exception is IOException or InvalidDataException or InvalidOperationException or ArgumentException)
        {
            Console.Error.WriteLine(exception.Message);
            return 2;
        }
    }

    public static async Task<int> RunAnalyzeAsync(string[] args)
    {
        string? databasePath = null;
        var algorithm = 2;

        for (var i = 1; i < args.Length; i++)
        {
            if (TryReadStringOption(args, ref i, "--db", out var stringValue))
            {
                databasePath = stringValue;
                continue;
            }

            if (TryReadIntOption(args, ref i, "--algorithm", out var intValue))
            {
                algorithm = intValue;
                continue;
            }

            Console.Error.WriteLine($"不明または値が不正なオプション: {args[i]}");
            return 1;
        }

        if (string.IsNullOrWhiteSpace(databasePath))
        {
            Console.Error.WriteLine("--db <trackmatch.db> は必須である。");
            return 1;
        }

        try
        {
            var database = new SqliteDatabase(databasePath);
            await database.InitializeAsync();
            var service = new CandidateAnalysisService(
                new SqliteFingerprintCatalogRepository(database),
                new SqliteCandidatePairRepository(database),
                new SqliteCandidateComparisonRepository(database),
                new FingerprintComparer());
            var result = await service.AnalyzeAsync(algorithm);

            Console.WriteLine($"Candidates: {result.TotalCandidates}");
            Console.WriteLine($"Compared: {result.ComparedCandidates}");
            Console.WriteLine($"Skipped: {result.SkippedCandidates}");
            return result.SkippedCandidates == 0 ? 0 : 2;
        }
        catch (Exception exception) when (exception is IOException or InvalidDataException or InvalidOperationException or ArgumentException)
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

    private static bool TryReadIntOption(string[] args, ref int index, string option, out int value)
    {
        value = default;
        if (!string.Equals(args[index], option, StringComparison.OrdinalIgnoreCase) || index + 1 >= args.Length)
        {
            return false;
        }

        return int.TryParse(args[++index], out value);
    }
}
