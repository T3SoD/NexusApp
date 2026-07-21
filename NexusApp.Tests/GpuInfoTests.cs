using NexusApp.Services;
using Xunit;

namespace NexusApp.Tests;

// Startup display-adapter line: the driver version is the triage pivot for render thread
// failures, and this line is what lets support say "known-bad driver, update to X" instead
// of generic advice. Formatting only; the registry read is environment glue.
public class GpuInfoTests
{
    [Fact]
    public void DescriptionAndVersion_ProduceWinTaggedLine()
    {
        var line = GpuInfo.AdapterLine("NVIDIA GeForce RTX 4080", "32.0.15.6094");
        Assert.Equal("[WIN] display adapter: NVIDIA GeForce RTX 4080 (driver 32.0.15.6094)", line);
    }

    [Fact]
    public void MissingVersion_StillLogsAdapterWithUnknownDriver()
    {
        var line = GpuInfo.AdapterLine("AMD Radeon RX 7900 XTX", null);
        Assert.Equal("[WIN] display adapter: AMD Radeon RX 7900 XTX (driver unknown)", line);
    }

    [Fact]
    public void MissingDescription_ProducesNoLine()
    {
        Assert.Null(GpuInfo.AdapterLine(null, "1.0"));
        Assert.Null(GpuInfo.AdapterLine("   ", "1.0"));
    }
}
