using System.IO;
using NexusApp.Services;
using Xunit;

namespace NexusApp.Tests;

// The demo-profile flag must swap the ENTIRE data root (settings, dbs, crash marker, logs,
// hulls) to a separate disposable folder, and its absence must resolve to the live profile.
// Everything about demo mode hangs off this one pure decision, so it is locked down headless.
public class AppPathsTests
{
    [Fact]
    public void NormalLaunch_UsesLiveRoot()
        => Assert.Equal(Path.Combine(@"C:\ad", "NexusApp"),
            AppPaths.ResolveRoot(new[] { "app.exe" }, @"C:\ad"));

    [Fact]
    public void DemoArg_UsesDemoRoot()
        => Assert.Equal(Path.Combine(@"C:\ad", "NexusApp_demo"),
            AppPaths.ResolveRoot(new[] { "app.exe", AppPaths.DemoArg }, @"C:\ad"));

    [Fact]
    public void DemoArg_MustMatchExactly()
        => Assert.Equal(Path.Combine(@"C:\ad", "NexusApp"),
            AppPaths.ResolveRoot(new[] { "app.exe", "--demo-profile-x" }, @"C:\ad"));

    [Fact]
    public void DemoRoot_IsTheDemoFolder()
        => Assert.EndsWith("NexusApp_demo", AppPaths.DemoRoot);

    [Fact]
    public void RootAndFlag_AgreeForThisProcess()
        => Assert.Equal(AppPaths.IsDemoProfile, AppPaths.Root.EndsWith("NexusApp_demo"));
}
