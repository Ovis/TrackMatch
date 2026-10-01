using System.Diagnostics;
using TrackMatch.Application;
using Xunit;

namespace TrackMatch.App.Tests;

public sealed class GlobalMutationGateTests
{
    [Fact]
    public async Task TryEnterAsync_WhenHeld_ReturnsImmediatelyWithoutLease()
    {
        var gate = new GlobalMutationGate();
        using var held = await gate.EnterAsync(TestContext.Current.CancellationToken);
        var stopwatch = Stopwatch.StartNew();

        var rejected = await gate.TryEnterAsync(TestContext.Current.CancellationToken);

        stopwatch.Stop();
        Assert.Null(rejected);
        Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(1));
    }

    [Fact]
    public async Task EnterAsync_WhenHeld_WaitsUntilLeaseIsReleased()
    {
        var gate = new GlobalMutationGate();
        var held = await gate.EnterAsync(TestContext.Current.CancellationToken);

        var waiting = gate.EnterAsync(TestContext.Current.CancellationToken);
        Assert.False(waiting.IsCompleted);

        held.Dispose();
        using var acquired = await waiting;
    }
}
