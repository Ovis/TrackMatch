using TrackMatch.Core.Candidates;
using Xunit;

namespace TrackMatch.Core.Tests;

public sealed class HumanVerdictMachineEvidencePolicyTests
{
    [Fact]
    public void MachineEvidenceChange_DoesNotInvalidateHumanVerdict()
        => Assert.False(HumanVerdictMachineEvidencePolicy.InvalidatesVerdictFromMachineEvidenceChange());

    [Fact]
    public void ExistingHumanVerdict_RemainsAuthoritative()
    {
        var verdict = CandidateReviewFactory.NotDuplicate(1, 2);
        Assert.Same(verdict, HumanVerdictMachineEvidencePolicy.ResolveAuthoritativeVerdict(verdict));
    }
}
