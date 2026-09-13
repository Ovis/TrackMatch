using System.Text.Json;
using TrackMatch.Application;
using TrackMatch.Core.Candidates;
using TrackMatch.Core.Classification;
using TrackMatch.Core.Duplicates;
using TrackMatch.Infrastructure.Persistence;

internal static class CandidateCommands
{
    public static async Task<int> RunGenerateAsync(string[] args)
    {
        var databasePath = TrackMatchDataPaths.DefaultDatabasePath;
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

        try
        {
            var options = new CandidateGenerationOptions
            {
                SegmentLengthItems = segmentLength,
                SegmentStrideItems = stride,
                MaximumSegmentHashHammingDistance = maximumDistance,
            };
            options.Validate();

            var workflow = new LibraryAnalysisWorkflow(databasePath, fingerprintAlgorithm: algorithm);
            var result = await workflow.GenerateCandidatesAsync(options);

            Console.WriteLine($"Tracks: {result.TrackCount}");
            Console.WriteLine($"Segments: {result.SegmentCount}");
            Console.WriteLine($"Indexed tracks: {result.IndexedTrackCount}");
            Console.WriteLine($"Mode: {(result.IsFullRebuild ? "full" : "incremental")}");
            Console.WriteLine($"Candidate pairs updated: {result.Pairs.Count}");
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
        var databasePath = TrackMatchDataPaths.DefaultDatabasePath;
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

        if (profilePath is null && (outputPath is not null || args.Any(item => string.Equals(item, "--format", StringComparison.OrdinalIgnoreCase))))
        {
            Console.Error.WriteLine("--output / --format を使う場合は --profile <thresholds.json> が必要である。");
            return 1;
        }

        try
        {
            if (profilePath is null)
            {
                var workflow = new LibraryAnalysisWorkflow(databasePath, fingerprintAlgorithm: algorithm);
                var result = await workflow.AnalyzeCandidatesAsync();

                Console.WriteLine($"Candidates: {result.TotalCandidates}");
                Console.WriteLine($"Compared: {result.ComparedCandidates}");
                Console.WriteLine($"Reused: {result.ReusedCandidates}");
                Console.WriteLine($"Skipped: {result.SkippedCandidates}");
                return result.SkippedCandidates == 0 ? 0 : 2;
            }

            var database = new SqliteDatabase(databasePath);
            await database.InitializeAsync();
            var comparisonRepository = new SqliteCandidateComparisonRepository(database);

            // 閾値調整は繰り返し行うため、再分類では保存済みの詳細比較値を再利用しraw Fingerprint比較をやり直さない。
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
            return 0;
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

    public static async Task<int> RunReviewAsync(string[] args)
    {
        var databasePath = TrackMatchDataPaths.DefaultDatabasePath;
        string? note = null;
        long? libraryId = null;
        long? trackIdA = null;
        long? trackIdB = null;
        long? keepTrackId = null;
        var decision = CandidateReviewDecision.NotDuplicate;

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

            if (TryReadLongOption(args, ref i, "--track-a", out longValue))
            {
                trackIdA = longValue;
                continue;
            }

            if (TryReadLongOption(args, ref i, "--track-b", out longValue))
            {
                trackIdB = longValue;
                continue;
            }

            if (TryReadLongOption(args, ref i, "--keep", out longValue))
            {
                keepTrackId = longValue;
                continue;
            }

            if (TryReadStringOption(args, ref i, "--decision", out stringValue)
                && TryParseReviewDecision(stringValue, out decision))
            {
                continue;
            }

            if (TryReadStringOption(args, ref i, "--note", out stringValue))
            {
                note = stringValue;
                continue;
            }

            Console.Error.WriteLine($"不明または値が不正なオプション: {args[i]}");
            return 1;
        }

        if (trackIdA is null || trackIdB is null)
        {
            Console.Error.WriteLine("--track-a、--track-b は必須である。");
            return 1;
        }

        try
        {
            var pair = CandidatePairKey.Create(trackIdA.Value, trackIdB.Value);
            var review = new CandidateReview(pair, decision, note, keepTrackId);
            review.Validate();

            var database = new SqliteDatabase(databasePath);
            await database.InitializeAsync();
            var trackLookup = new SqliteTrackLookupRepository(database);
            var resolvedLibraryId = libraryId ?? await ResolveUniqueCommonLibraryAsync(trackLookup, pair);
            var reviews = new SqliteCandidateReviewRepository(database, resolvedLibraryId);
            var groups = new SqliteDuplicateGroupRepository(database);
            var service = new DuplicateGroupService(reviews, trackLookup, groups);
            await service.SaveReviewAsync(resolvedLibraryId, review);

            if (decision == CandidateReviewDecision.ConfirmedDuplicate)
            {
                var discardTrackId = keepTrackId == pair.TrackIdA ? pair.TrackIdB : pair.TrackIdA;
                Console.WriteLine($"ConfirmedDuplicateとして記録: {pair.TrackIdA} <-> {pair.TrackIdB}, Keep: {keepTrackId}, Reject: {discardTrackId}, Library: {resolvedLibraryId}");
            }
            else
            {
                Console.WriteLine($"NotDuplicateとして記録: {pair.TrackIdA} <-> {pair.TrackIdB}, Library: {resolvedLibraryId}");
            }

            return 0;
        }
        catch (Exception exception) when (exception is IOException or InvalidDataException or InvalidOperationException or ArgumentException)
        {
            Console.Error.WriteLine(exception.Message);
            return 2;
        }
    }

    private static async Task<long> ResolveUniqueCommonLibraryAsync(
        SqliteTrackLookupRepository trackLookup,
        CandidatePairKey pair)
    {
        var librariesA = await trackLookup.GetLibraryIdsAsync(pair.TrackIdA);
        var librariesB = await trackLookup.GetLibraryIdsAsync(pair.TrackIdB);
        var common = librariesA.Intersect(librariesB).Order().ToArray();
        return common.Length switch
        {
            1 => common[0],
            0 => throw new InvalidOperationException("候補ペアを同時に含むLibraryがありません。--library-id を指定してもMembership外ではレビューできません。"),
            _ => throw new InvalidOperationException("候補ペアが複数Libraryに所属しています。Library固有Keepの対象を明確にするため --library-id を指定してください。"),
        };
    }

    private static bool TryParseReviewDecision(string value, out CandidateReviewDecision decision)
    {
        if (string.Equals(value, "not-duplicate", StringComparison.OrdinalIgnoreCase)
            || string.Equals(value, "notduplicate", StringComparison.OrdinalIgnoreCase))
        {
            decision = CandidateReviewDecision.NotDuplicate;
            return true;
        }

        if (string.Equals(value, "duplicate", StringComparison.OrdinalIgnoreCase)
            || string.Equals(value, "confirmed-duplicate", StringComparison.OrdinalIgnoreCase))
        {
            decision = CandidateReviewDecision.ConfirmedDuplicate;
            return true;
        }

        decision = default;
        return false;
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

    private static bool TryReadLongOption(string[] args, ref int index, string option, out long value)
    {
        value = default;
        if (!string.Equals(args[index], option, StringComparison.OrdinalIgnoreCase) || index + 1 >= args.Length)
        {
            return false;
        }

        return long.TryParse(args[++index], out value);
    }
}
