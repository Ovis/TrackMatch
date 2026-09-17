using TrackMatch.Core.Candidates;
using Xunit;

namespace TrackMatch.Core.Tests;

public sealed class HumanVerdictPreferredTrackResolverTests
{
    [Fact]
    public void Resolve_ReturnsPreferredTrackWhenAllVerdictsAgree()
    {
        CandidateReview[] verdicts = [CandidateReviewFactory.ConfirmedDuplicate(1, 2, 1), CandidateReviewFactory.ConfirmedDuplicate(1, 3, 1)];
        Assert.Equal(1, HumanVerdictPreferredTrackResolver.Resolve(verdicts, [1, 2, 3]));
    }

    [Fact]
    public void Resolve_ReturnsNullWhenPreferredTracksConflict()
    {
        CandidateReview[] verdicts = [CandidateReviewFactory.ConfirmedDuplicate(1, 2, 1), CandidateReviewFactory.ConfirmedDuplicate(2, 3, 3)];
        Assert.Null(HumanVerdictPreferredTrackResolver.Resolve(verdicts, [1, 2, 3]));
    }
}
