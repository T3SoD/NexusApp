using NexusApp.Views;
using Xunit;

namespace NexusApp.Tests;

public class StarterBlueprintTests
{
    [Fact]
    public void StarterLine_ShownForStarterWithNoUnlocks()
    {
        Assert.Equal("Starter blueprint. Every player has this one from the first login.",
            MainWindow.UnlockEmptyText(true));
        Assert.Equal("No unlock information available", MainWindow.UnlockEmptyText(false));
    }
}
