using TrackMatch.Core.Candidates;
using Xunit;

namespace TrackMatch.Core.Tests;

public sealed class HumanVerdictConflictResolutionPolicyTests
{
    [Fact]
    public void Conflict_BlocksDestructiveActionButAllowsVerdictEditing()
    {
        Assert.False(HumanVerdictConflictResolutionPolicy.CanPerformDestructiveAction(HumanVerdictConflictKind.DuplicateVsNotDuplicate));
        Assert.True(HumanVerdictConflictResolutionPolicy.CanEditHumanVerdict(HumanVerdictConflictKind.DuplicateVsNotDuplicate));
    }
}
