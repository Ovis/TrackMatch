using TrackMatch.Core.Candidates;

namespace TrackMatch.App;

public sealed partial class MainWindowViewModel
{
    /// <summary>現在選択中PairのUI操作から正本Human Verdictを生成する。</summary>
    private CandidateReview CreateHumanVerdict(CandidateReviewDecision decision, long? preferredTrackId)
    {
        var selected = SelectedCandidate ?? throw new InvalidOperationException("レビュー対象が選択されていません。");
        return decision switch
        {
            CandidateReviewDecision.NotDuplicate => CandidateReviewFactory.NotDuplicate(selected.TrackIdA, selected.TrackIdB),
            CandidateReviewDecision.ConfirmedDuplicate when preferredTrackId is { } preferred
                => CandidateReviewFactory.ConfirmedDuplicate(selected.TrackIdA, selected.TrackIdB, preferred),
            CandidateReviewDecision.ConfirmedDuplicate
                => throw new InvalidOperationException("重複判定には優先Trackの選択が必要です。"),
            _ => throw new ArgumentOutOfRangeException(nameof(decision)),
        };
    }
}
