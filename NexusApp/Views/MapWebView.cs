using System.IO;
using System.Text.Json;
using System.Windows.Controls;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;
using NexusApp.Services;

namespace NexusApp.Views;

// Hosts the starmap scene (Web/map/index.html, Three.js) in a WebView2, mirroring CargoWebView's
// pattern exactly: C# stays the source of truth (catalog + layer state), the page renders it and
// posts back selection/measure interactions as JSON. Fully offline - the scene assets and the app's
// own bundled type faces (nexus.fonts, read-only, mirrors the nexus.hulls precedent in CargoWebView)
// are served through virtual host mappings, so no network is ever touched. Shares one
// CoreWebView2Environment with the cargo 3D viewport via WebViewEnv (a second environment on the
// same user-data folder throws).
public sealed class MapWebView : UserControl
{
    private readonly WebView2 _web = new();
    private bool _ready;
    private string? _pending;

    // JS {type:"ready"} - the page's own scene has finished initializing and is ready for the
    // first init payload. Distinct from _ready above, which gates PostJson on the WebView2
    // navigation lifecycle (NavigationCompleted), not on the page's own JS readiness.
    public event Action? Ready;
    public event Action<int>? PinClicked;
    public event Action<int>? PinDoubleClicked;
    public event Action<int, int>? MeasurePicked;

    public MapWebView()
    {
        _web.DefaultBackgroundColor = System.Drawing.Color.FromArgb(0xFF, 0x07, 0x0B, 0x11);
        Content = _web;
        Loaded += async (_, _) => await InitAsync();
    }

    private async Task InitAsync()
    {
        if (_web.CoreWebView2 != null) return;

        try
        {
            await _web.EnsureCoreWebView2Async(await WebViewEnv.SharedAsync());

            var core = _web.CoreWebView2!;
            var siteFolder = Path.Combine(AppContext.BaseDirectory, "Web", "map");
            core.SetVirtualHostNameToFolderMapping("nexus.map", siteFolder, CoreWebView2HostResourceAccessKind.Allow);
            // Same TTFs the WPF chrome uses (Assets\Fonts), served read-only so the scene page can
            // @font-face the real type faces instead of falling back to system fonts. Precedent:
            // CargoWebView's nexus.hulls mapping.
            var fontsFolder = Path.Combine(AppContext.BaseDirectory, "Assets", "Fonts");
            core.SetVirtualHostNameToFolderMapping("nexus.fonts", fontsFolder, CoreWebView2HostResourceAccessKind.Allow);
            core.Settings.AreDefaultContextMenusEnabled = false;
            core.Settings.IsStatusBarEnabled = false;
            core.Settings.AreDevToolsEnabled = false;
            // Defense-in-depth: the scene is fully local (our own virtual host). Cancel any navigation
            // that leaves it and deny every new-window request, so a crafted page cannot steer the
            // WebView2 to an external or unsafe target.
            core.NavigationStarting += OnNavigationStarting;
            core.NewWindowRequested += OnNewWindowRequested;
            core.NavigationCompleted += (_, _) =>
            {
                _ready = true;
                if (_pending != null) { core.PostWebMessageAsJson(_pending); _pending = null; }
            };
            core.WebMessageReceived += OnWebMessage;
            core.Navigate("https://nexus.map/index.html");
        }
        catch (Exception ex)
        {
            // WebView2 runtime missing, or the shared user-data folder is locked/corrupt. Do not let an
            // async-void Loaded handler take the whole app down; show a fallback and let a later view retry.
            Logger.Error("[WIN] map view failed to start; WebView2 could not initialize", ex);
            WebViewEnv.Reset();
            Content = new System.Windows.Controls.TextBlock
            {
                Text = "Map unavailable. The WebView2 runtime could not be started. See nexus.log.",
                Foreground = System.Windows.Media.Brushes.Gainsboro,
                TextWrapping = System.Windows.TextWrapping.Wrap,
                Margin = new System.Windows.Thickness(24),
                HorizontalAlignment = System.Windows.HorizontalAlignment.Center,
                VerticalAlignment = System.Windows.VerticalAlignment.Center,
            };
        }
    }

    // Called once, right before the portable self-swap flips files: disposing the control ends the
    // msedgewebview2 child process so its read handles on Web\map release. Deliberately no re-init
    // path; after a FAILED swap the map view stays degraded until the app restarts, which beats
    // renaming files under a live embedded browser. Mirrors CargoWebView.ShutdownForUpdate.
    internal void ShutdownForUpdate()
    {
        try { _web.Dispose(); }
        catch (Exception ex) { Logger.Error("[WIN] map view dispose before update failed", ex); }
        WebViewEnv.Reset();
        _ready = false;
    }

    // Cancel any navigation off our own local virtual host. Fires for every navigation attempt
    // (page-initiated or otherwise); denials are logged once per occurrence.
    private static void OnNavigationStarting(object? sender, CoreWebView2NavigationStartingEventArgs e)
    {
        if (IsAllowedNavigation(e.Uri)) return;
        e.Cancel = true;
        Logger.Info($"[WIN] map view blocked navigation to {TextSanitizer.ForLog(e.Uri)}");
    }

    // Deny every new-window / popup request outright; the map scene never opens a second window.
    private static void OnNewWindowRequested(object? sender, CoreWebView2NewWindowRequestedEventArgs e)
    {
        e.Handled = true;
        Logger.Info($"[WIN] map view blocked new-window request to {TextSanitizer.ForLog(e.Uri)}");
    }

    // Pure allow decision for a WebView2 navigation target. Only the app's own local https virtual
    // hosts (nexus.map and nexus.fonts, both mapped in InitAsync) and the implicit initial about:blank
    // are permitted; everything else (http downgrade, file:, javascript:, data:, external https, other
    // hosts including nexus.cargo) is denied. Kept internal + static so it is unit-testable without
    // spinning up WebView2.
    internal static bool IsAllowedNavigation(string? uri)
    {
        if (string.IsNullOrEmpty(uri)) return false;
        // WebView2 may raise the implicit blank document before we navigate to our host.
        if (string.Equals(uri, "about:blank", StringComparison.OrdinalIgnoreCase)) return true;
        if (!Uri.TryCreate(uri, UriKind.Absolute, out var u)) return false;
        // Scheme must be exactly https (blocks http downgrade, file:, javascript:, data:, etc.); host
        // must be one of our own virtual hosts. Uri lowercases scheme/host already; the comparers are
        // belt-and-suspenders.
        if (!string.Equals(u.Scheme, "https", StringComparison.OrdinalIgnoreCase)) return false;
        return string.Equals(u.Host, "nexus.map", StringComparison.OrdinalIgnoreCase)
            || string.Equals(u.Host, "nexus.fonts", StringComparison.OrdinalIgnoreCase);
    }

    // Push one JSON payload (init/update) to the scene. Queues until the page has navigated.
    public void PostJson(string json)
    {
        if (_ready && _web.CoreWebView2 != null) _web.CoreWebView2.PostWebMessageAsJson(json);
        else _pending = json;
    }

    // Inbound scene messages (the page posts a JSON string): ready, pin selection, measure result,
    // and a scene-side log passthrough. Parsing is delegated to the pure ParseMessage so the
    // parse-and-route decision is unit-testable without a live WebView2 instance.
    private void OnWebMessage(object? sender, CoreWebView2WebMessageReceivedEventArgs e)
    {
        try
        {
            var raw = e.TryGetWebMessageAsString();
            if (string.IsNullOrEmpty(raw)) return;
            var msg = ParseMessage(raw);
            if (msg == null) return;

            switch (msg.Type)
            {
                case "ready":
                    Ready?.Invoke();
                    break;
                case "pinClick":
                    if (msg.Id.HasValue) PinClicked?.Invoke(msg.Id.Value);
                    break;
                case "pinDoubleClick":
                    if (msg.Id.HasValue) PinDoubleClicked?.Invoke(msg.Id.Value);
                    break;
                case "measureResult":
                    if (msg.A.HasValue && msg.B.HasValue) MeasurePicked?.Invoke(msg.A.Value, msg.B.Value);
                    break;
                case "log":     // surface a page-side error/warning into nexus.log (App Log Monitor)
                    if (msg.Msg != null) Logger.Info($"[UI] map scene: {msg.Msg}");
                    break;
            }
        }
        catch (Exception ex) { Logger.Error("[WIN] map view message handling failed", ex); }
    }

    // One parsed inbound scene message: Type is always set on a non-null result; the other fields
    // are populated per message type (see ParseMessage) and null otherwise.
    internal sealed record MapWebMessage(string Type, int? Id = null, int? A = null, int? B = null, string? Msg = null);

    // Pure parse-and-route decision for one inbound JS message string. Never throws: malformed JSON,
    // an unknown "type", or a message missing/mistyping the fields its type requires all resolve to
    // null rather than raising. Kept internal + static so it is unit-testable without a WebView2
    // instance. Recognized shapes (Web/map/index.html, per the task-5 scene bridge):
    //   {type:"ready"}
    //   {type:"pinClick", id}
    //   {type:"pinDoubleClick", id}
    //   {type:"measureResult", a, b}
    //   {type:"log", msg}
    internal static MapWebMessage? ParseMessage(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return null;
            if (!root.TryGetProperty("type", out var t) || t.ValueKind != JsonValueKind.String) return null;

            switch (t.GetString())
            {
                case "ready":
                    return new MapWebMessage("ready");
                case "pinClick":
                    return root.TryGetProperty("id", out var id1) && id1.ValueKind == JsonValueKind.Number
                        ? new MapWebMessage("pinClick", Id: id1.GetInt32())
                        : null;
                case "pinDoubleClick":
                    return root.TryGetProperty("id", out var id2) && id2.ValueKind == JsonValueKind.Number
                        ? new MapWebMessage("pinDoubleClick", Id: id2.GetInt32())
                        : null;
                case "measureResult":
                    if (root.TryGetProperty("a", out var a) && a.ValueKind == JsonValueKind.Number &&
                        root.TryGetProperty("b", out var b) && b.ValueKind == JsonValueKind.Number)
                        return new MapWebMessage("measureResult", A: a.GetInt32(), B: b.GetInt32());
                    return null;
                case "log":
                    return root.TryGetProperty("msg", out var m) && m.ValueKind == JsonValueKind.String
                        ? new MapWebMessage("log", Msg: m.GetString())
                        : null;
                default:
                    return null;
            }
        }
        catch (Exception)
        {
            // Malformed JSON, or a well-formed-but-wrong-shaped number (e.g. GetInt32 on a non-integer)
            // - either way this is untrusted page-side input, so it resolves to null, never throws.
            return null;
        }
    }
}
