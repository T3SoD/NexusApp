namespace NexusApp.Services;

// Star Citizen launcher channel, inferred from the folder that holds Game.log (issue #28).
// LIVE and HOTFIX point at the live universe: HOTFIX is CIG's intermittent staging channel for an
// imminent LIVE patch and progress there is real LIVE-account progress. PTU / EPTU / TECH-PREVIEW
// are wiped test environments (copied account, progress never carries). Custom = an unrecognized
// parent folder (non-standard install): single-file semantics, no auto-follow, and blueprint
// recording only with the user's explicit Settings authorization.
public enum GameChannel { Live, Hotfix, Ptu, Eptu, TechPreview, Custom }

public static class GameChannels
{
    // Known channel folder names under one StarCitizen root. LIVE first: FindGameLog probes in
    // this order and the first hit wins, so default installs keep resolving to LIVE.
    public static readonly string[] KnownFolders = { "LIVE", "HOTFIX", "PTU", "EPTU", "TECH-PREVIEW" };

    public static GameChannel FromLogPath(string? gameLogPath)
    {
        if (string.IsNullOrWhiteSpace(gameLogPath)) return GameChannel.Custom;
        string? dir;
        try { dir = System.IO.Path.GetFileName(System.IO.Path.GetDirectoryName(gameLogPath)); }
        catch { return GameChannel.Custom; }
        return dir?.ToUpperInvariant() switch
        {
            "LIVE"         => GameChannel.Live,
            "HOTFIX"       => GameChannel.Hotfix,
            "PTU"          => GameChannel.Ptu,
            "EPTU"         => GameChannel.Eptu,
            "TECH-PREVIEW" => GameChannel.TechPreview,
            _              => GameChannel.Custom,
        };
    }

    public static string FolderName(GameChannel c) => c switch
    {
        GameChannel.Live        => "LIVE",
        GameChannel.Hotfix      => "HOTFIX",
        GameChannel.Ptu         => "PTU",
        GameChannel.Eptu        => "EPTU",
        GameChannel.TechPreview => "TECH-PREVIEW",
        _                       => "CUSTOM",
    };

    /// <summary>True when progress on this channel is real LIVE-account progress, i.e. blueprint
    /// receipts should be recorded. Test channels are never real; custom folders defer to the
    /// user's Settings authorization (off by default).</summary>
    public static bool RecordsRealData(GameChannel c, bool customAuthorized) =>
        c is GameChannel.Live or GameChannel.Hotfix
        || (c == GameChannel.Custom && customAuthorized);

    /// <summary>PTU-family: a wiped test environment whose progress never reaches the LIVE account.</summary>
    public static bool IsTest(GameChannel c) =>
        c is GameChannel.Ptu or GameChannel.Eptu or GameChannel.TechPreview;

    /// <summary>Chip text suffix: empty on LIVE (players on a default install see no change),
    /// " · CHANNEL" otherwise. The dot is the same U+00B7 the SHARD chip already uses.</summary>
    public static string ChipSuffix(GameChannel c) =>
        c == GameChannel.Live ? "" : $" · {FolderName(c)}";
}
