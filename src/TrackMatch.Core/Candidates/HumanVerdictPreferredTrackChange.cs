namespace TrackMatch.Core.Candidates;

/// <summary>ConfirmedDuplicateの優先Track変更をHuman Verdictそのものの変更として扱う。</summary>
public static class HumanVerdictPreferredTrackChange
{
    /// <summary>同じPairのConfirmedDuplicateについてPreferred Trackを差し替えた新Verdictを生成する。</summary>
    public static CandidateReview Change(CandidateReview current, long preferredTrackId)
    {
        ArgumentNullException.ThrowIfNull(current);
        if (current.Decision != CandidateReviewDecision.ConfirmedDuplicate) throw new InvalidOperationException("Preferred Trackを変更できるのはConfirmedDuplicateだけです。");
        return CandidateReviewFactory.ConfirmedDuplicate(current.Pair.TrackIdA, current.Pair.TrackIdB, preferredTrackId, current.Note);
    }
}
