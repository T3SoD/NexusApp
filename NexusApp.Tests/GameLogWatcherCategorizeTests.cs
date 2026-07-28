using NexusApp.Services;
using Xunit;

namespace NexusApp.Tests;

// GameLogWatcher.Categorize (Services/GameLogWatcher.cs:243-260) is a public static, headless
// keyword classifier with no WPF/timer dependency - it had zero test coverage. Already public, so
// no access-seam change was needed here (unlike OcrService.ExtractRsValue). One row per
// LogCategory, in the same keyword-cascade order the method checks, plus the no-keyword-match
// default (Other).
public class GameLogWatcherCategorizeTests
{
    [Theory]
    [InlineData("Acquired blueprint: Explorer Backpack", LogCategory.Blueprint)]
    [InlineData("<Actor Death> Player_1 killed by Player_2", LogCategory.Kill)]
    [InlineData("<Vehicle Destruction> hull breach detected", LogCategory.VehicleDestruction)]
    [InlineData("Quantum jump requested to Pyro", LogCategory.Quantum)]
    [InlineData("Loading screen for new zone: Stanton", LogCategory.Location)]
    [InlineData("Player spawn near habitation module", LogCategory.Spawn)]
    [InlineData("Login: account authenticated", LogCategory.Login)]
    [InlineData("changelist 9876543 branch sc-alpha-4.9", LogCategory.Version)]
    [InlineData("connect to server region us-east", LogCategory.Connection)]
    [InlineData("mission accepted: new delivery contract", LogCategory.Mission)]
    [InlineData("refinery queue: commodity price updated", LogCategory.Economy)]
    [InlineData("Player entered the persistent universe", LogCategory.Other)]
    public void Categorize_ReturnsExpectedCategory(string line, LogCategory expected)
    {
        Assert.Equal(expected, GameLogWatcher.Categorize(line));
    }
}
