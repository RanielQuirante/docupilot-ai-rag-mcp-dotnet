namespace DocuPilot.UnitTests;

/// <summary>
/// Smoke tests proving that the xUnit runner, central package management, and project
/// references are all wired correctly. These tests carry no business meaning and exist
/// solely so a developer running <c>dotnet test</c> sees at least one assertion execute.
/// Real test suites land in Phase 3+.
/// </summary>
public sealed class SanityCheckTests
{
    [Fact]
    public void Sanity_RunnerIsWired()
    {
        // If this fact runs at all, the xUnit runner is correctly wired through
        // Microsoft.NET.Test.Sdk, the Directory.Packages.props central versions
        // resolved, and the project compiled cleanly under net10.0.
        Assert.True(true);
    }
}
