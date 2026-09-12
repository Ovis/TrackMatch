using TrackMatch.Core.Candidates;

namespace TrackMatch.Application;

/// <summary>
/// Candidate一覧からTrack単体品質解析の対象を抽出する。
/// </summary>
public static class TrackQualityAnalysisRequestFactory
{
    /// <summary>
    /// Candidateに参加しているTrackだけをTrackId単位で重複排除して解析対象へ変換する。
    /// </summary>
    public static IReadOnlyList<TrackQualityAnalysisRequest> FromCandidates(
        IReadOnlyCollection<CandidateReviewReportRow> candidates)
    {
        ArgumentNullException.ThrowIfNull(candidates);

        var requests = new Dictionary<long, TrackQualityAnalysisRequest>();
        foreach (var candidate in candidates)
        {
            Add(requests, candidate.TrackIdA, candidate.PathA);
            Add(requests, candidate.TrackIdB, candidate.PathB);
        }

        return requests.Values.ToArray();
    }

    private static void Add(
        IDictionary<long, TrackQualityAnalysisRequest> requests,
        long trackId,
        string path)
    {
        var fullPath = Path.GetFullPath(path);
        if (requests.TryGetValue(trackId, out var existing)
            && !string.Equals(existing.Path, fullPath, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException($"Track {trackId} に複数のPathが含まれています。");
        }

        requests[trackId] = new TrackQualityAnalysisRequest(trackId, fullPath);
    }
}
