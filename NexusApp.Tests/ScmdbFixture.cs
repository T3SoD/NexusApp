using System.IO;
using Xunit;

namespace NexusApp.Tests;

// Shared embedded-fixture loader for the SCMDB import tests (issue #3): the sanitized 12-entry
// v3 sample used by ScmdbExportParserTests (ScmdbImportPlanTests exercises its bucketing logic
// with plain inline dictionaries/arrays instead - it needs no export shape at all). Embedded IN
// the test assembly and read the same way SeedTestFixture reads Data/seed_data.json from the main
// assembly - mirrors that embed pattern rather than TestFiles.cs's shared-read-from-disk pattern,
// since this is a static fixture file, not a log a concurrent writer might be touching.
internal static class ScmdbFixture
{
    public static string LoadV3SampleJson()
    {
        using var stream = typeof(ScmdbFixture).Assembly
            .GetManifestResourceStream("NexusApp.Tests.Fixtures.scmdb-v3-sample.json");
        Assert.NotNull(stream);
        using var sr = new StreamReader(stream!);
        return sr.ReadToEnd();
    }
}
