using TrackMatch.Core.Candidates;
using Xunit;

namespace TrackMatch.Core.Tests;

public sealed class HumanVerdictConflictDetectorTests
{
    [Fact]
    public void FindConflictingTrackIds_DetectsTransitiveContradictionWithoutRejectingVerdicts()
    {
        CandidateReview[] reviews =
        [
            CandidateReviewFactory.ConfirmedDuplicate(1, 2, 1),
            CandidateReviewFactory.ConfirmedDuplicate(2, 3, 2),
            CandidateReviewFactory.NotDuplicate(1, 3),
        ];

        var conflicts = HumanVerdictConflictDetector.FindConflictingTrackIds(reviews);

        Assert.Contains(1, conflicts);
        Assert.Contains(3, conflicts);
    }
}
