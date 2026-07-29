namespace NexusApp.Services;

/// <summary>Channel gate for the past-logs blueprint import (issue #28). Pure so the messages and
/// the matrix are headless-testable; BlueprintImportFlow consults it before scanning.</summary>
public static class BlueprintImportGate
{
    /// <summary>Why importing ownership from a log on this channel is refused, or null when allowed.</summary>
    public static string? Refusal(GameChannel channel, bool customAuthorized)
    {
        if (GameChannels.IsTest(channel))
            return $"This Game.log is from the {GameChannels.FolderName(channel)} test environment. " +
                   "Test-channel progress is wiped by CIG and is not recorded as owned blueprints.";
        if (channel == GameChannel.Custom && !customAuthorized)
            return "This Game.log is in a custom folder, where blueprint recording is off by default. " +
                   "Allow it in Settings > Game (Record blueprints from this custom folder), then import again.";
        return null;
    }
}
