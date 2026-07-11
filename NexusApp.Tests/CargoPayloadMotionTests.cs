using NexusApp.Views;
using Xunit;

// Both tests live in one class so xUnit keeps them in the same collection: Motion.Reduced is a
// static mutable, and each test sets and restores it to avoid bleed into the parallel-run suite.
public class CargoPayloadMotionTests
{
    [Fact]
    public void Payload_CarriesReducedMotionFlag()
    {
        Motion.Reduced = true;
        try
        {
            var json = CargoWebView.BuildPayloadForTest(null, null);
            Assert.Contains("\"reduced\":true", json);
        }
        finally { Motion.Reduced = false; }
    }

    [Fact]
    public void Payload_ReducedFalseByDefault()
    {
        Motion.Reduced = false;
        var json = CargoWebView.BuildPayloadForTest(null, null);
        Assert.Contains("\"reduced\":false", json);
    }
}
