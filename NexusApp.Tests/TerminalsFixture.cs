using System.IO;
using Xunit;

namespace NexusApp.Tests;

// Shared embedded-fixture loader for /terminals parsing tests: a hand-trimmed 9-row real capture
// (nexus-assets/specs/sct-uex-benchmark-raw/uex_terminals.json, 823 rows) covering the location
// hierarchy fields (orbit_name/planet_name/moon_name) ProximityTiers (Task 3) depends on, plus one
// hand-authored bad row (missing star_system_name) the real payload never contains - see
// uex_terminals_sample.json's own rows. Mirrors TradePricesFixture's embed-and-read pattern exactly.
internal static class TerminalsFixture
{
    public static string LoadSampleJson()
    {
        using var stream = typeof(TerminalsFixture).Assembly
            .GetManifestResourceStream("NexusApp.Tests.Fixtures.uex_terminals_sample.json");
        Assert.NotNull(stream);
        using var sr = new StreamReader(stream!);
        return sr.ReadToEnd();
    }
}
