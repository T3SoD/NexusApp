using System.Globalization;

namespace NexusApp.Services;

// Pure, WPF-free presentation seam for every update surface (RelaunchNotice pattern): the
// Operations strips, the Settings > Updates rows and the install confirmation all
// read their copy and show/hide decisions here, so voice and logic are pinned by unit tests.
public static class UpdateNotice
{
    public const string ConsentEyebrow = "UPDATE CHECKS";
    public const string ConsentBody =
        "Nexus can check for new versions each time it starts. It contacts " +
        "GitHub only and sends nothing about you. You can change this anytime in " +
        "Settings > Updates.";
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

    public const string InstallConfirmBodyPortable =
        "Nexus will close for a moment and reopen as the new version. Your settings, work orders, and blueprints are kept.";

    // Post-confirm staging failure: the app is still open, nothing was touched, and both
    // recovery paths are named (house pattern: what happened, what the app did, what to do).
    public const string PrepareFailedBody =
        "Couldn't prepare the update. Nothing was changed. Try again, or update manually from Settings > Updates.";

    // The stuck-rollback state: the swap failed AND the rollback could not put every file
    // back, so the previous version lives in .old files that only startup recovery can
    // restore. Retrying in this session would be a lie, so this copy asks for the restart the
    // recovery needs and no Try again button is offered beside it.
    public const string RestorePendingBody =
        "The update could not finish. Nexus will finish restoring the previous version the next time it starts. Close Nexus and start it again.";

    public static string InstallConfirmTitle(Version v) => $"Install Nexus {v.ToString(3)} now?";

    public static string UpdateBody(string current, Version available) =>
        $"Nexus {available.ToString(3)} is available. You are on {current}.";

    public static string DownloadingBody(Version v, long doneBytes, long totalBytes) =>
        $"Downloading Nexus {v.ToString(3)}. {doneBytes / 1048576} of {totalBytes / 1048576} MB.";

    public static string VerifyingBody(Version v) => $"Verifying the Nexus {v.ToString(3)} download.";

    public static string ReadyBodyInstaller(Version v) =>
        $"Nexus {v.ToString(3)} is downloaded and verified.";

    // Installer parity: same sentence as ReadyBodyInstaller. Kept as its own method so call
    // sites and tests stay explicit about which flavor they are rendering.
    public static string ReadyBodyPortable(Version v) => ReadyBodyInstaller(v);

    public static string PreparingBody(Version v) =>
        $"Preparing Nexus {v.ToString(3)}. Nexus will close and reopen in a moment.";

    // The guided manual flow is a different, intentional flow, never an error: the app
    // extracts and opens the folders, the user does one copy.
    public static string ReadyBodyPortableManual(Version v) =>
        $"Nexus {v.ToString(3)} is downloaded and verified. Nexus cannot replace its own files from " +
        "this location, so this update finishes with one quick copy.";

    public static string UnpackingBody(Version v) => $"Unpacking Nexus {v.ToString(3)}.";

    public static string ManualHandoffBody(Version v) =>
        $"Two folders are open: the new Nexus {v.ToString(3)} and your current Nexus. Close Nexus, " +
        "then copy everything from the new folder into the current one, replacing files when asked.";

    // Sentence order is the house pattern: what happened, what the app did, where you stand.
    public static string SwapFailedBody(string attempted, string current) =>
        $"The update to Nexus {attempted} could not finish. Nexus restored the previous version " +
        $"and nothing changed. You are still on Nexus {current}.";

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
            case "ManualHandoff":
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
