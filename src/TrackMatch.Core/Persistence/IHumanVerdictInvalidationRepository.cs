using TrackMatch.Core.Candidates;

namespace TrackMatch.Core.Persistence;

/// <summary>Track Content変更等に伴うHuman Verdictの履歴退避境界を定義する。</summary>
public interface IHumanVerdictInvalidationRepository
{
    /// <summary>指定Trackに関係するCurrent Verdictを履歴へ退避し、Currentから除外する。</summary>
    Task InvalidateByTrackAsync(long trackId, HumanVerdictInvalidationReason reason, CancellationToken cancellationToken = default);
}
