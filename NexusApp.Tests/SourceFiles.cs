using System.IO;
using Xunit;

namespace NexusApp.Tests;

// Locates app source files from the test bin directory, for source-pin tests. Some glue cannot
// be constructed under test (MainViewModel news ScannerService and dereferences App statics in
// its ctor), so those tests pin the load-bearing source text instead - the sanctioned stopgap
// until the VM gets an injectable seam. Shared-helper pattern mirrors TestFiles/SeedTestFixture.
internal static class SourceFiles
{
    public static string ReadAppSource(string relativePath)
    {
        // bin/Debug/net8.0-windows.../ -> walk up to the directory that contains NexusApp/Views.
        var dir = new DirectoryInfo(System.AppContext.BaseDirectory);
        while (dir != null && !File.Exists(Path.Combine(dir.FullName, "NexusApp", "Views", "OverlayWindow.xaml.cs")))
            dir = dir.Parent;
        Assert.NotNull(dir);
        return File.ReadAllText(Path.Combine(dir!.FullName, "NexusApp", relativePath));
    }
}
