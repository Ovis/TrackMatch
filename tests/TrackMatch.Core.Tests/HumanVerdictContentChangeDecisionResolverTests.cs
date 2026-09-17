using TrackMatch.Core.Candidates;
using Xunit;

namespace TrackMatch.Core.Tests;

public sealed class HumanVerdictContentChangeDecisionResolverTests
{
    [Fact]
    public void FailedVerification_InvalidatesToHistory()
        => Assert.Equal(HumanVerdictContentChangeDecision.InvalidateToHistory, HumanVerdictContentChangeDecisionResolver.Resolve(HumanVerdictContentVerificationState.Failed));
}
