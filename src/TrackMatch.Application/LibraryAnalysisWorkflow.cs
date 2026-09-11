using TrackMatch.Core.Candidates;
using TrackMatch.Core.Comparison;
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
    /// 指定ライブラリを増分走査し、変更されたFLACのメタデータとFingerprintを更新する。
    /// </summary>
    public async Task<IncrementalScanResult> ScanAsync(
        string rootPath,
        CancellationToken cancellationToken = default)
    {
        var database = await OpenDatabaseAsync(cancellationToken);
        var service = new IncrementalLibraryScanService(
            new FlacLibraryScanner(new FlacMetadataReader()),
            new SqliteTrackRepository(database),
            new SqliteScanSessionRepository(database),
            new FpcalcFingerprintExtractor(_fpcalcPath),
            _fingerprintAlgorithm);
        return await service.ScanAsync(rootPath, cancellationToken);
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
        var reviewRepository = new SqliteCandidateReviewRepository(database);
        var sketcher = new FingerprintSegmentSketcher();
        var service = new CandidateGenerationService(
            new SqliteFingerprintCatalogRepository(database),
            new SqliteFingerprintSegmentSketchRepository(database),
            new SqliteCandidatePairRepository(database),
            reviewRepository,
            sketcher,
            new CandidatePairGenerator(sketcher));
        return await service.GenerateAsync(_fingerprintAlgorithm, options, cancellationToken);
    }

    /// <summary>
    /// 候補ペアを詳細比較し、変更されていない比較結果はDBから再利用する。
    /// </summary>
    public async Task<CandidateAnalysisResult> AnalyzeCandidatesAsync(
        CancellationToken cancellationToken = default)
    {
        var database = await OpenDatabaseAsync(cancellationToken);
        var service = new CandidateAnalysisService(
            new SqliteFingerprintCatalogRepository(database),
            new SqliteCandidatePairRepository(database),
            new SqliteCandidateComparisonRepository(database),
            new FingerprintComparer());
        return await service.AnalyzeAsync(_fingerprintAlgorithm, cancellationToken);
    }

    /// <summary>
    /// ライブラリ走査から候補詳細比較までを一連の処理として実行する。
    /// </summary>
    /// <param name="rootPath">解析対象ライブラリのルートフォルダ</param>
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
/// ライブラリ走査、候補生成、詳細比較を一括実行した結果を保持する。
/// </summary>
public sealed record LibraryAnalysisWorkflowResult(
    IncrementalScanResult Scan,
    CandidateGenerationResult Generation,
    CandidateAnalysisResult Analysis);
