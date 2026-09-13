using TrackMatch.Core.Candidates;
using TrackMatch.Core.Classification;
using TrackMatch.Core.Comparison;
using TrackMatch.Core.Libraries;
using TrackMatch.Core.Scanning;
using TrackMatch.Infrastructure.Audio;
using TrackMatch.Infrastructure.Chromaprint;
using TrackMatch.Infrastructure.Persistence;
using TrackMatch.Infrastructure.Scanning;

namespace TrackMatch.Application;

/// <summary>
/// ライブラリ走査、候補生成、詳細比較、自動分類を同じ設定とDBで順番に実行するアプリケーションWorkflow。
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
    /// 指定Rootを増分走査し、変更された対応Audio FileのメタデータとFingerprintを更新する。
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
        CancellationToken cancellationToken = default,
        IProgress<LibraryScanBatchProgress>? progress = null)
    {
        var database = await OpenDatabaseAsync(cancellationToken);
        var library = await GetRequiredLibraryAsync(database, libraryId, cancellationToken);
        var service = CreateScanService(database);
        var results = new List<IncrementalScanResult>(library.Roots.Count);

        // Root間でTrack Identityを共有しない設計なので、各Rootを独立したScan Sessionとして順番に処理する。
        for (var index = 0; index < library.Roots.Count; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var root = library.Roots[index];
            var rootProgress = new Progress<IncrementalScanProgress>(value =>
                progress?.Report(new LibraryScanBatchProgress(
                    index + 1,
                    library.Roots.Count,
                    value.CompletedFiles,
                    value.TotalFiles,
                    value.CurrentPath)));
            results.Add(await service.ScanAsync(root.Path, cancellationToken, rootProgress));
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
        CancellationToken cancellationToken = default,
        IProgress<CandidateGenerationProgress>? progress = null)
    {
        options ??= new CandidateGenerationOptions();
        options.Validate();

        var database = await OpenDatabaseAsync(cancellationToken);
        await GetRequiredLibraryAsync(database, libraryId, cancellationToken);
        return await CreateCandidateGenerationService(database, libraryId)
            .GenerateAsync(_fingerprintAlgorithm, options, cancellationToken, progress);
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
        CancellationToken cancellationToken = default,
        IProgress<CandidateAnalysisProgress>? progress = null)
    {
        var database = await OpenDatabaseAsync(cancellationToken);
        await GetRequiredLibraryAsync(database, libraryId, cancellationToken);
        return await CreateCandidateAnalysisService(database, libraryId)
            .AnalyzeAsync(_fingerprintAlgorithm, cancellationToken, progress);
    }

    /// <summary>
    /// 指定Library内の詳細比較済み候補へ関係分類を適用する。
    /// </summary>
    /// <param name="libraryId">分類対象LibraryのID</param>
    /// <param name="profile">分類に使用するしきい値プロファイル</param>
    /// <param name="cancellationToken">キャンセル通知</param>
    public async Task<IReadOnlyList<CandidateClassificationReportRow>> ClassifyCandidatesAsync(
        long libraryId,
        RelationshipThresholdProfile profile,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(profile);

        var database = await OpenDatabaseAsync(cancellationToken);
        await GetRequiredLibraryAsync(database, libraryId, cancellationToken);
        var service = new CandidateClassificationService(
            new SqliteCandidateComparisonRepository(database, libraryId),
            new SqliteCandidateClassificationRepository(database, libraryId));
        return await service.ClassifyAsync(profile, cancellationToken);
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
    /// 指定Libraryの全Root走査からLibrary内候補の詳細比較・自動分類までを一連の処理として実行する。
    /// </summary>
    /// <param name="libraryId">解析対象LibraryのID</param>
    /// <param name="progress">処理段階と実処理件数をUI等へ通知するための進捗通知先</param>
    public async Task<LibraryScopedAnalysisWorkflowResult> RunAsync(
        long libraryId,
        IProgress<LibraryAnalysisProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        progress?.Report(new LibraryAnalysisProgress(LibraryAnalysisStage.Scanning, 0, null, null));
        var scanProgress = new Progress<LibraryScanBatchProgress>(value =>
            progress?.Report(new LibraryAnalysisProgress(
                LibraryAnalysisStage.Scanning,
                value.CompletedFiles,
                value.TotalFiles,
                $"対象フォルダ {value.RootIndex}/{value.RootCount}")));
        var scan = await ScanLibraryAsync(libraryId, cancellationToken, scanProgress);

        progress?.Report(new LibraryAnalysisProgress(LibraryAnalysisStage.GeneratingCandidates, 0, null, null));
        var generationProgress = new Progress<CandidateGenerationProgress>(value =>
            progress?.Report(new LibraryAnalysisProgress(
                LibraryAnalysisStage.GeneratingCandidates,
                value.CompletedCount,
                value.TotalCount,
                value.Phase == CandidateGenerationProgressPhase.UpdatingIndex ? "索引更新" : "候補ペア探索")));
        var generation = await GenerateCandidatesAsync(
            libraryId,
            cancellationToken: cancellationToken,
            progress: generationProgress);

        progress?.Report(new LibraryAnalysisProgress(LibraryAnalysisStage.AnalyzingCandidates, 0, null, null));
        var analysisProgress = new Progress<CandidateAnalysisProgress>(value =>
            progress?.Report(new LibraryAnalysisProgress(
                LibraryAnalysisStage.AnalyzingCandidates,
                value.CompletedPairs,
                value.TotalPairs,
                null)));
        var analysis = await AnalyzeCandidatesAsync(libraryId, cancellationToken, analysisProgress);

        progress?.Report(new LibraryAnalysisProgress(LibraryAnalysisStage.ClassifyingCandidates, 0, null, null));
        await ClassifyCandidatesAsync(
            libraryId,
            AutomaticRelationshipClassificationProfile.Default,
            cancellationToken);

        return new LibraryScopedAnalysisWorkflowResult(scan, generation, analysis);
    }

    private IncrementalLibraryScanService CreateScanService(SqliteDatabase database)
        => new(
            new AudioLibraryScanner(new AudioMetadataReaderDispatcher()),
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
        return library ?? throw new InvalidOperationException($"ライブラリが見つかりません: {libraryId}");
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
    ClassifyingCandidates,
}

/// <summary>
/// ライブラリ分析の実処理件数を含む進捗を表す。
/// </summary>
/// <param name="Stage">現在の処理段階</param>
/// <param name="CompletedCount">現在の段階で処理を完了した件数</param>
/// <param name="TotalCount">現在の段階の総件数。不明な場合はnull</param>
/// <param name="Detail">処理段階内の補足表示</param>
public sealed record LibraryAnalysisProgress(
    LibraryAnalysisStage Stage,
    int CompletedCount,
    int? TotalCount,
    string? Detail);

/// <summary>
/// 複数対象フォルダを走査するときの進捗を表す。
/// </summary>
public sealed record LibraryScanBatchProgress(
    int RootIndex,
    int RootCount,
    int CompletedFiles,
    int? TotalFiles,
    string? CurrentPath);

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
/// Library単位の走査、候補生成、詳細比較、自動分類を一括実行した結果を保持する。
/// </summary>
public sealed record LibraryScopedAnalysisWorkflowResult(
    LibraryRootScanBatchResult Scan,
    CandidateGenerationResult Generation,
    CandidateAnalysisResult Analysis);
