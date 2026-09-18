using TrackMatch.Core.Candidates;
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
/// Library走査、候補生成、詳細比較、自動分類を同じ設定とDBで順番に実行するApplication Workflow。
/// </summary>
public sealed class LibraryAnalysisWorkflow
{
    private static readonly SemaphoreSlim ScanGate = new(1, 1);
    private readonly string _databasePath;
    private readonly string _fpcalcPath;
    private readonly int _fingerprintAlgorithm;

    /// <summary>
    /// Library分析Workflowを生成する。
    /// </summary>
    /// <param name="databasePath">WPFアプリが使用するSQLiteデータベースのPath</param>
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
    /// 指定Libraryに登録された全Rootを順に増分走査する。
    /// </summary>
    /// <param name="libraryId">走査対象LibraryのID</param>
    /// <param name="cancellationToken">走査のキャンセル要求</param>
    /// <param name="progress">Root数とファイル処理件数を通知する進捗通知先</param>
    public async Task<LibraryRootScanBatchResult> ScanLibraryAsync(
        long libraryId,
        CancellationToken cancellationToken = default,
        IProgress<LibraryScanBatchProgress>? progress = null)
    {
        await EnterScanGateAsync(cancellationToken);
        try
        {
            var database = await OpenDatabaseAsync(cancellationToken);
            var scanMayHaveCommittedChanges = false;
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

                    // Root ScanはTrack単位で完了済み更新を保持するため、呼び出し後に例外となっても
                    // Global Groupの派生状態をCurrent Verdictへ追従させる必要がある。
                    scanMayHaveCommittedChanges = true;
                    results.Add(await service.ScanAsync(library.Id, root.Id, root.Path, cancellationToken, rootProgress));
                }

                // ScanはContent ChangeでCurrent Verdictを無効化したりTrackをMissingへ遷移させる。
                // Materialized Global Groupを古いCurrent Verdictのまま残さないため、正常終了したScan Batchの直後に再同期する。
                await SynchronizeDuplicateGroupsAsync(database, cancellationToken);
                return new LibraryRootScanBatchResult(library.Id, results);
            }
            catch
            {
                if (scanMayHaveCommittedChanges)
                {
                    // CancellationだけでなくI/O例外等でも、失敗前までにTrack更新やVerdict無効化がCommit済みになり得る。
                    // 元のScan例外を失わないよう、派生Global Groupの修復はbest-effortで実行する。
                    await TrySynchronizeDuplicateGroupsAsync(database);
                }

                throw;
            }
        }
        finally
        {
            ScanGate.Release();
        }
    }

    /// <summary>
    /// 指定LibraryのMembershipに属するTrackだけを対象に候補Pairを増分生成する。
    /// </summary>
    /// <param name="libraryId">候補生成対象LibraryのID</param>
    /// <param name="options">候補生成設定。nullの場合は既定値を使用する</param>
    /// <param name="cancellationToken">候補生成のキャンセル要求</param>
    /// <param name="progress">索引更新と候補探索の件数を通知する進捗通知先</param>
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
    /// 指定Library内の候補Pairだけを詳細比較する。
    /// </summary>
    /// <param name="libraryId">詳細比較対象LibraryのID</param>
    /// <param name="cancellationToken">詳細比較のキャンセル要求</param>
    /// <param name="progress">候補Pair単位の比較進捗通知先</param>
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
    /// 指定Library内の詳細比較済み候補へApplication共通の自動分類Profileを適用する。
    /// </summary>
    /// <remarks>
    /// Candidate ClassificationはGlobal Pair単位のCurrent Stateなので、Libraryごとに異なるProfileを適用しない。
    /// GUIから利用するProfileはApplication既定値へ固定し、同じGlobal PairがLibraryによって異なる分類へ上書きされることを防ぐ。
    /// </remarks>
    /// <param name="libraryId">分類対象LibraryのID</param>
    /// <param name="cancellationToken">分類処理のキャンセル要求</param>
    public async Task<IReadOnlyList<CandidateClassificationReportRow>> ClassifyCandidatesAsync(
        long libraryId,
        CancellationToken cancellationToken = default)
    {
        var database = await OpenDatabaseAsync(cancellationToken);
        await GetRequiredLibraryAsync(database, libraryId, cancellationToken);
        var service = new CandidateClassificationService(
            new SqliteCandidateComparisonRepository(database, libraryId),
            new SqliteCandidateClassificationRepository(database, libraryId));
        return await service.ClassifyAsync(AutomaticRelationshipClassificationProfile.Default, cancellationToken);
    }

    /// <summary>
    /// 指定Libraryの全Root走査からLibrary内候補の詳細比較・自動分類までを一連の処理として実行する。
    /// </summary>
    /// <param name="libraryId">解析対象LibraryのID</param>
    /// <param name="progress">処理段階と実処理件数をUI等へ通知するための進捗通知先</param>
    /// <param name="cancellationToken">Workflow全体のキャンセル要求</param>
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
        IncrementalScanResult scan;
        try
        {
            scan = await ScanLibraryAsync(libraryId, cancellationToken, scanProgress);

            // Content Changed確定でHuman Verdictが削除された時点から旧Groupを表示・利用し続けない。
            // Candidate再評価は長時間化し得るため、その完了を待たず残存Verdictだけで派生状態を更新する。
            var scanDatabase = await OpenDatabaseAsync(CancellationToken.None);
            await SynchronizeDuplicateGroupsAsync(scanDatabase, CancellationToken.None);
        }
        catch
        {
            // Scan途中でキャンセルやDB障害が起きても、それ以前に確定済みのContent Changeだけは派生Groupへ反映する。
            // 修復用DB自体を開けない場合は、修復例外で元のScan失敗理由を上書きしない。
            try
            {
                var scanDatabase = await OpenDatabaseAsync(CancellationToken.None);
                await TrySynchronizeDuplicateGroupsAsync(scanDatabase);
            }
            catch
            {
            }

            throw;
        }

        try
        {
            var reevaluationDatabase = await OpenDatabaseAsync(cancellationToken);
            await new SqliteTrackRepository(reevaluationDatabase).MarkReevaluationStartedAsync(libraryId, cancellationToken);

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
            await ClassifyCandidatesAsync(libraryId, cancellationToken);

            var database = await OpenDatabaseAsync(CancellationToken.None);
            await new SqliteTrackRepository(database).MarkReevaluationCompletedAsync(libraryId, CancellationToken.None);
            await SynchronizeDuplicateGroupsAsync(database, CancellationToken.None);
            return new LibraryScopedAnalysisWorkflowResult(scan, generation, analysis);
        }
        catch (OperationCanceledException)
        {
            // キャンセルは解析障害とは区別する。ReevaluationPendingを維持し、次回Workflowでそのまま再試行する。
            var database = await OpenDatabaseAsync(CancellationToken.None);
            await TrySynchronizeDuplicateGroupsAsync(database);
            throw;
        }
        catch (Exception exception)
        {
            // Content Changed確定後の再評価が途中で失敗した場合は、次回通常Workflowで再試行できるよう状態を残す。
            // 失敗TrackをKeepやTrash判断へ戻さないため、派生Groupも失敗状態を反映して再同期する。
            var database = await OpenDatabaseAsync(CancellationToken.None);
            await new SqliteTrackRepository(database).MarkReevaluationFailedAsync(
                libraryId,
                exception.Message,
                CancellationToken.None);
            await TrySynchronizeDuplicateGroupsAsync(database);
            throw;
        }
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
        long libraryId)
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
            new SqliteCandidateGenerationWorkRepository(database, libraryId));
    }

    private static CandidateAnalysisService CreateCandidateAnalysisService(
        SqliteDatabase database,
        long libraryId)
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

    private static async Task TrySynchronizeDuplicateGroupsAsync(SqliteDatabase database)
    {
        try
        {
            await SynchronizeDuplicateGroupsAsync(database, CancellationToken.None);
        }
        catch
        {
            // 元のScan失敗原因を呼び出し元へ返すことを優先する。
            // DB障害が継続している場合、修復側の例外で最初の原因を上書きしない。
        }
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
}

/// <summary>
/// GUIへ通知するLibrary分析Workflowの処理段階を表す。
/// </summary>
public enum LibraryAnalysisStage
{
    Scanning,
    GeneratingCandidates,
    AnalyzingCandidates,
    ClassifyingCandidates,
}

/// <summary>
/// Library分析の実処理件数を含む進捗を表す。
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
/// <param name="RootIndex">現在処理中のRoot番号。1始まり</param>
/// <param name="RootCount">対象Libraryに登録されているRoot総数</param>
/// <param name="CompletedFiles">現在Rootで処理済みのファイル数</param>
/// <param name="TotalFiles">現在Rootの対象ファイル総数。不明な場合はnull</param>
/// <param name="CurrentPath">直近に処理したファイルPath</param>
public sealed record LibraryScanBatchProgress(
    int RootIndex,
    int RootCount,
    int CompletedFiles,
    int? TotalFiles,
    string? CurrentPath);

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
