using System.Reflection;
using Xunit;

namespace TrackMatch.Core.Tests;

public sealed class ProjectReferenceTests
{
    [Fact]
    public void CoreAssemblyCanBeLoaded()
    {
        var assembly = Assembly.Load("TrackMatch.Core");

        Assert.Equal("TrackMatch.Core", assembly.GetName().Name);
    }
}
