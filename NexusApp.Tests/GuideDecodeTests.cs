using NexusApp.Views;
using Xunit;

namespace NexusApp.Tests;

// Decode sizing for the Mission Guides viewer. The guides are 4k to 5.6k pixels wide, so
// decoding every one at native resolution would cost up to about 116 MB per image. The rule
// is: decode at roughly twice the viewport while the image is parked at fit, and only upgrade
// to native once the zoom would out-run the decoded pixels. Pure so it is provable without a
// window; the overlay passes a 4096 cap through the same function.
public class GuideDecodeTests
{
    [Fact]
    public void Initial_decode_is_twice_viewport()
        => Assert.Equal(1952, GuideDecode.PickDecodeWidth(5500, 976, 0.163, 0.163, int.MaxValue));

    [Fact]
    public void Initial_decode_never_exceeds_native()
        => Assert.Equal(3841, GuideDecode.PickDecodeWidth(3841, 2400, 0.15, 0.15, int.MaxValue));

    [Fact]
    public void Deep_zoom_upgrades_to_native()
        => Assert.Equal(5500, GuideDecode.PickDecodeWidth(5500, 976, 0.5, 0.163, int.MaxValue));

    [Fact]
    public void Mild_zoom_keeps_the_first_decode()  // no re-decode churn until it would soften
        => Assert.Equal(1952, GuideDecode.PickDecodeWidth(5500, 976, 0.3, 0.163, int.MaxValue));

    [Fact]
    public void Overlay_cap_applies_everywhere()
    {
        Assert.Equal(640, GuideDecode.PickDecodeWidth(5500, 320, 0.06, 0.06, 4096));
        Assert.Equal(4096, GuideDecode.PickDecodeWidth(5500, 320, 1.0, 0.06, 4096));
    }

    [Fact]
    public void Unmeasured_viewport_falls_back_to_the_capped_native_width()
        => Assert.Equal(4096, GuideDecode.PickDecodeWidth(5500, 0, 0, 0, 4096));
}
