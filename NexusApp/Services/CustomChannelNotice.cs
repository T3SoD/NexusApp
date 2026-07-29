namespace NexusApp.Services;

/// <summary>Show/hide rule for the one-time "custom Game.log folder" Operations notice (issue #28).
/// Pure so the once-per-distinct-path behavior is headless-testable.</summary>
public static class CustomChannelNotice
{
    public static bool ShouldShow(GameChannel channel, string activePath, string noticedPath, bool authorized) =>
        channel == GameChannel.Custom
        && !authorized
        && !string.Equals(activePath, noticedPath, StringComparison.OrdinalIgnoreCase);
}
