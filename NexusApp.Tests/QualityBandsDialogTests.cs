using NexusApp.Models;
using NexusApp.Views;
using Xunit;

namespace NexusApp.Tests;

public class QualityBandsDialogTests
{
    // Chart frame from the mock: W 596, H 250, L 44, R 14, T 18, B 34.
    [Theory]
    [InlineData(0, 44)]
    [InlineData(500, 313)]
    [InlineData(1000, 582)]
    public void ChartX_MapsQualityAcrossThePlotWidth(double q, double x) =>
        Assert.Equal(x, QualityBandsDialog.ChartX(q), 6);

    [Theory]
    [InlineData(0, 216)]
    [InlineData(500, 117)]
    [InlineData(1000, 18)]
    public void ChartY_MapsQualityUpThePlotHeight(double q, double y) =>
        Assert.Equal(y, QualityBandsDialog.ChartY(q), 6);

    [Theory]
    [InlineData(0, 399, 318, "+119")]
    [InlineData(400, 599, 511, "+12")]
    [InlineData(600, 699, 614, "-35")]
    [InlineData(950, 998, 953, "-21")]
    [InlineData(999, 1000, 1000, "+1")]
    [InlineData(10, 20, 15, "0")]
    [InlineData(0, 1, 0, "0")]   // -0.5 rounds half up to 0, as in the mock's Math.round
    public void VsMiddleText_SignsAndRoundsTheOffset(int s, int e, int v, string text) =>
        Assert.Equal(text, QualityBandsDialog.VsMiddleText(new QualityBand(s, e, v)));

    [Theory]
    [InlineData(850, false)]
    [InlineData(851, true)]
    [InlineData(999, true)]
    [InlineData(0, false)]
    public void MarkerLabel_AnchorsLeftAbove850(int q, bool left) =>
        Assert.Equal(left, QualityBandsDialog.MarkerLabelLeft(q));

    [Fact]
    public void Copy_MatchesTheMock()
    {
        Assert.Equal("Q952 crafts as 953", QualityBandsDialog.MarkerText(952, 953));
        Assert.Equal("950-998 crafts as 953", QualityBandsDialog.TipText(new QualityBand(950, 998, 953)));
        Assert.Equal("700 to 799", QualityBandsDialog.HoldText(new QualityBand(700, 799, 783)));
        Assert.Equal(
            "Game record for Aluminium. Every ore has its own bands. The dashed line is what an unbanded quality would craft as. Click a band to set the Frame + Stock slot on the P4-AR Rifle to its start.",
            QualityBandsDialog.FooterText("Aluminium", "Frame + Stock", "P4-AR Rifle"));
    }

    [Theory]
    [InlineData(0, 399, true)]
    [InlineData(900, 949, false)]
    [InlineData(700, 760, false)]
    [InlineData(700, 761, true)]
    public void ValueLabel_ShowsOnlyOnBandsWiderThan60(int s, int e, bool shown) =>
        Assert.Equal(shown, QualityBandsDialog.ShowsValueLabel(new QualityBand(s, e, 500)));

    [Fact]
    public void StepEnd_ClampsTheLastBandToTheAxis()
    {
        Assert.Equal(400, QualityBandsDialog.StepEnd(new QualityBand(0, 399, 318)));
        Assert.Equal(1000, QualityBandsDialog.StepEnd(new QualityBand(999, 1000, 1000)));
    }

    [Fact]
    public void BandAt_FindsTheBandOrNull()
    {
        var bands = new List<QualityBand> { new(0, 399, 318), new(400, 1000, 600) };
        Assert.Equal(318, QualityBandsDialog.BandAt(bands, 399)?.Mapped);
        Assert.Equal(600, QualityBandsDialog.BandAt(bands, 400)?.Mapped);
        Assert.Null(QualityBandsDialog.BandAt([], 500));
    }
}
