using System.Reflection;
using Xunit;

namespace TrackMatch.Infrastructure.Tests;

public sealed class ProjectReferenceTests
{
    [Fact]
    public void InfrastructureAssemblyCanBeLoaded()
    {
        var assembly = Assembly.Load("TrackMatch.Infrastructure");

        Assert.Equal("TrackMatch.Infrastructure", assembly.GetName().Name);
    }
}
