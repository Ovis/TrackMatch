using TrackMatch.Core.Persistence;
using TrackMatch.Core.Quality;

namespace TrackMatch.Application;

/// <summary>
/// Track単体品質解析キャッシュを前提に、Candidate一致区間の相対品質比較を実行・永続化する。
/// </summary>
public sealed class CandidateQualityAnalysisService
{
    private readonly ICandidateQualityAnalyzer _analyzer;
    private readonly ITrackQualityAnalysisRepository _trackRepository;
    private readonly ICandidateQualityComparisonRepository _candidateRepository;

    /// <summary>
    /// Candidate相対品質解析Serviceを生成する。
    /// </summary>
    public CandidateQualityAnalysisService(
        ICandidateQualityAnalyzer analyzer,
        ITrackQualityAnalysisRepository trackRepository,
        ICandidateQualityComparisonRepository candidateRepository)
    {
        _analyzer = analyzer ?? throw new ArgumentNullException(nameof(analyzer));
        _trackRepository = trackRepository ?? throw new ArgumentNullException(nameof(trackRepository));
        _candidateRepository = candidateRepository ?? throw new ArgumentNullException(nameof(candidateRepository));
    }

    /// <summary>
    /// Track単体解析が揃っているCandidateだけを比較し、現行Versionの成功済み結果は再利用する。
    /// </summary>
    public async Task<CandidateQualityComparison?> AnalyzeAsync(
        CandidateQualityAnalysisRequest request,
        bool force = false,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(request.Candidate);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.PathA);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.PathB);

        var candidate = request.Candidate;
        if (!force)
        {
            var cached = await _candidateRepository.GetAsync(
                candidate.TrackIdA,
                candidate.TrackIdB,
                cancellationToken);
            if (cached is not null
                && cached.ComparisonVersion == QualityAnalysisVersions.CandidateQualityComparison
                && cached.Status == QualityAnalysisStatus.Analyzed)
            {
                return cached;
            }
        }

        var analysisA = await _trackRepository.GetAsync(candidate.TrackIdA, cancellationToken);
        var analysisB = await _trackRepository.GetAsync(candidate.TrackIdB, cancellationToken);
        if (!IsUsableTrackAnalysis(analysisA) || !IsUsableTrackAnalysis(analysisB))
        {
            // Candidate比較はTrack単体解析の派生データなので、片側が未完了なら失敗扱いにはしない。
            // Track解析完了後に再度呼び出すことで自然に処理を続行できる。
            return null;
        }

        await _candidateRepository.UpsertAsync(
            CreateState(candidate.TrackIdA, candidate.TrackIdB, QualityAnalysisStatus.Analyzing),
            cancellationToken);

        CandidateQualityComparison result;
        try
        {
            result = await _analyzer.AnalyzeAsync(
                candidate,
                Path.GetFullPath(request.PathA),
                Path.GetFullPath(request.PathB),
                analysisA!,
                analysisB!,
                cancellationToken);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            // Analyzer実装側で通常のDecode失敗はFailedへ変換するが、想定外例外でも
            // Candidate全体の品質解析処理を止めないよう永続化可能なFailedへ変換する。
            result = CreateState(
                candidate.TrackIdA,
                candidate.TrackIdB,
                QualityAnalysisStatus.Failed,
                ex.Message);
        }

        await _candidateRepository.UpsertAsync(result, cancellationToken);
        return result;
    }

    private static bool IsUsableTrackAnalysis(TrackQualityAnalysis? analysis)
        => analysis is not null
            && analysis.AnalysisVersion == QualityAnalysisVersions.TrackQualityAnalysis
            && analysis.Status == QualityAnalysisStatus.Analyzed;

    private static CandidateQualityComparison CreateState(
        long trackIdA,
        long trackIdB,
        QualityAnalysisStatus status,
        string? failureReason = null)
        => new(
            trackIdA,
            trackIdB,
            QualityAnalysisVersions.CandidateQualityComparison,
            status,
            null,
            null,
            null,
            null,
            null,
            null,
            null,
            null,
            failureReason);
}
