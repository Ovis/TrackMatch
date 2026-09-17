namespace TrackMatch.Core.Candidates;

/// <summary>UIでConflict理由を説明するための派生Summaryを表す。</summary>
public sealed record HumanVerdictConflictSummary(HumanVerdictConflictKind Kind, IReadOnlyList<long> TrackIds)
{
    public bool IsConflict => Kind != HumanVerdictConflictKind.None;
}
