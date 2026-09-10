using System.Text.Json;
using TrackMatch.Core.Candidates;
using TrackMatch.Core.Classification;
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
        string? profilePath = null;
        string? outputPath = null;
        var outputFormat = CandidateReportFormat.Csv;
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

            if (TryReadStringOption(args, ref i, "--profile", out stringValue))
            {
                profilePath = stringValue;
                continue;
            }

            if (TryReadStringOption(args, ref i, "--output", out stringValue))
            {
                outputPath = stringValue;
                continue;
            }

            if (TryReadStringOption(args, ref i, "--format", out stringValue)
                && CandidateReportWriter.TryParseFormat(stringValue, out outputFormat))
            {
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

        if (profilePath is null && (outputPath is not null || args.Any(item => string.Equals(item, "--format", StringComparison.OrdinalIgnoreCase))))
        {
            Console.Error.WriteLine("--output / --format を使う場合は --profile <thresholds.json> が必要である。");
            return 1;
        }

        try
        {
            var database = new SqliteDatabase(databasePath);
            await database.InitializeAsync();
            var comparisonRepository = new SqliteCandidateComparisonRepository(database);
            var service = new CandidateAnalysisService(
                new SqliteFingerprintCatalogRepository(database),
                new SqliteCandidatePairRepository(database),
                comparisonRepository,
                new FingerprintComparer());
            var result = await service.AnalyzeAsync(algorithm);

            if (profilePath is null)
            {
                Console.WriteLine($"Candidates: {result.TotalCandidates}");
                Console.WriteLine($"Compared: {result.ComparedCandidates}");
                Console.WriteLine($"Skipped: {result.SkippedCandidates}");
                return result.SkippedCandidates == 0 ? 0 : 2;
            }

            Console.Error.WriteLine($"Candidates: {result.TotalCandidates}");
            Console.Error.WriteLine($"Compared: {result.ComparedCandidates}");
            Console.Error.WriteLine($"Skipped: {result.SkippedCandidates}");

            var profileJson = File.ReadAllText(profilePath);
            var profile = JsonSerializer.Deserialize<RelationshipThresholdProfile>(
                profileJson,
                new JsonSerializerOptions
                {
                    PropertyNameCaseInsensitive = true,
                    RespectRequiredConstructorParameters = true,
                }) ?? throw new InvalidDataException("しきい値プロファイルを読み込めない。");
            var classificationService = new CandidateClassificationService(
                comparisonRepository,
                new SqliteCandidateClassificationRepository(database));
            var rows = await classificationService.ClassifyAsync(profile);
            await CandidateReportWriter.WriteAsync(rows, outputFormat, outputPath);
            Console.Error.WriteLine($"Classified: {rows.Count}");
            return result.SkippedCandidates == 0 ? 0 : 2;
        }
        catch (JsonException exception)
        {
            Console.Error.WriteLine($"しきい値プロファイルJSONが不正である: {exception.Message}");
            return 2;
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
