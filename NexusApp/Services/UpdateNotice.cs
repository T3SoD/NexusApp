using System.Globalization;

namespace NexusApp.Services;

// Pure, WPF-free presentation seam for every update surface (RelaunchNotice pattern): the
// Operations strips, the Settings > Diagnostics Updates rows and the install confirmation all
// read their copy and show/hide decisions here, so voice and logic are pinned by unit tests.
public static class UpdateNotice
{
    public const string ConsentEyebrow = "UPDATE CHECKS";
    public const string ConsentBody =
        "Nexus can check for new versions when it starts, at most once a day. It contacts " +
        "GitHub only and sends nothing about you. You can change this anytime in " +
        "Settings > Diagnostics.";
    public const string ConsentEnable = "Enable";
    public const string ConsentDecline = "No thanks";

    public const string UpdateEyebrow = "UPDATE AVAILABLE";
    public const string PostUpdateEyebrow = "UPDATED";

    public const string NeverChecked = "Never checked";
    public const string CheckFailed = "Couldn't check for updates. See nexus.log for details.";

    // Named causes for the failures the app can actually classify. Anything else keeps the
    // generic CheckFailed line above.
    public const string CheckFailedNotSignedYet =
        "The latest release is not signed yet. Updates stay hidden until it is signed.";
    public const string CheckFailedSignatureMissing =
        "The update manifest is missing its signature. Updates stay hidden until signing completes.";
    public const string CheckFailedNetwork =
        "Could not reach the update service. Check your connection and try again.";

    public const string VerifyFailedTitle = "Update verification failed";
    public const string VerifyFailedBody =
        "The downloaded file failed verification and was deleted. Nothing was installed.\n\n" +
        "Try again later. If this keeps happening, download the update from the releases page instead.";

    public const string InstallConfirmBody =
        "Nexus will close and the installer will open. Your settings, work orders, and blueprints are kept.";
    public static string InstallConfirmTitle(Version v) => $"Install Nexus {v.ToString(3)} now?";

    public static string UpdateBody(string current, Version available) =>
        $"Nexus {available.ToString(3)} is available. You are on {current}.";

    public static string DownloadingBody(Version v, long doneBytes, long totalBytes) =>
        $"Downloading Nexus {v.ToString(3)}. {doneBytes / 1048576} of {totalBytes / 1048576} MB.";

    public static string VerifyingBody(Version v) => $"Verifying the Nexus {v.ToString(3)} download.";

    public static string ReadyBodyInstaller(Version v) =>
        $"Nexus {v.ToString(3)} is downloaded and verified.";

    public static string ReadyBodyPortable(Version v) =>
        $"Nexus {v.ToString(3)} is downloaded and verified. Close Nexus and swap in the new folder when you are ready.";

    public static string PostUpdateBody(string version) =>
        $"Nexus updated to v{version}. See what changed in About > Changelog.";

    public static string FormatLastChecked(DateTime? lastUtc) =>
        lastUtc is null ? NeverChecked : "Last checked " + RelaunchNotice.FormatTimestamp(lastUtc);

    // The one-time opt-in strip shows only while the question has never been answered, and
    // never in the demo profile (the whole feature is inert there).
    public static bool ShouldShowConsentStrip(bool? updateCheckEnabled, bool isDemoProfile) =>
        !isDemoProfile && updateCheckEnabled is null;

    // "Ran an older version last session" is the one condition for the updated-to strip;
    // fresh installs (null) and manual downgrades stay quiet.
    public static bool ShouldShowPostUpdateStrip(string? lastSeenVersion, string currentVersion) =>
        UpdateVerifier.IsUpgrade(lastSeenVersion, currentVersion);

    // The Settings "Check now" status text. Takes the state and the failure kind by NAME so this
    // seam has no dependency on UpdateService; UpdateService's enum members must keep these names.
    public static string StatusLine(string state, Version? availableVersion, DateTime? lastCheckedUtc,
                                    bool lastFailureWasUserInitiated, string failureKind = "")
    {
        switch (state)
        {
            case "Failed":
                return lastFailureWasUserInitiated ? FailureLine(failureKind) : FormatLastChecked(lastCheckedUtc);
            case "UpToDate":
                return "Up to date";
            case "UpdateAvailable":
            case "Downloading":
            case "Verifying":
            case "ReadyToInstall":
            case "Installing":
                return availableVersion is null
                    ? FormatLastChecked(lastCheckedUtc)
                    : string.Create(CultureInfo.InvariantCulture, $"Nexus {availableVersion.ToString(3)} is available");
            default:
                return FormatLastChecked(lastCheckedUtc);
        }
    }

    // A manual check that failed says WHY when the cause is one the service could classify.
    // An unknown or unclassified kind falls back to the generic line, so a new failure kind
    // can never leave the row blank.
    private static string FailureLine(string failureKind) => failureKind switch
    {
        "NotSignedYet" => CheckFailedNotSignedYet,
        "SignatureMissing" => CheckFailedSignatureMissing,
        "Network" => CheckFailedNetwork,
        _ => CheckFailed,
    };
}
