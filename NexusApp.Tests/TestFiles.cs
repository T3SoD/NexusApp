using System.Collections.Generic;
using System.IO;

namespace NexusApp.Tests;

// Shared-read file helper for tests that inspect the isolated log files while other test
// classes (running in parallel) are writing them. File.ReadAllLines opens with FileShare.Read,
// which DENIES a concurrent writer and makes the writer's never-throw catch silently drop the
// line - a race that showed up as a flaky ScanHistory_RecordsUnmatchedToDedicatedLog. Reading
// with FileShare.ReadWrite never blocks a writer.
internal static class TestFiles
{
    public static string[] ReadSharedLines(string path)
    {
        if (!File.Exists(path)) return System.Array.Empty<string>();
        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        using var sr = new StreamReader(fs);
        var lines = new List<string>();
        string? line;
        while ((line = sr.ReadLine()) != null) lines.Add(line);
        return lines.ToArray();
    }
}
