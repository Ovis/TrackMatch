using TrackMatch.Core.Candidates;

namespace TrackMatch.Core.Persistence;

/// <summary>
/// 候補分類結果の永続化と人手確認用読み取りを定義する。
/// </summary>
public interface ICandidateClassificationRepository
{
    Task ReplaceAllAsync(
        IReadOnlyCollection<CandidateClassification> classifications,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<CandidateClassificationReportRow>> GetReportAsync(
        CancellationToken cancellationToken = default);
}
