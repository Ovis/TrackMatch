using TrackMatch.Core.Candidates;
using Xunit;

namespace TrackMatch.Core.Tests;

public sealed class HumanVerdictFileOperationPolicyTests
{
    [Fact]
    public void Rename_KeepsVerdict()
        => Assert.True(HumanVerdictFileOperationPolicy.KeepsVerdictForPathOnlyChange());

    [Fact]
    public void ContentVerificationFailure_InvalidatesVerdict()
        => Assert.True(HumanVerdictFileOperationPolicy.InvalidatesVerdictForContentFailure());
}
