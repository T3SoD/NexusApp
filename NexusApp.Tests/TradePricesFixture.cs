using System.IO;
using Xunit;

namespace NexusApp.Tests;

// Shared embedded-fixture loader for the trading tab's data-layer tests (Tasks 1/4/5): a
// hand-trimmed 28-row real capture of /commodities_prices_all (Laranite rows with known values
// across buy and sell terminals, plus two hand-authored coverage rows the real payload never
// contains - see uex_prices_all_sample.json's own header). Mirrors ScmdbFixture's embed-and-read
// pattern exactly.
internal static class TradePricesFixture
{
    public static string LoadSampleJson()
    {
        using var stream = typeof(TradePricesFixture).Assembly
            .GetManifestResourceStream("NexusApp.Tests.Fixtures.uex_prices_all_sample.json");
        Assert.NotNull(stream);
        using var sr = new StreamReader(stream!);
        return sr.ReadToEnd();
    }
}
