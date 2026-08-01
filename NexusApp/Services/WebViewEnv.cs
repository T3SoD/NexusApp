using System.IO;
using System.Threading.Tasks;
using Microsoft.Web.WebView2.Core;

namespace NexusApp.Services;

// One shared CoreWebView2Environment for every WebView2 host in the process. A second environment
// on the same user-data folder throws ("folder in use"), so every WebView2 host (cargo 3D viewport,
// starmap MAP host) must share this one.
internal static class WebViewEnv
{
    private static Task<CoreWebView2Environment>? _sharedEnv;

    internal static Task<CoreWebView2Environment> SharedAsync()
    {
        if (_sharedEnv == null)
        {
            // Keep the WebView2 profile in a writable per-user folder (the exe may live under Program Files).
            var dataFolder = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "NexusApp",
                AppPaths.IsDemoProfile ? "WebView2_demo" : "WebView2");
            Directory.CreateDirectory(dataFolder);
            _sharedEnv = CoreWebView2Environment.CreateAsync(null, dataFolder, null);
        }
        return _sharedEnv;
    }

    // Nulls the cached task so the next SharedAsync() call creates a fresh environment. Called on
    // init failure (the cached task may be faulted or the folder may be locked/corrupt) and before
    // a portable self-swap shuts every WebView2 host down.
    internal static void Reset() => _sharedEnv = null;
}
