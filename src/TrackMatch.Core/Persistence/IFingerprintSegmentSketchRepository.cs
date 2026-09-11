using TrackMatch.Core.Candidates;

namespace TrackMatch.Core.Persistence;

/// <summary>
/// 候補生成用Segment Sketchキャッシュの永続化境界を定義する。
/// </summary>
public interface IFingerprintSegmentSketchRepository
{
    Task<IReadOnlyDictionary<long, DateTime>> GetTrackStatesAsync(
        int algorithm,
        CandidateGenerationOptions options,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<FingerprintSegmentSketch>> GetAllAsync(
        int algorithm,
        CandidateGenerationOptions options,
        CancellationToken cancellationToken = default);

    Task ReplaceTrackAsync(
        StoredFingerprint fingerprint,
        CandidateGenerationOptions options,
        IReadOnlyCollection<FingerprintSegmentSketch> sketches,
        CancellationToken cancellationToken = default);

    Task PruneAsync(
        int algorithm,
        CandidateGenerationOptions options,
        CancellationToken cancellationToken = default);
}
