using TrackMatch.Core.Candidates;
using Xunit;

namespace TrackMatch.Core.Tests;

public sealed class HumanVerdictGroupResolverTests
{
    [Fact]
    public void Resolve_DuplicateVsNotDuplicateConflictTakesPriority()
    {
        CandidateReview[] verdicts = [CandidateReviewFactory.ConfirmedDuplicate(1, 2, 1), CandidateReviewFactory.ConfirmedDuplicate(2, 3, 2), CandidateReviewFactory.NotDuplicate(1, 3)];
        var result = HumanVerdictGroupResolver.Resolve(verdicts, [1, 2, 3]);
        Assert.Equal(HumanVerdictConflictKind.DuplicateVsNotDuplicate, result.ConflictKind);
    }

    [Fact]
    public void Resolve_DetectsPreferredTrackConflict()
    {
        CandidateReview[] verdicts = [CandidateReviewFactory.ConfirmedDuplicate(1, 2, 1), CandidateReviewFactory.ConfirmedDuplicate(2, 3, 3)];
        var result = HumanVerdictGroupResolver.Resolve(verdicts, [1, 2, 3]);
        Assert.Equal(HumanVerdictConflictKind.PreferredTrack, result.ConflictKind);
    }
}
