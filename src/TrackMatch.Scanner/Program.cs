using System.Globalization;
using System.Text;
using System.Text.Json;
using TrackMatch.Core.Classification;
using TrackMatch.Core.Comparison;
using TrackMatch.Core.Probe;
using TrackMatch.Core.Scanning;
using TrackMatch.Infrastructure.Audio;
using TrackMatch.Infrastructure.Chromaprint;
using TrackMatch.Infrastructure.Persistence;
using TrackMatch.Infrastructure.Scanning;

return await RunAsync(args);

static async Task<int> RunAsync(string[] args)
{
    if (args.Length == 0)
    {
        PrintUsage();
        return 1;
    }

    if (string.Equals(args[0], "scan", StringComparison.OrdinalIgnoreCase))
    {
        return await RunScanAsync(args);
    }

    if (string.Equals(args[0], "generate-candidates", StringComparison.OrdinalIgnoreCase))
    {
        return await CandidateCommands.RunGenerateAsync(args);
    }

    if (string.Equals(args[0], "analyze-candidates", StringComparison.OrdinalIgnoreCase))
    {
        return await CandidateCommands.RunAnalyzeAsync(args);
    }

    if (string.Equals(args[0], "review-candidate", StringComparison.OrdinalIgnoreCase))
    {
        return await CandidateCommands.RunReviewAsync(args);
    }

    if (string.Equals(args[0], "trash-reviewed", StringComparison.OrdinalIgnoreCase))
    {
        return await TrashCommands.RunAsync(args);
    }

    if (string.Equals(args[0], "compare", StringComparison.OrdinalIgnoreCase))
    {
        return await RunCompareAsync(args);
    }

    if (string.Equals(args[0], "probe", StringComparison.OrdinalIgnoreCase))
    {
        return await RunProbeAsync(args);
    }

    if (string.Equals(args[0], "analyze-probe", StringComparison.OrdinalIgnoreCase))
    {
        return RunAnalyzeProbe(args);
    }

    if (string.Equals(args[0], "classify-probe", StringComparison.OrdinalIgnoreCase))
    {
        return RunClassifyProbe(args);
    }

    PrintUsage();
    return 1;
}

static async Task<int> RunScanAsync(string[] args)
{
    if (args.Length < 2)
    {
        PrintUsage();
        return 1;
    }

    var rootPath = args[1];
    var databasePath = TrackMatchDataPaths.DefaultDatabasePath;
    var fpcalcPath = Environment.GetEnvironmentVariable("TRACKMATCH_FPCALC") ?? "fpcalc";
    for (var i = 2; i < args.Length; i++)
    {
        if (string.Equals(args[i], "--db", StringComparison.OrdinalIgnoreCase) && i + 1 < args.Length)
        {
            databasePath = args[++i];
            continue;
        }

        if (string.Equals(args[i], "--fpcalc", StringComparison.OrdinalIgnoreCase) && i + 1 < args.Length)
        {
            fpcalcPath = args[++i];
            continue;
        }

        Console.Error.WriteLine($"不明なオプション: {args[i]}");
        return 1;
    }

    try
    {
        // 通常利用ではGUIと同じ標準DBを使い、初回scanがDB作成とライブラリ登録を担う。
        // --dbは検証用DBなどへ明示的に切り替える場合の上書き手段として残す。
        var database = new SqliteDatabase(databasePath);
        await database.InitializeAsync();
        var service = new IncrementalLibraryScanService(
            new FlacLibraryScanner(new FlacMetadataReader()),
            new SqliteTrackRepository(database),
            new SqliteScanSessionRepository(database),
            new FpcalcFingerprintExtractor(fpcalcPath),
            fingerprintAlgorithm: 2);
        var result = await service.ScanAsync(rootPath);
        foreach (var error in result.Errors)
        {
            Console.Error.WriteLine($"ERROR\t{error.Stage}\t{error.Path}\t{error.Message}");
        }

        var summary = result.Summary;
        Console.Error.WriteLine(
            $"Session: {result.SessionId}, Scanned: {summary.TotalFiles}, Processed: {summary.ProcessedFiles}, " +
            $"Added: {summary.AddedFiles}, Updated: {summary.UpdatedFiles}, Missing: {summary.RemovedFiles}, Errors: {summary.ErrorCount}");
        return summary.ErrorCount == 0 ? 0 : 2;
    }
    catch (DirectoryNotFoundException exception)
    {
        Console.Error.WriteLine(exception.Message);
        return 1;
    }
    catch (Exception exception) when (exception is IOException or InvalidDataException or InvalidOperationException)
    {
        Console.Error.WriteLine(exception.Message);
        return 2;
    }
}

static async Task<int> RunCompareAsync(string[] args)
{
    if (args.Length < 3)
    {
        PrintUsage();
        return 1;
    }

    var fpcalcPath = Environment.GetEnvironmentVariable("TRACKMATCH_FPCALC") ?? "fpcalc";
    var csv = false;

    for (var i = 3; i < args.Length; i++)
    {
        if (string.Equals(args[i], "--csv", StringComparison.OrdinalIgnoreCase))
        {
            csv = true;
            continue;
        }

        if (string.Equals(args[i], "--fpcalc", StringComparison.OrdinalIgnoreCase) && i + 1 < args.Length)
        {
            fpcalcPath = args[++i];
            continue;
        }

        Console.Error.WriteLine($"不明なオプション: {args[i]}");
        return 1;
    }

    try
    {
        var extractor = new FpcalcFingerprintExtractor(fpcalcPath);
        var fingerprintATask = extractor.ExtractAsync(args[1]);
        var fingerprintBTask = extractor.ExtractAsync(args[2]);
        await Task.WhenAll(fingerprintATask, fingerprintBTask);

        var a = await fingerprintATask;
        var b = await fingerprintBTask;
        var result = new FingerprintComparer().Compare(a, b);

        if (csv)
        {
            WriteCsvResult(a, b, result);
        }
        else
        {
            WriteTextResult(a, b, result);
        }

        return 0;
    }
    catch (Exception exception) when (exception is IOException or InvalidDataException or InvalidOperationException)
    {
        Console.Error.WriteLine(exception.Message);
        return 2;
    }
}

static async Task<int> RunProbeAsync(string[] args)
{
    if (args.Length < 2)
    {
        PrintUsage();
        return 1;
    }

    var inputPath = args[1];
    string? outputPath = null;
    var fpcalcPath = Environment.GetEnvironmentVariable("TRACKMATCH_FPCALC") ?? "fpcalc";

    for (var i = 2; i < args.Length; i++)
    {
        if (string.Equals(args[i], "--output", StringComparison.OrdinalIgnoreCase) && i + 1 < args.Length)
        {
            outputPath = args[++i];
            continue;
        }

        if (string.Equals(args[i], "--fpcalc", StringComparison.OrdinalIgnoreCase) && i + 1 < args.Length)
        {
            fpcalcPath = args[++i];
            continue;
        }

        Console.Error.WriteLine($"不明なオプション: {args[i]}");
        return 1;
    }

    try
    {
        var pairs = ProbeCsv.ReadPairs(inputPath);
        var runner = new ProbeRunner(new FpcalcFingerprintExtractor(fpcalcPath), new FingerprintComparer());

        using var output = outputPath is null
            ? new StreamWriter(Console.OpenStandardOutput(), new UTF8Encoding(encoderShouldEmitUTF8Identifier: false), leaveOpen: true)
            : new StreamWriter(outputPath, append: false, new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));

        ProbeCsv.WriteHeader(output);
        var processed = 0;
        await foreach (var result in runner.RunAsync(pairs))
        {
            ProbeCsv.WriteResult(output, result);
            await output.FlushAsync();
            processed++;
            Console.Error.WriteLine($"Probe: {processed}/{pairs.Count} {result.Pair.Label}");
        }

        return 0;
    }
    catch (Exception exception) when (exception is IOException or InvalidDataException or InvalidOperationException)
    {
        Console.Error.WriteLine(exception.Message);
        return 2;
    }
}

static int RunAnalyzeProbe(string[] args)
{
    if (args.Length != 2)
    {
        PrintUsage();
        return 1;
    }

    try
    {
        var measurements = ProbeAnalysisCsv.ReadMeasurements(args[1]);
        var statistics = new ProbeAnalyzer().Analyze(measurements);

        Console.WriteLine("Relation\tCount\tSimilarity(min/median/max)\tMinCoverage(min/median/max)\tMaxCoverage(min/median/max)\tDurationRatio(min/median/max)");
        foreach (var item in statistics)
        {
            Console.WriteLine(string.Join(
                '\t',
                item.ExpectedRelation,
                item.Count.ToString(CultureInfo.InvariantCulture),
                FormatStatistics(item.Similarity),
                FormatStatistics(item.MinimumCoverage),
                FormatStatistics(item.MaximumCoverage),
                FormatStatistics(item.DurationRatio)));
        }

        return 0;
    }
    catch (Exception exception) when (exception is IOException or InvalidDataException or InvalidOperationException)
    {
        Console.Error.WriteLine(exception.Message);
        return 2;
    }
}

static int RunClassifyProbe(string[] args)
{
    if (args.Length != 4 || !string.Equals(args[2], "--profile", StringComparison.OrdinalIgnoreCase))
    {
        PrintUsage();
        return 1;
    }

    try
    {
        var measurements = ProbeAnalysisCsv.ReadMeasurements(args[1]);
        var profileJson = File.ReadAllText(args[3], Encoding.UTF8);
        var profile = JsonSerializer.Deserialize<RelationshipThresholdProfile>(
            profileJson,
            new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true,
                RespectRequiredConstructorParameters = true,
            }) ?? throw new InvalidDataException("しきい値プロファイルを読み込めない。");

        var classifier = new RelationshipClassifier(profile);
        Console.WriteLine("ExpectedRelation\tPredictedRelation\tSimilarity\tMinCoverage\tMaxCoverage\tDurationRatio\tReason");
        foreach (var measurement in measurements)
        {
            var result = classifier.Classify(measurement);
            Console.WriteLine(string.Join(
                '\t',
                measurement.ExpectedRelation,
                result.Kind,
                measurement.Similarity.ToString("F6", CultureInfo.InvariantCulture),
                measurement.MinimumCoverage.ToString("F6", CultureInfo.InvariantCulture),
                measurement.MaximumCoverage.ToString("F6", CultureInfo.InvariantCulture),
                measurement.DurationRatio.ToString("F6", CultureInfo.InvariantCulture),
                result.Reason));
        }

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

static string FormatStatistics(ProbeMetricStatistics statistics)
{
    return string.Create(
        CultureInfo.InvariantCulture,
        $"{statistics.Minimum:F4}/{statistics.Median:F4}/{statistics.Maximum:F4}");
}

static void WriteTextResult(
    TrackMatch.Core.Fingerprinting.AudioFingerprint a,
    TrackMatch.Core.Fingerprinting.AudioFingerprint b,
    FingerprintComparisonResult result)
{
    Console.WriteLine($"File A            : {a.Path}");
    Console.WriteLine($"File B            : {b.Path}");
    Console.WriteLine($"Duration A        : {a.Duration.TotalSeconds:F3} sec");
    Console.WriteLine($"Duration B        : {b.Duration.TotalSeconds:F3} sec");
    Console.WriteLine($"Fingerprint A     : {a.Values.Count} items");
    Console.WriteLine($"Fingerprint B     : {b.Values.Count} items");
    Console.WriteLine($"Similarity        : {result.Similarity:P2}");
    Console.WriteLine($"Matched duration  : {result.MatchedDuration.TotalSeconds:F3} sec");
    Console.WriteLine($"Coverage A        : {result.CoverageA:P2}");
    Console.WriteLine($"Coverage B        : {result.CoverageB:P2}");
    Console.WriteLine($"Best offset       : {result.BestOffset.TotalSeconds:+0.000;-0.000;0.000} sec ({result.BestOffsetItems:+#;-#;0} items)");
    Console.WriteLine($"Duration ratio    : {Math.Min(a.Duration.TotalSeconds, b.Duration.TotalSeconds) / Math.Max(a.Duration.TotalSeconds, b.Duration.TotalSeconds):P2}");
}

static void WriteCsvResult(
    TrackMatch.Core.Fingerprinting.AudioFingerprint a,
    TrackMatch.Core.Fingerprinting.AudioFingerprint b,
    FingerprintComparisonResult result)
{
    Console.WriteLine("FileA,FileB,DurationASeconds,DurationBSeconds,FingerprintLengthA,FingerprintLengthB,BestOffsetSeconds,Similarity,MatchedDurationSeconds,CoverageA,CoverageB,DurationRatio");
    Console.WriteLine(string.Join(",",
        Csv(a.Path),
        Csv(b.Path),
        a.Duration.TotalSeconds.ToString("F3", CultureInfo.InvariantCulture),
        b.Duration.TotalSeconds.ToString("F3", CultureInfo.InvariantCulture),
        a.Values.Count.ToString(CultureInfo.InvariantCulture),
        b.Values.Count.ToString(CultureInfo.InvariantCulture),
        result.BestOffset.TotalSeconds.ToString("F3", CultureInfo.InvariantCulture),
        result.Similarity.ToString("F6", CultureInfo.InvariantCulture),
        result.MatchedDuration.TotalSeconds.ToString("F3", CultureInfo.InvariantCulture),
        result.CoverageA.ToString("F6", CultureInfo.InvariantCulture),
        result.CoverageB.ToString("F6", CultureInfo.InvariantCulture),
        (Math.Min(a.Duration.TotalSeconds, b.Duration.TotalSeconds) / Math.Max(a.Duration.TotalSeconds, b.Duration.TotalSeconds)).ToString("F6", CultureInfo.InvariantCulture)));
}

static string Csv(string value)
{
    if (value.IndexOfAny([',', '"', '\r', '\n']) < 0)
    {
        return value;
    }

    return $"\"{value.Replace("\"", "\"\"", StringComparison.Ordinal)}\"";
}

static void PrintUsage()
{
    Console.Error.WriteLine("Usage:");
    Console.Error.WriteLine("  TrackMatch.Scanner scan <folder> [--db <trackmatch.db>] [--fpcalc <path>]");
    Console.Error.WriteLine("  TrackMatch.Scanner generate-candidates [--db <trackmatch.db>] [--algorithm <n>] [--segment-length <items>] [--stride <items>] [--max-distance <0..3>]");
    Console.Error.WriteLine("  TrackMatch.Scanner analyze-candidates [--db <trackmatch.db>] [--algorithm <n>] [--profile <thresholds.json>] [--format csv|text] [--output <path>]");
    Console.Error.WriteLine("  TrackMatch.Scanner review-candidate [--db <trackmatch.db>] --track-a <id> --track-b <id> [--decision not-duplicate|duplicate] [--keep <id>] [--note <text>]");
    Console.Error.WriteLine("  TrackMatch.Scanner trash-reviewed [--db <trackmatch.db>] --library-root <folder> --trash-root <folder> [--execute]");
    Console.Error.WriteLine("  TrackMatch.Scanner compare <file-a> <file-b> [--csv] [--fpcalc <path>]");
    Console.Error.WriteLine("  TrackMatch.Scanner probe <pairs.csv> [--output <results.csv>] [--fpcalc <path>]");
    Console.Error.WriteLine("  TrackMatch.Scanner analyze-probe <results.csv>");
    Console.Error.WriteLine("  TrackMatch.Scanner classify-probe <results.csv> --profile <thresholds.json>");
    Console.Error.WriteLine();
    Console.Error.WriteLine($"DBを省略した場合は標準DBを使用する: {TrackMatchDataPaths.DefaultDatabasePath}");
    Console.Error.WriteLine("trash-reviewedは既定でdry-runし、ConfirmedDuplicateのReject側を確認する。実移動には--executeが必要である。");
    Console.Error.WriteLine("fpcalcは--fpcalc、TRACKMATCH_FPCALC環境変数、またはPATHで指定できる。");
}
