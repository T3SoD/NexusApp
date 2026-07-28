using System;
using System.Collections.Generic;
using NexusApp.Services;
using Xunit;

namespace NexusApp.Tests;

public class DiagnosticSnapshotTests
{
    [Fact]
    public void Build_IncludesContextHeaderSettingsAndLog()
    {
        var settings = new List<(string, string)>
        {
            ("Theme", "luxury"),
            ("Session Tracking", "on"),
        };
        var snap = DiagnosticSnapshot.Build(
            appVersion: "5.4.5",
            gameVersion: "4.8.2",
            miningDataVersion: "1.2.5",
            osVersion: "Microsoft Windows NT 10.0.19045.0",
            settings: settings,
            logContents: "2026-06-22 10:00:00 [INFO] Nexus starting\n2026-06-22 10:00:01 [ERROR] boom",
            timestamp: new DateTime(2026, 6, 22, 10, 5, 0));

        Assert.Contains("Nexus diagnostic snapshot", snap);
        Assert.Contains("Taken: 2026-06-22 10:05:00", snap);
        Assert.Contains("App version: 5.4.5", snap);
        Assert.Contains("Star Citizen game data: 4.8.2", snap);
        Assert.Contains("Mining data: 1.2.5", snap);
        Assert.Contains("OS: Microsoft Windows NT 10.0.19045.0", snap);
        Assert.Contains("Theme: luxury", snap);
        Assert.Contains("Session Tracking: on", snap);
        Assert.Contains("[ERROR] boom", snap);
    }

    [Fact]
    public void Build_IncludesUnmatchedBlueprintSection_WhenProvided()
    {
        var snap = DiagnosticSnapshot.Build(
            "6.3.0", "4.8.3", "1.2.5", "os", new List<(string, string)>(),
            "log line", new DateTime(2026, 7, 9, 12, 0, 0),
            unmatchedLogContents: "2026-07-09 11:59:00 | live | app 6.3.0 | map not loaded | P IND 0B Defiant | <line>");

        Assert.Contains("--- unmatched_blueprints.log ---", snap);
        Assert.Contains("P IND 0B Defiant", snap);
    }

    [Fact]
    public void Build_IncludesPreviousLogSection_WhenProvided()
    {
        var snap = DiagnosticSnapshot.Build(
            "6.4.2", "4.9.0", "1.3.0", "os", new List<(string, string)>(),
            "current line", new DateTime(2026, 7, 21, 12, 0, 0),
            previousLogContents: "2026-07-18 09:00:00 [ERROR] evidence from before rotation");

        Assert.Contains("--- nexus.log.1 (previous) ---", snap);
        Assert.Contains("evidence from before rotation", snap);
    }

    [Fact]
    public void Build_OmitsPreviousLogSection_WhenAbsent()
    {
        var snap = DiagnosticSnapshot.Build(
            "6.4.2", "4.9.0", "1.3.0", "os", new List<(string, string)>(),
            "current line", new DateTime(2026, 7, 21, 12, 0, 0));

        Assert.DoesNotContain("nexus.log.1", snap);
    }

    [Fact]
    public void Build_OmitsUnmatchedBlueprintSection_WhenEmpty()
    {
        var snap = DiagnosticSnapshot.Build(
            "6.3.0", "4.8.3", "1.2.5", "os", new List<(string, string)>(),
            "log line", new DateTime(2026, 7, 9, 12, 0, 0));

        Assert.DoesNotContain("unmatched_blueprints.log", snap);
    }

    [Fact]
    public void RedactUserProfile_ReplacesProfilePrefixOnly()
    {
        var home = @"C:\Users\alice";
        // the profile prefix (which carries the Windows username) is masked
        Assert.Equal(@"%USERPROFILE%\AppData\Local\...\Game.log",
            DiagnosticSnapshot.RedactUserProfile(@"C:\Users\alice\AppData\Local\...\Game.log", home));
        // prefix match is case-insensitive
        Assert.Equal(@"%USERPROFILE%\x", DiagnosticSnapshot.RedactUserProfile(@"c:\users\alice\x", home));
        // a path outside the profile has no username to leak, so it is unchanged
        Assert.Equal(@"D:\Games\StarCitizen\Game.log",
            DiagnosticSnapshot.RedactUserProfile(@"D:\Games\StarCitizen\Game.log", home));
        // empty inputs are safe
        Assert.Equal("", DiagnosticSnapshot.RedactUserProfile("", home));
    }

    [Fact]
    public void Build_HandlesEmptyLog()
    {
        var snap = DiagnosticSnapshot.Build(
            "5.4.5", "4.8.2", "1.2.5", "Windows", new List<(string, string)>(), "", new DateTime(2026, 6, 22, 0, 0, 0));
        Assert.Contains("(log is empty)", snap);
    }

    // The snapshot's biggest leak risk is a profile path embedded mid-line inside the raw log blob
    // (e.g. the "[GameLog] ... watching: <path>" line), not just the single curated Game.log path
    // row - RedactUserProfile must scan the whole blob, not only a whole-string prefix match.
    [Fact]
    public void RedactUserProfile_ReplacesPathEmbeddedMidLine_InAMultiLineBlob()
    {
        var home = @"C:\Users\alice";
        var blob =
            "2026-07-28 10:00:00 [INFO] [WIN] Nexus 6.10.2 starting\n" +
            @"2026-07-28 10:00:01 [INFO] [GameLog] session tracking + auto-track always on; watching: C:\Users\alice\AppData\Local\StarCitizen\LIVE\Game.log" + "\n" +
            "2026-07-28 10:00:02 [INFO] [WIN] process DPI awareness: PerMonitorV2";

        var redacted = DiagnosticSnapshot.RedactUserProfile(blob, home);

        Assert.DoesNotContain("alice", redacted, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(@"watching: %USERPROFILE%\AppData\Local\StarCitizen\LIVE\Game.log", redacted);
        // Unrelated lines in the same blob survive untouched.
        Assert.Contains("Nexus 6.10.2 starting", redacted);
        Assert.Contains("process DPI awareness: PerMonitorV2", redacted);
    }

    [Fact]
    public void RedactUserProfile_ReplacesForwardSlashVariant_OfTheProfilePath()
    {
        var home = @"C:\Users\alice";
        // A user-typed Settings override (or an exception message built from it) can carry forward
        // slashes even though the profile constant itself is backslash-form.
        var line = "Could not open C:/Users/alice/Documents/game.log: file in use";

        var redacted = DiagnosticSnapshot.RedactUserProfile(line, home);

        Assert.DoesNotContain("alice", redacted, StringComparison.OrdinalIgnoreCase);
        Assert.Equal("Could not open %USERPROFILE%/Documents/game.log: file in use", redacted);
    }

    [Fact]
    public void RedactUserProfile_ReplacesEveryOccurrence_WhenThePathAppearsMoreThanOnce()
    {
        var home = @"C:\Users\alice";
        var blob = @"C:\Users\alice\a.log then again C:\Users\alice\b.log";

        var redacted = DiagnosticSnapshot.RedactUserProfile(blob, home);

        Assert.DoesNotContain("alice", redacted, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(@"%USERPROFILE%\a.log then again %USERPROFILE%\b.log", redacted);
    }

    [Fact]
    public void RedactUserProfile_LeavesTextWithNoProfilePath_Unchanged()
    {
        var home = @"C:\Users\alice";
        var line = "2026-07-28 10:00:00 [INFO] [SCAN] scan confirmed: value=48210";

        Assert.Equal(line, DiagnosticSnapshot.RedactUserProfile(line, home));
    }
}
