using TrackMatch.Core.Candidates;
using Xunit;

namespace TrackMatch.Core.Tests;

/// <summary>Human Verdictを機械判定と独立した正本として扱う不変条件を検証する。</summary>
public sealed class HumanVerdictPrecedenceTests
{
    [Fact]
    public void ConfirmedDuplicate_RemainsValidIndependentOfMachineClassification()
    {
        var verdict = CandidateReviewFactory.ConfirmedDuplicate(10, 20, 20);
        verdict.Validate();
        Assert.Equal(CandidateReviewDecision.ConfirmedDuplicate, verdict.Decision);
        Assert.Equal(20, verdict.PreferredTrackId);
    }

    [Fact]
    public void NotDuplicate_RemainsValidWithoutPreferredTrack()
    {
        var verdict = CandidateReviewFactory.NotDuplicate(10, 20);
        verdict.Validate();
        Assert.Equal(CandidateReviewDecision.NotDuplicate, verdict.Decision);
        Assert.Null(verdict.PreferredTrackId);
    }
}
