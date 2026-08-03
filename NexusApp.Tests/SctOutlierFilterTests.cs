using System;
using System.Collections.Generic;
using System.Linq;
using NexusApp.Services;
using Xunit;

namespace NexusApp.Tests;

public class SctOutlierFilterTests
{
    private static readonly DateTime T = new(2026, 8, 3, 12, 0, 0, DateTimeKind.Utc);

    private static SctListing Row(string commodity, string transaction, double price, string location = "loc")
        => new(location, transaction, commodity, price, 0, 0, T);

    [Fact]
    public void DropsTheFatFingeredPrice_TheOwnerActuallyHit()
    {
        // Verbatim from the live snapshot: quartz BUYS across every terminal, median 4,400, with
        // one listing at 84,200 - a mistyped 4,200 that a newest-wins lookup then showed.
        var rows = new List<SctListing>
        {
            Row("quartz", "BUYS", 3500), Row("quartz", "BUYS", 4200), Row("quartz", "BUYS", 4200),
            Row("quartz", "BUYS", 4400), Row("quartz", "BUYS", 4400), Row("quartz", "BUYS", 4600),
            Row("quartz", "BUYS", 4600), Row("quartz", "BUYS", 84200, "pyro > rod's fuel 'n supplies"),
        };
        var (kept, dropped) = SctOutlierFilter.Apply(rows);
        Assert.Equal(1, dropped);
        Assert.DoesNotContain(kept, r => r.Price == 84200);
        Assert.Equal(7, kept.Count);
    }

    [Fact]
    public void KeepsGenuineSpreadBetweenTerminals()
    {
        // Real prices for one commodity vary by location. Nothing here is near 4x the median, so
        // nothing may be dropped - a filter that trims ordinary variation is worse than none.
        var rows = new List<SctListing>
        {
            Row("laranite", "SELLS", 3000), Row("laranite", "SELLS", 3300),
            Row("laranite", "SELLS", 3705), Row("laranite", "SELLS", 4100),
            Row("laranite", "SELLS", 4600),
        };
        var (kept, dropped) = SctOutlierFilter.Apply(rows);
        Assert.Equal(0, dropped);
        Assert.Equal(5, kept.Count);
    }

    [Fact]
    public void DropsAPriceFarBelowTheMedianToo()
    {
        // The mirror typo: a dropped digit, 420 for 4,200.
        var rows = new List<SctListing>
        {
            Row("quartz", "BUYS", 4200), Row("quartz", "BUYS", 4200), Row("quartz", "BUYS", 4400),
            Row("quartz", "BUYS", 4400), Row("quartz", "BUYS", 420),
        };
        var (kept, dropped) = SctOutlierFilter.Apply(rows);
        Assert.Equal(1, dropped);
        Assert.DoesNotContain(kept, r => r.Price == 420);
    }

    [Fact]
    public void JudgesEachSideSeparately()
    {
        // A terminal buys low and sells high, so pooling both sides would widen the median enough
        // to let real typos through. Here the SELLS group's own median is 800, and a 9,000 SELLS
        // listing must go even though it would look ordinary beside the BUYS prices.
        var rows = new List<SctListing>
        {
            Row("waste", "BUYS", 8000), Row("waste", "BUYS", 8200), Row("waste", "BUYS", 8400), Row("waste", "BUYS", 8600),
            Row("waste", "SELLS", 780), Row("waste", "SELLS", 800), Row("waste", "SELLS", 820), Row("waste", "SELLS", 840),
            Row("waste", "SELLS", 9000),
        };
        var (kept, dropped) = SctOutlierFilter.Apply(rows);
        Assert.Equal(1, dropped);
        Assert.DoesNotContain(kept, r => r.Price == 9000);
        Assert.Equal(4, kept.Count(r => r.Transaction == "BUYS"));
    }

    [Fact]
    public void JudgesEachCommoditySeparately()
    {
        var rows = new List<SctListing>
        {
            Row("quartz", "BUYS", 4200), Row("quartz", "BUYS", 4200), Row("quartz", "BUYS", 4400), Row("quartz", "BUYS", 4400),
            Row("stileron", "BUYS", 150000), Row("stileron", "BUYS", 152000),
            Row("stileron", "BUYS", 148000), Row("stileron", "BUYS", 151000),
        };
        // Stileron is legitimately 30x quartz. Grouping by commodity is what stops that being read
        // as an outlier.
        var (kept, dropped) = SctOutlierFilter.Apply(rows);
        Assert.Equal(0, dropped);
        Assert.Equal(8, kept.Count);
    }

    [Fact]
    public void LeavesThinlyReportedCommoditiesAlone()
    {
        // Three listings cannot establish a trustworthy median, so nothing is judged against them
        // even though one is wildly apart. Better a suspect price than a confidently wrong filter.
        var rows = new List<SctListing>
        {
            Row("agricium", "BUYS", 3000), Row("agricium", "BUYS", 3100), Row("agricium", "BUYS", 90000),
        };
        var (kept, dropped) = SctOutlierFilter.Apply(rows);
        Assert.Equal(0, dropped);
        Assert.Equal(3, kept.Count);
    }

    [Fact]
    public void ZeroPricesNeitherDragTheMedianNorGetDropped()
    {
        // SCT publishes zero-price rows; they are not a price, so they must not pull the median
        // down and turn real listings into outliers. They are also not typos, so they stay.
        var rows = new List<SctListing>
        {
            Row("quartz", "BUYS", 0), Row("quartz", "BUYS", 0), Row("quartz", "BUYS", 0),
            Row("quartz", "BUYS", 4200), Row("quartz", "BUYS", 4200), Row("quartz", "BUYS", 4400),
            Row("quartz", "BUYS", 4400), Row("quartz", "BUYS", 84200),
        };
        var (kept, dropped) = SctOutlierFilter.Apply(rows);
        Assert.Equal(1, dropped);
        Assert.Equal(3, kept.Count(r => r.Price == 0));
    }

    [Fact]
    public void EmptyInputIsNotAnError()
    {
        var (kept, dropped) = SctOutlierFilter.Apply(Array.Empty<SctListing>());
        Assert.Empty(kept);
        Assert.Equal(0, dropped);
    }

    [Fact]
    public void PreservesOrderAndIdentityOfWhatItKeeps()
    {
        var rows = new List<SctListing>
        {
            Row("quartz", "BUYS", 4200, "a"), Row("quartz", "BUYS", 84200, "b"),
            Row("quartz", "BUYS", 4400, "c"), Row("quartz", "BUYS", 4400, "d"),
            Row("quartz", "BUYS", 4600, "e"),
        };
        var (kept, _) = SctOutlierFilter.Apply(rows);
        Assert.Equal(new[] { "a", "c", "d", "e" }, kept.Select(r => r.Location).ToArray());
    }
}
