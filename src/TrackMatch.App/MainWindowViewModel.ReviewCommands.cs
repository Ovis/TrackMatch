using TrackMatch.Core.Candidates;

namespace TrackMatch.App;

public sealed partial class MainWindowViewModel
{
    /// <summary>重複ではない判定をHuman Verdictとして保存する。</summary>
    public Task MarkNotDuplicateHumanVerdictAsync() => SaveHumanVerdictAsync(CandidateReviewDecision.NotDuplicate, null);

    /// <summary>Aを優先する重複判定をHuman Verdictとして保存する。</summary>
    public Task ConfirmDuplicatePreferAAsync() => SaveHumanVerdictAsync(CandidateReviewDecision.ConfirmedDuplicate, SelectedCandidate?.TrackIdA);

    /// <summary>Bを優先する重複判定をHuman Verdictとして保存する。</summary>
    public Task ConfirmDuplicatePreferBAsync() => SaveHumanVerdictAsync(CandidateReviewDecision.ConfirmedDuplicate, SelectedCandidate?.TrackIdB);
}
