using TrackMatch.Core.Candidates;
using Xunit;

namespace TrackMatch.Core.Tests;

public sealed class HumanVerdictTrashPolicyTests
{
    [Fact]
    public void Conflict_BlocksDestructiveAction()
        => Assert.False(HumanVerdictTrashPolicy.CanExecute(1, HumanVerdictConflictKind.PreferredTrack));

    [Fact]
    public void ResolvedPreferredTrack_AllowsDestructiveAction()
        => Assert.True(HumanVerdictTrashPolicy.CanExecute(1, HumanVerdictConflictKind.None));
}
