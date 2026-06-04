using System.Reflection;

namespace Nexus_v4;

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
}
