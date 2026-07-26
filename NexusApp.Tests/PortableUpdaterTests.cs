using NexusApp.Services;
using Xunit;

namespace NexusApp.Tests;

public class PortableUpdaterTests : IDisposable
{
    private readonly List<string> _tempDirs = new();

    public void Dispose()
    {
        foreach (var d in _tempDirs)
        {
            try { if (Directory.Exists(d)) Directory.Delete(d, recursive: true); }
            catch { /* best effort */ }
        }
    }

    private string TempDir()
    {
        var dir = Path.Combine(Path.GetTempPath(), "nexus-swap-test-" + Path.GetRandomFileName());
        Directory.CreateDirectory(dir);
        _tempDirs.Add(dir);
        return dir;
    }

    // A PortableEnv whose special folders all live inside one throwaway tree, so the
    // path-shape rules can be exercised without touching the real machine's folders.
    private (PortableEnv env, string root) FakeEnv()
    {
        var root = TempDir();
        var env = new PortableEnv(
            TempPath: Path.Combine(root, "Temp"),
            ProgramFiles: Path.Combine(root, "Program Files"),
            ProgramFilesX86: Path.Combine(root, "Program Files (x86)"),
            WindowsDir: Path.Combine(root, "Windows"),
            LocalAppData: Path.Combine(root, "LocalAppData"),
            AppDataRoot: Path.Combine(root, "AppData", "NexusApp"));
        return (env, root);
    }

    // ---- PreflightPathIssue (pure path-shape rules) ----

    [Fact]
    public void PreflightPathIssue_HappyPath_ReturnsNull()
    {
        var (env, root) = FakeEnv();
        var install = Path.Combine(root, "PortableApps", "NexusApp");
        Assert.Null(PortableUpdater.PreflightPathIssue(Path.Combine(install, "NexusApp.exe"), install, env));
    }

    [Fact]
    public void PreflightPathIssue_NullProcessPath_Refuses()
    {
        var (env, root) = FakeEnv();
        Assert.NotNull(PortableUpdater.PreflightPathIssue(null, Path.Combine(root, "x"), env));
    }

    [Fact]
    public void PreflightPathIssue_RenamedExe_Refuses()
    {
        var (env, root) = FakeEnv();
        var install = Path.Combine(root, "x");
        Assert.NotNull(PortableUpdater.PreflightPathIssue(Path.Combine(install, "MyNexus.exe"), install, env));
    }

    [Fact]
    public void PreflightPathIssue_ProcessDirDisagreesWithBaseDir_Refuses()
    {
        var (env, root) = FakeEnv();
        Assert.NotNull(PortableUpdater.PreflightPathIssue(
            Path.Combine(root, "a", "NexusApp.exe"), Path.Combine(root, "b"), env));
    }

    [Theory]
    [InlineData("Temp")]                    // %TEMP% (7-Zip transient extraction)
    [InlineData("Program Files")]
    [InlineData("Program Files (x86)")]
    [InlineData("Windows")]
    public void PreflightPathIssue_ForbiddenRoots_Refuse(string sub)
    {
        var (env, root) = FakeEnv();
        var install = Path.Combine(root, sub, "NexusApp");
        Assert.NotNull(PortableUpdater.PreflightPathIssue(Path.Combine(install, "NexusApp.exe"), install, env));
    }

    [Fact]
    public void PreflightPathIssue_AppDataRoot_Refuses()
    {
        var (env, root) = FakeEnv();
        var install = Path.Combine(root, "AppData", "NexusApp", "updates", "6.9.0", "staged", "NexusApp");
        Assert.NotNull(PortableUpdater.PreflightPathIssue(Path.Combine(install, "NexusApp.exe"), install, env));
    }

    [Fact]
    public void PreflightPathIssue_InstallerLocation_Refuses()
    {
        var (env, root) = FakeEnv();
        var install = Path.Combine(root, "LocalAppData", "Nexus");
        Assert.NotNull(PortableUpdater.PreflightPathIssue(Path.Combine(install, "NexusApp.exe"), install, env));
    }

    [Fact]
    public void PreflightPathIssue_UncPath_Refuses()
    {
        var (env, _) = FakeEnv();
        Assert.NotNull(PortableUpdater.PreflightPathIssue(
            @"\\server\share\NexusApp\NexusApp.exe", @"\\server\share\NexusApp", env));
    }

    [Fact]
    public void PreflightPathIssue_OverlongPath_Refuses()
    {
        var (env, root) = FakeEnv();
        var install = Path.Combine(root, new string('a', 210));
        Assert.NotNull(PortableUpdater.PreflightPathIssue(Path.Combine(install, "NexusApp.exe"), install, env));
    }

    // ---- NormalizeEntry (zip entry hardening) ----

    [Theory]
    [InlineData("NexusApp/NexusApp.exe", "NexusApp.exe")]
    [InlineData("NexusApp/Web/cargo/index.html", @"Web\cargo\index.html")]
    [InlineData(@"NexusApp\e_sqlite3.dll", "e_sqlite3.dll")]
    [InlineData("NexusApp/", "")]                 // the top-level folder entry itself
    [InlineData("NexusApp/Web/", "Web")]          // directory entry
    public void NormalizeEntry_Accepts(string entry, string expectedRel)
    {
        Assert.Null(PortableUpdater.NormalizeEntry(entry, out var rel));
        Assert.Equal(expectedRel, rel);
    }

    [Theory]
    [InlineData("loose.txt")]                          // outside the NexusApp top folder
    [InlineData("Other/NexusApp.exe")]
    [InlineData("NexusApp/../evil.txt")]               // traversal
    [InlineData(@"NexusApp\..\evil.txt")]
    [InlineData("/NexusApp/NexusApp.exe")]             // rooted
    [InlineData(@"C:\NexusApp\NexusApp.exe")]          // drive letter (also caught by colon)
    [InlineData("NexusApp/a:b.txt")]                   // alternate data stream
    [InlineData("NexusApp/install.marker")]            // would flip Distribution to Installer
    [InlineData("NexusApp/Web/install.marker")]
    [InlineData("NexusApp/update_journal.json")]       // collides with swap bookkeeping
    [InlineData("NexusApp/update.lock")]
    [InlineData("NexusApp/NexusApp.exe.old")]
    [InlineData("NexusApp/e_sqlite3.dll.new")]
    [InlineData("NexusApp/update-staging/x.txt")]
    [InlineData("NexusApp/CON.txt")]                   // reserved device name
    [InlineData("NexusApp/lpt1")]
    [InlineData("NexusApp/trailingdot./x.txt")]
    [InlineData("NexusApp/trailing.dll ")]             // trailing space
    [InlineData("NexusApp/./x.txt")]
    [InlineData("")]
    public void NormalizeEntry_Rejects(string entry) =>
        Assert.NotNull(PortableUpdater.NormalizeEntry(entry, out _));
}
