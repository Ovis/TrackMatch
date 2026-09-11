using TrackMatch.Core.Candidates;
using TrackMatch.Core.Comparison;
using TrackMatch.Core.Libraries;
using TrackMatch.Core.Scanning;
using TrackMatch.Infrastructure.Audio;
using TrackMatch.Infrastructure.Chromaprint;
using TrackMatch.Infrastructure.Persistence;
using TrackMatch.Infrastructure.Scanning;

namespace TrackMatch.Application;

/// <summary>
/// ライブラリ走査、候補生成、詳細比較を同じ設定とDBで順番に実行するアプリケーションWorkflow。
/// </summary>
public sealed class LibraryAnalysisWorkflow
{
    private readonly string _databasePath;
    private readonly string _fpcalcPath;
    private readonly int _fingerprintAlgorithm;

    /// <summary>
    /// ライブラリ分析Workflowを生成する。
    /// </summary>
    /// <param name="databasePath">ScannerとGUIで共有するSQLiteデータベースのパス</param>
    /// <param name="fpcalcPath">Chromaprint fpcalcの実行ファイル指定。既定値では同梱fpcalcを優先して解決する</param>
    /// <param name="fingerprintAlgorithm">ChromaprintのFingerprint Algorithm</param>
    public LibraryAnalysisWorkflow(
        string databasePath,
        string fpcalcPath = "fpcalc",
        int fingerprintAlgorithm = 2)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(databasePath);
        ArgumentException.ThrowIfNullOrWhiteSpace(fpcalcPath);
        if (fingerprintAlgorithm < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(fingerprintAlgorithm));
        }

        _databasePath = databasePath;
        _fpcalcPath = fpcalcPath;
        _fingerprintAlgorithm = fingerprintAlgorithm;
    }

    /// <summary>
    /// 指定Rootを増分走査し、変更されたFLACのメタデータとFingerprintを更新する。
    /// </summary>
    /// <remarks>
    /// CLI/GUIをLibrary選択方式へ移行するまでの互換API。新規処理では<see cref="ScanLibraryAsync"/>を使用する。
    /// </remarks>
    public async Task<IncrementalScanResult> ScanAsync(
        string rootPath,
        CancellationToken cancellationToken = default)
    {
        var database = await OpenDatabaseAsync(cancellationToken);
        var service = CreateScanService(database);
        return await service.ScanAsync(rootPath, cancellationToken);
    }

    /// <summary>
    /// 指定Libraryに登録された全Rootを順に増分走査する。
    /// </summary>
    public async Task<LibraryRootScanBatchResult> ScanLibraryAsync(
        long libraryId,
        CancellationToken cancellationToken = default)
    {
        var database = await OpenDatabaseAsync(cancellationToken);
        var library = await GetRequiredLibraryAsync(database, libraryId, cancellationToken);
        var service = CreateScanService(database);
        var results = new List<IncrementalScanResult>(library.Roots.Count);

        // Root間でTrack Identityを共有しない設計なので、各Rootを独立したScan Sessionとして順番に処理する。
        foreach (var root in library.Roots)
        {
            cancellationToken.ThrowIfCancellationRequested();
            results.Add(await service.ScanAsync(root.Path, cancellationToken));
        }

        return new LibraryRootScanBatchResult(library.Id, results);
    }

    /// <summary>
    /// 保存済みFingerprintから候補ペアを増分生成する。
    /// </summary>
    public async Task<CandidateGenerationResult> GenerateCandidatesAsync(
        CandidateGenerationOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        options ??= new CandidateGenerationOptions();
        options.Validate();

        var database = await OpenDatabaseAsync(cancellationToken);
        return await CreateCandidateGenerationService(database, libraryId: null)
            .GenerateAsync(_fingerprintAlgorithm, options, cancellationToken);
    }

    /// <summary>
    /// 指定Library内のTrackだけを対象に候補ペアを増分生成する。
    /// </summary>
    public async Task<CandidateGenerationResult> GenerateCandidatesAsync(
        long libraryId,
        CandidateGenerationOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        options ??= new CandidateGenerationOptions();
        options.Validate();

        var database = await OpenDatabaseAsync(cancellationToken);
        await GetRequiredLibraryAsync(database, libraryId, cancellationToken);
        return await CreateCandidateGenerationService(database, libraryId)
            .GenerateAsync(_fingerprintAlgorithm, options, cancellationToken);
    }

    /// <summary>
    /// 候補ペアを詳細比較し、変更されていない比較結果はDBから再利用する。
    /// </summary>
    public async Task<CandidateAnalysisResult> AnalyzeCandidatesAsync(
        CancellationToken cancellationToken = default)
    {
        var database = await OpenDatabaseAsync(cancellationToken);
        return await CreateCandidateAnalysisService(database, libraryId: null)
            .AnalyzeAsync(_fingerprintAlgorithm, cancellationToken);
    }

    /// <summary>
    /// 指定Library内の候補ペアだけを詳細比較する。
    /// </summary>
    public async Task<CandidateAnalysisResult> AnalyzeCandidatesAsync(
        long libraryId,
        CancellationToken cancellationToken = default)
    {
        var database = await OpenDatabaseAsync(cancellationToken);
        await GetRequiredLibraryAsync(database, libraryId, cancellationToken);
        return await CreateCandidateAnalysisService(database, libraryId)
            .AnalyzeAsync(_fingerprintAlgorithm, cancellationToken);
    }

    /// <summary>
    /// 単一Rootの走査から候補詳細比較までを一連の処理として実行する。
    /// </summary>
    /// <remarks>
    /// CLI/GUIをLibrary選択方式へ移行するまでの互換API。新規処理ではLibrary ID指定のRunAsyncを使用する。
    /// </remarks>
    /// <param name="rootPath">解析対象のRootフォルダ</param>
    /// <param name="progress">現在の処理段階をUI等へ通知するための進捗通知先</param>
    public async Task<LibraryAnalysisWorkflowResult> RunAsync(
        string rootPath,
        IProgress<LibraryAnalysisStage>? progress = null,
        CancellationToken cancellationToken = default)
    {
        progress?.Report(LibraryAnalysisStage.Scanning);
        var scan = await ScanAsync(rootPath, cancellationToken);

        progress?.Report(LibraryAnalysisStage.GeneratingCandidates);
        var generation = await GenerateCandidatesAsync(cancellationToken: cancellationToken);

        progress?.Report(LibraryAnalysisStage.AnalyzingCandidates);
        var analysis = await AnalyzeCandidatesAsync(cancellationToken);

        return new LibraryAnalysisWorkflowResult(scan, generation, analysis);
    }

    /// <summary>
    /// 指定Libraryの全Root走査からLibrary内候補の詳細比較までを一連の処理として実行する。
    /// </summary>
    /// <param name="libraryId">解析対象LibraryのID</param>
    /// <param name="progress">現在の処理段階をUI等へ通知するための進捗通知先</param>
    public async Task<LibraryScopedAnalysisWorkflowResult> RunAsync(
        long libraryId,
        IProgress<LibraryAnalysisStage>? progress = null,
        CancellationToken cancellationToken = default)
    {
        progress?.Report(LibraryAnalysisStage.Scanning);
        var scan = await ScanLibraryAsync(libraryId, cancellationToken);

        progress?.Report(LibraryAnalysisStage.GeneratingCandidates);
        var generation = await GenerateCandidatesAsync(libraryId, cancellationToken: cancellationToken);

        progress?.Report(LibraryAnalysisStage.AnalyzingCandidates);
        var analysis = await AnalyzeCandidatesAsync(libraryId, cancellationToken);

        return new LibraryScopedAnalysisWorkflowResult(scan, generation, analysis);
    }

    private IncrementalLibraryScanService CreateScanService(SqliteDatabase database)
        => new(
            new FlacLibraryScanner(new FlacMetadataReader()),
            new SqliteTrackRepository(database),
            new SqliteScanSessionRepository(database),
            new FpcalcFingerprintExtractor(_fpcalcPath),
            _fingerprintAlgorithm);

    private static CandidateGenerationService CreateCandidateGenerationService(
        SqliteDatabase database,
        long? libraryId)
    {
        var reviewRepository = new SqliteCandidateReviewRepository(database);
        var sketcher = new FingerprintSegmentSketcher();
        return new CandidateGenerationService(
            new SqliteFingerprintCatalogRepository(database, libraryId),
            new SqliteFingerprintSegmentSketchRepository(database, libraryId),
            new SqliteCandidatePairRepository(database, libraryId),
            reviewRepository,
            sketcher,
            new CandidatePairGenerator(sketcher));
    }

    private static CandidateAnalysisService CreateCandidateAnalysisService(
        SqliteDatabase database,
        long? libraryId)
        => new(
            new SqliteFingerprintCatalogRepository(database, libraryId),
            new SqliteCandidatePairRepository(database, libraryId),
            new SqliteCandidateComparisonRepository(database, libraryId),
            new FingerprintComparer());

    private static async Task<Library> GetRequiredLibraryAsync(
        SqliteDatabase database,
        long libraryId,
        CancellationToken cancellationToken)
    {
        if (libraryId <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(libraryId));
        }

        var library = await new SqliteLibraryRepository(database).GetAsync(libraryId, cancellationToken);
        return library ?? throw new InvalidOperationException($"Libraryが見つかりません: {libraryId}");
    }

    private async Task<SqliteDatabase> OpenDatabaseAsync(CancellationToken cancellationToken)
    {
        var database = new SqliteDatabase(_databasePath);
        await database.InitializeAsync(cancellationToken);
        return database;
    }
}

/// <summary>
/// GUI等へ通知するライブラリ分析Workflowの処理段階を表す。
/// </summary>
public enum LibraryAnalysisStage
{
    Scanning,
    GeneratingCandidates,
    AnalyzingCandidates,
}

/// <summary>
/// 単一Rootの走査、候補生成、詳細比較を一括実行した結果を保持する。
/// </summary>
public sealed record LibraryAnalysisWorkflowResult(
    IncrementalScanResult Scan,
    CandidateGenerationResult Generation,
    CandidateAnalysisResult Analysis);

/// <summary>
/// Libraryに登録された全Rootの走査結果を保持する。
/// </summary>
public sealed record LibraryRootScanBatchResult(
    long LibraryId,
    IReadOnlyList<IncrementalScanResult> Roots);

/// <summary>
/// Library単位の走査、候補生成、詳細比較を一括実行した結果を保持する。
/// </summary>
public sealed record LibraryScopedAnalysisWorkflowResult(
    LibraryRootScanBatchResult Scan,
    CandidateGenerationResult Generation,
    CandidateAnalysisResult Analysis);
