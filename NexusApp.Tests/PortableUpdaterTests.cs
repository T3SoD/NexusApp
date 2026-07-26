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

    // ---- VerifyAndExtract (same-handle verify, hardened extraction) ----

    private static string HashHex(byte[] data) =>
        Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(data)).ToLowerInvariant();

    // Builds a zip at a temp path from (entryName, content) pairs and returns (path, hash).
    private (string path, string sha256) MakeZip(params (string name, string content)[] entries)
    {
        var path = Path.Combine(TempDir(), "payload.zip");
        using (var fs = File.Create(path))
        using (var zip = new System.IO.Compression.ZipArchive(fs, System.IO.Compression.ZipArchiveMode.Create))
            foreach (var (name, content) in entries)
            {
                var e = zip.CreateEntry(name);
                using var s = e.Open();
                var bytes = System.Text.Encoding.UTF8.GetBytes(content);
                s.Write(bytes, 0, bytes.Length);
            }
        return (path, HashHex(File.ReadAllBytes(path)));
    }

    private static (string name, string content)[] GoodEntries() => new[]
    {
        ("NexusApp/NexusApp.exe", "new exe bytes"),
        ("NexusApp/e_sqlite3.dll", "new sqlite bytes"),
        ("NexusApp/wpfgfx_cor3.dll", "new wpf bytes"),
        ("NexusApp/README.txt", "new readme"),
        ("NexusApp/Web/cargo/index.html", "new page"),
    };

    [Fact]
    public void VerifyAndExtract_HappyPath_ExtractsAndHashes()
    {
        var (zip, hash) = MakeZip(GoodEntries());
        var dest = Path.Combine(TempDir(), "staged", "NexusApp");
        var result = PortableUpdater.VerifyAndExtract(zip, hash, dest);
        Assert.Equal("new exe bytes", File.ReadAllText(Path.Combine(dest, "NexusApp.exe")));
        Assert.Equal("new page", File.ReadAllText(Path.Combine(dest, "Web", "cargo", "index.html")));
        Assert.Equal(5, result.FileHashes.Count);
        Assert.Equal(HashHex(System.Text.Encoding.UTF8.GetBytes("new page")), result.FileHashes[@"Web\cargo\index.html"]);
        Assert.Equal(dest, result.PayloadRoot);
        Assert.True(result.TotalBytes > 0);
    }

    [Fact]
    public void VerifyAndExtract_WrongZipHash_RefusesBeforeExtracting()
    {
        var (zip, _) = MakeZip(GoodEntries());
        var dest = Path.Combine(TempDir(), "staged", "NexusApp");
        Assert.Throws<InvalidOperationException>(() =>
            PortableUpdater.VerifyAndExtract(zip, new string('a', 64), dest));
        Assert.False(Directory.Exists(dest));   // nothing may land before the hash gate
    }

    [Theory]
    [InlineData("NexusApp/../evil.txt")]
    [InlineData("NexusApp/install.marker")]
    [InlineData("NexusApp/x.dll.old")]
    [InlineData("NexusApp/x.dll.new")]
    [InlineData("NexusApp/update_journal.json")]
    [InlineData("NexusApp/update.lock")]
    [InlineData("NexusApp/update-staging/x.txt")]
    [InlineData("loose.txt")]
    public void VerifyAndExtract_HostileEntry_Refuses(string hostile)
    {
        var entries = GoodEntries().Append((hostile, "evil")).ToArray();
        var (zip, hash) = MakeZip(entries);
        var dest = Path.Combine(TempDir(), "staged", "NexusApp");
        Assert.Throws<InvalidOperationException>(() => PortableUpdater.VerifyAndExtract(zip, hash, dest));
    }

    [Fact]
    public void VerifyAndExtract_MissingExe_Refuses()
    {
        var (zip, hash) = MakeZip(("NexusApp/README.txt", "no exe here"));
        Assert.Throws<InvalidOperationException>(() =>
            PortableUpdater.VerifyAndExtract(zip, hash, Path.Combine(TempDir(), "s")));
    }

    [Fact]
    public void VerifyAndExtract_TooManyEntries_Refuses()
    {
        var (zip, hash) = MakeZip(GoodEntries());
        Assert.Throws<InvalidOperationException>(() =>
            PortableUpdater.VerifyAndExtract(zip, hash, Path.Combine(TempDir(), "s"), maxEntries: 2));
    }

    [Fact]
    public void VerifyAndExtract_ExpansionPastCap_Refuses()
    {
        var (zip, hash) = MakeZip(GoodEntries());
        Assert.Throws<InvalidOperationException>(() =>
            PortableUpdater.VerifyAndExtract(zip, hash, Path.Combine(TempDir(), "s"), maxBytes: 10));
    }

    [Fact]
    public void VerifyAndExtract_ReplacesAStaleStagedTree()
    {
        var (zip, hash) = MakeZip(GoodEntries());
        var dest = Path.Combine(TempDir(), "staged", "NexusApp");
        Directory.CreateDirectory(dest);
        File.WriteAllText(Path.Combine(dest, "stale.txt"), "from a crashed run");
        PortableUpdater.VerifyAndExtract(zip, hash, dest);
        Assert.False(File.Exists(Path.Combine(dest, "stale.txt")));   // fresh tree every run
    }
}
