using Dapper;
using TrackMatch.Core.Candidates;
using TrackMatch.Core.Classification;
using TrackMatch.Core.Comparison;
using TrackMatch.Core.Duplicates;
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
    private static readonly SemaphoreSlim ScanGate = new(1, 1);
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
    /// Pathで一意に特定できる登録済みRootを増分走査する。
    /// </summary>
    /// <remarks>
    /// 異なるLibraryで同一Rootを登録できるため、同一Pathが複数Libraryに存在する場合は曖昧として拒否する。
    /// GUIの通常処理ではLibrary ID指定APIを使用する。Scan Jobはプロセス全体で同時に1件へ制限する。
    /// </remarks>
    public async Task<IncrementalScanResult> ScanAsync(
        string rootPath,
        CancellationToken cancellationToken = default)
    {
        await EnterScanGateAsync(cancellationToken);
        try
        {
            var database = await OpenDatabaseAsync(cancellationToken);
            try
            {
                var normalizedRoot = LibraryValueNormalizer.NormalizeRootPath(rootPath);
                await using var connection = await database.OpenConnectionAsync(cancellationToken);
                var roots = (await connection.QueryAsync<RootIdentityRow>(new CommandDefinition(
                    "SELECT Id, LibraryId, Path FROM LibraryRoots WHERE PathKey = @PathKey;",
                    new { PathKey = normalizedRoot.Key },
                    cancellationToken: cancellationToken))).ToArray();
                if (roots.Length != 1)
                {
                    throw new InvalidOperationException(
                        roots.Length == 0
                            ? $"登録済み対象フォルダが見つかりません: {normalizedRoot.DisplayPath}"
                            : $"同じ対象フォルダが複数Libraryに登録されています。Libraryを指定して実行してください: {normalizedRoot.DisplayPath}");
                }

                var root = roots[0];
                var result = await CreateScanService(database).ScanAsync(root.LibraryId, root.Id, root.Path, cancellationToken);
                await SynchronizeDuplicateGroupsAsync(database, cancellationToken);
                return result;
            }
            catch (OperationCanceledException)
            {
                // Track単位で完了したContent Change無効化はキャンセル後も保持される。
                // Cancel済みTokenを再利用すると派生Groupだけ古い状態で残るため、整合回復だけはキャンセル不可で完了させる。
                await SynchronizeDuplicateGroupsAsync(database, CancellationToken.None);
                throw;
            }
        }
        finally
        {
            ScanGate.Release();
        }
    }

    /// <summary>
    /// 指定Libraryに登録された全Rootを順に増分走査する。
    /// </summary>
    public async Task<LibraryRootScanBatchResult> ScanLibraryAsync(
        long libraryId,
        CancellationToken cancellationToken = default,
        IProgress<LibraryScanBatchProgress>? progress = null)
    {
        await EnterScanGateAsync(cancellationToken);
        try
        {
            var database = await OpenDatabaseAsync(cancellationToken);
            try
            {
                var library = await GetRequiredLibraryAsync(database, libraryId, cancellationToken);
                var service = CreateScanService(database);
                var results = new List<IncrementalScanResult>(library.Roots.Count);

                // Global TrackはRoot間・Library間で共有するが、Membership確立とMissing確定は各Rootの正常Scan単位で行う。
                // 同一DBへの競合更新を避けるため、Root Scanはここで順番に実行する。
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
                    results.Add(await service.ScanAsync(library.Id, root.Id, root.Path, cancellationToken, rootProgress));
                }

                // ScanはContent ChangeでCurrent Verdictを無効化したりTrackをMissingへ遷移させる。
                // Materialized Global Groupを古いCurrent Verdictのまま残さないため、正常終了したScan Batchの直後に再同期する。
                await SynchronizeDuplicateGroupsAsync(database, cancellationToken);
                return new LibraryRootScanBatchResult(library.Id, results);
            }
            catch (OperationCanceledException)
            {
                // Batch途中のキャンセルでも完了済みRoot/Trackの変更は保持されるため、
                // Materialized Global Groupだけが古い状態に戻らないようCurrent Verdictから再同期する。
                await SynchronizeDuplicateGroupsAsync(database, CancellationToken.None);
                throw;
            }
        }
        finally
        {
            ScanGate.Release();
        }
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
    /// 指定LibraryのMembershipに属するTrackだけを対象に候補ペアを増分生成する。
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
    /// 候補ペアを詳細比較し、変更されていないGlobal ComparisonはDBから再利用する。
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
    /// Pathで一意に特定できるRootの走査から候補詳細比較までを一連の処理として実行する。
    /// </summary>
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
            new CandidatePairGenerator(sketcher),
            libraryId is { } id ? new SqliteCandidateGenerationWorkRepository(database, id) : null);
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

    private static async Task SynchronizeDuplicateGroupsAsync(
        SqliteDatabase database,
        CancellationToken cancellationToken)
    {
        var service = new DuplicateGroupService(
            new SqliteCandidateReviewRepository(database),
            new SqliteTrackLookupRepository(database),
            new SqliteDuplicateGroupRepository(database));
        await service.SynchronizeGlobalAsync(cancellationToken);
    }

    private async Task<SqliteDatabase> OpenDatabaseAsync(CancellationToken cancellationToken)
    {
        var database = new SqliteDatabase(_databasePath);
        await database.InitializeAsync(cancellationToken);
        return database;
    }

    private static async Task EnterScanGateAsync(CancellationToken cancellationToken)
    {
        if (!await ScanGate.WaitAsync(0, cancellationToken))
        {
            throw new InvalidOperationException("別のスキャンが既に実行中です。完了またはキャンセルしてから再実行してください。");
        }
    }

    private sealed record RootIdentityRow(long Id, long LibraryId, string Path);
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