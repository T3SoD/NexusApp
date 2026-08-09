using System.Text;
using NexusApp.Models;
using NexusApp.Services;
using Xunit;

namespace NexusApp.Tests;

public class AutoLoadTimeTableTests
{
    [Fact]
    public void LoadEmbedded_ShipsCalibratedWithLiveKioskValues()
    {
        var t = AutoLoadTimeTable.LoadEmbedded();
        Assert.True(t.Calibrated);
        Assert.Equal(72.0, t.BaseSeconds(TransactionKind.Buy));
        Assert.Equal(72.0, t.BaseSeconds(TransactionKind.Sell));
        Assert.Equal(9.0, t.SecondsPerBox(TransactionKind.Buy, 24m));
        Assert.Equal(0.0, t.SecondsPerBox(TransactionKind.Buy, 0.5m));
        Assert.Equal(10.8, t.SecondsPerBox(TransactionKind.Sell, 32m));
        Assert.Equal(new[] { 1, 2, 4, 8, 16, 24, 32 }, t.IntSizes);
    }

    [Fact]
    public void Load_MalformedStream_FoldsToEmptyDarkTable()
    {
        using var s = new MemoryStream(Encoding.UTF8.GetBytes("not json"));
        var t = AutoLoadTimeTable.Load(s);
        Assert.False(t.Calibrated);
        Assert.Null(t.BaseSeconds(TransactionKind.Buy));
        Assert.Null(t.SecondsPerBox(TransactionKind.Buy, 24m));
        Assert.Empty(t.IntSizes);
    }

    [Fact]
    public void SecondsPerBox_UnknownSize_IsNull()
    {
        var t = AutoLoadTimeTable.LoadEmbedded();
        Assert.Null(t.SecondsPerBox(TransactionKind.Buy, 3m));
    }
}
