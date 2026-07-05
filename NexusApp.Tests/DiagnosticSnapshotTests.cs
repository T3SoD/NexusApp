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
}
