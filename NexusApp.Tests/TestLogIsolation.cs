using System.IO;
using System.Runtime.CompilerServices;

namespace NexusApp.Tests;

// Point NexusApp's Logger at a temp file for the whole test run so tests (several of which deliberately
// induce error / rollback paths) never write to, rotate, or delete the user's real
// %AppData%\NexusApp\logs\nexus.log. A module initializer runs before any other code in this assembly,
// so the NEXUS_LOG_PATH override is in place before Logger's static path initializer reads it.
internal static class TestLogIsolation
{
    [ModuleInitializer]
    internal static void RedirectLog()
    {
        var dir = Path.Combine(Path.GetTempPath(), "NexusAppTests", "logs");
        Directory.CreateDirectory(dir);
        System.Environment.SetEnvironmentVariable("NEXUS_LOG_PATH", Path.Combine(dir, "nexus_test.log"));
    }
}
