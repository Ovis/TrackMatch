using TrackMatch.Core.Candidates;
using Xunit;

namespace TrackMatch.Core.Tests;

public sealed class HumanVerdictDeletionPolicyTests
{
    [Fact]
    public void Conflict_BlocksDeletionCandidate()
        => Assert.False(HumanVerdictDeletionPolicy.CanRejectTrack(2, 1, true));

    [Fact]
    public void NonPreferredTrack_CanBeDeletionCandidateWithoutConflict()
        => Assert.True(HumanVerdictDeletionPolicy.CanRejectTrack(2, 1, false));

    [Fact]
    public void PreferredTrack_IsNeverDeletionCandidate()
        => Assert.False(HumanVerdictDeletionPolicy.CanRejectTrack(1, 1, false));
}
