using NexusApp.Services;
using Xunit;

namespace NexusApp.Tests;

public class CraftAmountTests
{
    [Theory]
    // sub-1-SCU amounts render as centiSCU (1 SCU = 100 cSCU)
    [InlineData(0.02, "SCU", "2", "cSCU", "2 cSCU")]
    [InlineData(0.05, "SCU", "5", "cSCU", "5 cSCU")]
    [InlineData(0.015, "SCU", "1.5", "cSCU", "1.5 cSCU")]   // half-cSCU precision preserved
    [InlineData(0.01, "SCU", "1", "cSCU", "1 cSCU")]
    [InlineData(0.99, "SCU", "99", "cSCU", "99 cSCU")]
    // whole-SCU-or-larger amounts stay in SCU
    [InlineData(1, "SCU", "1", "SCU", "1 SCU")]
    [InlineData(5, "SCU", "5", "SCU", "5 SCU")]
    [InlineData(12.5, "SCU", "12.5", "SCU", "12.5 SCU")]
    // unit match is case-insensitive
    [InlineData(0.02, "scu", "2", "cSCU", "2 cSCU")]
    // non-SCU units are never converted
    [InlineData(3, "units", "3", "units", "3 units")]
    [InlineData(0.5, "units", "0.5", "units", "0.5 units")]
    // zero is not centi-converted
    [InlineData(0, "SCU", "0", "SCU", "0 SCU")]
    public void FormatsHybridScuAndCentiScu(double qty, string unit, string value, string unitOut, string combined)
    {
        Assert.Equal(value, CraftAmount.Value(qty, unit));
        Assert.Equal(unitOut, CraftAmount.Unit(qty, unit));
        Assert.Equal(combined, CraftAmount.Format(qty, unit));
    }
}
