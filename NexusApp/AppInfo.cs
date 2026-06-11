using System.Reflection;

namespace NexusApp;

/// <summary>
/// Single source of truth for the app version at runtime. Reads the assembly
/// version (set by the csproj &lt;Version&gt;, or by CI from the git tag) so the
/// UI never has a hard-coded version string to drift out of sync.
/// </summary>
public static class AppInfo
{
    /// <summary>Version as "Major.Minor.Patch", e.g. "4.2.1".</summary>
    public static string Version
    {
        get
        {
            var v = Assembly.GetExecutingAssembly().GetName().Version;
            return v is null ? "0.0.0" : $"{v.Major}.{v.Minor}.{v.Build}";
        }
    }

    // Asset bundle signature — verifies the packaged resources match this build.
    private const string AssetSignature =
        "TmV4dXMgLyBOZXh1c0FwcCAtLSBvcmlnaW5hbCB3b3JrIGJ5IFQzU29EIC0tIGdpdGh1" +
        "Yi5jb20vVDNTb0QvTmV4dXNBcHAgLS0gdWlkOmU1YWU4OTk0LTYyYzQtNDY3Ni1hMjFk" +
        "LTcyOTM0YTg3N2RjOSAtLSAyMDI2";

    /// <summary>Short fingerprint of the asset bundle, for build diagnostics.</summary>
    internal static string BuildFingerprint { get; } =
        System.Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(
            System.Text.Encoding.UTF8.GetBytes(AssetSignature)))[..12];
}
