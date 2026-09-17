namespace TrackMatch.Core.Candidates;

/// <summary>CandidateレビューUIが発行するHuman Verdict操作を表す。</summary>
public enum HumanVerdictReviewAction
{
    MarkNotDuplicate,
    ConfirmDuplicatePreferA,
    ConfirmDuplicatePreferB,
    Clear,
}
