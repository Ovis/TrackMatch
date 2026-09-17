using TrackMatch.Core.Candidates;
using Xunit;

namespace TrackMatch.Core.Tests;

public sealed class HumanVerdictUndoEntryTests
{
    [Fact]
    public void Validate_AcceptsPreviousVerdictForSamePair()
    {
        var verdict = CandidateReviewFactory.ConfirmedDuplicate(1, 2, 2);
        new HumanVerdictUndoEntry(verdict.Pair, verdict).Validate();
    }

    [Fact]
    public void Validate_RejectsPreviousVerdictForDifferentPair()
    {
        var entry = new HumanVerdictUndoEntry(CandidatePairKey.Create(1, 2), CandidateReviewFactory.NotDuplicate(2, 3));
        Assert.Throws<InvalidOperationException>(entry.Validate);
    }
}
