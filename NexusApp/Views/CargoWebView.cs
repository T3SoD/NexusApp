using System.IO;
using System.Text.Json;
using System.Threading.Tasks;
using System.Windows.Controls;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;
using NexusApp.Models.Cargo;
using NexusApp.Services;
using NexusApp.Services.Cargo;

namespace NexusApp.Views;

// Hosts the cargo grid 3D scene (Web/cargo/index.html, Three.js) in a WebView2 so the view matches the
// approved mockup 1:1. C# stays the source of truth: it packs the cargo and posts the packed result as
// JSON; the page renders it. Fully offline - Three.js is vendored locally and served through a virtual
// host mapping, so no network is ever touched.
public sealed class CargoWebView : UserControl
{
    private const int GapCells = 2;   // spacing between a ship's grids in the synthetic layout (matches the packer view)

    // Hull outline buffers are local-only: never bundled with the app, loaded from the user's
    // machine when present. Ships without a buffer render exactly as before.
    private static readonly string HullsDir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "NexusApp", "hulls");
    private static string? _lastHullLog;

    // Variants that share a physical hull (they differ only in cargo grids) point at one outline.
    // Ironclad Assault is the same Drake Ironclad airframe as the Ironclad, so it reuses its hologram.
    private static readonly Dictionary<string, string> HullAlias = new()
    {
        ["drak-ironclad-assault"] = "drak-ironclad",
    };

    private readonly WebView2 _web = new();
    private bool _ready;
    private string? _pending;
    private bool _editMode;
    private int _editRev;   // bumped on save/revert so the page re-seeds its working copy

    // Edit Layout mode: when on, the payload carries the raw editable grid list and the page
    // renders the interactive editor. The planner page toggles this and owns persistence.
    public bool EditMode
    {
        get => _editMode;
        set => _editMode = value;
    }

    // Grid Studio mode: renders grid volumes orange (to read against the hologram) and numbers
    // every grid, even outside edit mode. The shippable planner leaves this off (approved cyan,
    // no numbers). Set once per viewport.
    private bool _studio;
    public bool Studio
    {
        get => _studio;
        set => _studio = value;
    }

    // Which grid is highlighted (pink) for test-fill selection; -1 for none. Only meaningful in
    // Studio mode. Raised back to the page when the user clicks a grid in the 3D view.
    public int TestSelectedGrid { get; set; } = -1;
    public event Action<int>? TestGridClicked;

    // Raised when the editor posts a saved / reverted grid set. The planner persists via the
    // override store and reloads the catalog; this view stays a dumb renderer.
    public event Action<string, List<GridOverride>>? GridsSaved;
    public event Action<string>? GridsReverted;

    // The planner calls this after applying a save/revert so the next render tells the page to
    // re-seed its working copy from the reloaded catalog (a revert must discard in-page edits).
    public void BumpEditRev() => _editRev++;

    public CargoWebView()
    {
        _web.DefaultBackgroundColor = System.Drawing.Color.FromArgb(0xFF, 0x07, 0x0B, 0x11);
        Content = _web;
        Loaded += async (_, _) => await InitAsync();
    }

    // One shared environment for every CargoWebView in the process. A second environment on the
    // same user-data folder throws ("folder in use"), so both the planner and Grid Studio
    // viewports must share this one.
    private static Task<CoreWebView2Environment>? _sharedEnv;
    private static Task<CoreWebView2Environment> SharedEnvAsync()
    {
        if (_sharedEnv == null)
        {
            // Keep the WebView2 profile in a writable per-user folder (the exe may live under Program Files).
            var dataFolder = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "NexusApp", "WebView2");
            Directory.CreateDirectory(dataFolder);
            _sharedEnv = CoreWebView2Environment.CreateAsync(null, dataFolder, null);
        }
        return _sharedEnv;
    }

    private async Task InitAsync()
    {
        if (_web.CoreWebView2 != null) return;

        await _web.EnsureCoreWebView2Async(await SharedEnvAsync());

        var core = _web.CoreWebView2!;
        var siteFolder = Path.Combine(AppContext.BaseDirectory, "Web", "cargo");
        core.SetVirtualHostNameToFolderMapping("nexus.cargo", siteFolder, CoreWebView2HostResourceAccessKind.Allow);
        Directory.CreateDirectory(HullsDir);
        core.SetVirtualHostNameToFolderMapping("nexus.hulls", HullsDir, CoreWebView2HostResourceAccessKind.Allow);
        Logger.Info($"[UI] cargo hologram: hulls folder mapped, {Directory.GetFiles(HullsDir, "*.bin").Length} outline file(s) present");
        core.Settings.AreDefaultContextMenusEnabled = false;
        core.Settings.IsStatusBarEnabled = false;
        core.Settings.AreDevToolsEnabled = true;   // dev trial; gate off before any release
        core.NavigationCompleted += (_, _) =>
        {
            _ready = true;
            if (_pending != null) { core.PostWebMessageAsJson(_pending); _pending = null; }
        };
        core.WebMessageReceived += OnWebMessage;
        core.Navigate("https://nexus.cargo/index.html");
    }

    // Push one packed trip to the scene. Queues until the page has loaded.
    public void RenderTrip(PackResult? trip, ShipCargoDef? ship)
    {
        var json = BuildPayload(trip, ship, _editMode, _editRev, _studio, TestSelectedGrid);
        if (_ready && _web.CoreWebView2 != null) _web.CoreWebView2.PostWebMessageAsJson(json);
        else _pending = json;
    }

    // Inbound editor messages (the page posts a JSON string): save or revert a ship's grids.
    private void OnWebMessage(object? sender, CoreWebView2WebMessageReceivedEventArgs e)
    {
        try
        {
            var raw = e.TryGetWebMessageAsString();
            if (string.IsNullOrEmpty(raw)) return;
            using var doc = JsonDocument.Parse(raw);
            var root = doc.RootElement;
            if (!root.TryGetProperty("type", out var t)) return;
            var shipId = root.TryGetProperty("shipId", out var sid) ? sid.GetString() ?? "" : "";

            switch (t.GetString())
            {
                case "saveGrids":
                    if (!string.IsNullOrEmpty(shipId) && root.TryGetProperty("grids", out var g) && g.ValueKind == JsonValueKind.Array)
                        GridsSaved?.Invoke(shipId, ParseGrids(g));
                    break;
                case "revertGrids":
                    if (!string.IsNullOrEmpty(shipId)) GridsReverted?.Invoke(shipId);
                    break;
                case "testGridSelected":
                    if (root.TryGetProperty("index", out var gi) && gi.ValueKind == JsonValueKind.Number)
                        TestGridClicked?.Invoke(gi.GetInt32());
                    break;
            }
        }
        catch (Exception ex) { Logger.Error("cargo editor message parse failed", ex); }
    }

    private static List<GridOverride> ParseGrids(JsonElement arr)
    {
        var list = new List<GridOverride>();
        foreach (var g in arr.EnumerateArray())
            list.Add(new GridOverride
            {
                Id = Int(g, "id"), W = Int(g, "w"), D = Int(g, "d"), H = Int(g, "h"), Cap = Int(g, "cap"),
                Accepts = IntArray(g, "accepts"),
                Px = Dbl(g, "px"), Py = Dbl(g, "py"), Pz = Dbl(g, "pz"),
                Wy = g.TryGetProperty("wy", out var wy) && wy.ValueKind == JsonValueKind.True,
            });
        return list;
    }

    private static int Int(JsonElement o, string k) =>
        o.TryGetProperty(k, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetInt32() : 0;

    private static double Dbl(JsonElement o, string k) =>
        o.TryGetProperty(k, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetDouble() : 0;

    private static List<int>? IntArray(JsonElement o, string k)
    {
        if (!o.TryGetProperty(k, out var v) || v.ValueKind != JsonValueKind.Array) return null;
        var list = new List<int>();
        foreach (var e in v.EnumerateArray())
            if (e.ValueKind == JsonValueKind.Number) list.Add(e.GetInt32());
        return list.Count > 0 ? list : null;
    }

    // Cell coords in the payload: x = lateral, y = depth (fore-aft), z = vertical. Each grid is sent
    // as its own volume (min corner + size). When the catalog carries datamined ship-space positions
    // for every grid, the layout matches the real hull arrangement; otherwise grids fall back to the
    // old synthetic side-by-side strip. Boxes are absolute in the same frame.
    private sealed record GridFrame(double X, double Y, double Z, int SizeX, int SizeY, int H, bool Wy);

    private static string BuildPayload(PackResult? trip, ShipCargoDef? ship, bool editMode, int editRev,
        bool studio, int testSel)
    {
        var frames = new Dictionary<int, GridFrame>();
        double[]? origin = null;   // ship-space min corner (cells); anchors the hull hologram
        if (ship != null)
        {
            if (ship.Grids.Count > 0 && ship.Grids.All(g => g.HasPos))
            {
                // Real layout: grid centers from hull geometry; W may run fore-aft (Wy).
                foreach (var g in ship.Grids)
                {
                    int sx = g.WAlongShipY ? g.D : g.W;
                    int sy = g.WAlongShipY ? g.W : g.D;
                    frames[g.Id] = new GridFrame(
                        g.PosX!.Value - sx / 2.0, g.PosY!.Value - sy / 2.0,
                        Math.Round(g.PosZ!.Value - g.H / 2.0), sx, sy, g.H, g.WAlongShipY);
                }
                // Normalize so the layout's min corner sits at the origin.
                double minX = frames.Values.Min(f => f.X);
                double minY = frames.Values.Min(f => f.Y);
                double minZ = frames.Values.Min(f => f.Z);
                foreach (var (id, f) in frames.ToList())
                    frames[id] = f with { X = Math.Round(f.X - minX, 2), Y = Math.Round(f.Y - minY, 2), Z = f.Z - minZ };
                origin = new[] { Math.Round(minX, 2), Math.Round(minY, 2), minZ };
            }
            else
            {
                int cursor = 0;
                foreach (var g in ship.Grids)
                {
                    frames[g.Id] = new GridFrame(cursor, 0, 0, g.W, g.D, g.H, false);
                    cursor += g.W + GapCells;
                }
            }
        }

        var grids = frames.Values
            .Select(f => new { x = f.X, y = f.Y, z = f.Z, w = f.SizeX, d = f.SizeY, h = f.H })
            .ToList();
        double boundsW = grids.Count > 0 ? grids.Max(g => g.x + g.w) : 8;
        double boundsD = grids.Count > 0 ? grids.Max(g => g.y + g.d) : 4;
        double boundsH = grids.Count > 0 ? grids.Max(g => g.z + g.h) : 4;

        var boxes = new List<object>();
        if (trip != null)
            foreach (var p in trip.Placed)
            {
                var f = frames.GetValueOrDefault(p.GridId) ?? new GridFrame(0, 0, 0, 0, 0, 0, false);
                boxes.Add(new
                {
                    scu = p.Scu,
                    x = f.X + (f.Wy ? p.Y : p.X),
                    y = f.Y + (f.Wy ? p.X : p.Y),
                    z = f.Z + p.Z,
                    w = f.Wy ? p.Size.D : p.Size.W,
                    d = f.Wy ? p.Size.W : p.Size.D,
                    h = p.Size.H,
                });
            }

        // Editor round-trip: the raw editable grid list in ship-space cells (grid CENTER at
        // px/py/pz, which may be null for a schematic ship), each grid carrying its own max
        // container cap so it can be set per grid. The page renders the editor from this when on.
        object? edit = null;
        if (ship != null)
            edit = new
            {
                on = editMode,
                rev = editRev,
                shipId = ship.Id,
                grids = ship.Grids.Select(g => new
                {
                    id = g.Id, w = g.W, d = g.D, h = g.H, cap = g.MaxContainerScu,
                    accepts = g.AcceptedCaps,
                    px = g.PosX, py = g.PosY, pz = g.PosZ, wy = g.WAlongShipY,
                }).ToList(),
            };

        int cap = ship?.TotalScu ?? 0;
        int scu = trip?.PlacedScu ?? 0;
        int count = trip?.Placed.Count ?? 0;
        int pct = cap > 0 ? (int)Math.Round(scu / (double)cap * 100) : 0;

        // Hull hologram: only for real layouts, and only when the user's machine has the
        // outline buffer. Nothing here is bundled or downloaded.
        string? hull = null;
        if (ship != null && origin != null)
        {
            var hullId = HullAlias.TryGetValue(ship.Id, out var alias) ? alias : ship.Id;
            var file = hullId + ".bin";
            if (File.Exists(Path.Combine(HullsDir, file)))
            {
                hull = file;
                if (_lastHullLog != file)
                {
                    _lastHullLog = file;
                    Logger.Info($"[UI] cargo hologram: local hull outline active for {ship.DisplayName}");
                }
            }
        }

        var payload = new
        {
            cell = 1.25,
            ship = new { name = ship?.DisplayName ?? "", totalScu = cap },
            bounds = new { w = boundsW, d = boundsD, h = boundsH },
            origin,
            hull,
            edit,
            studio,
            testSel,
            grids,
            boxes,
            stats = new { pct, scu, cap, boxes = count },
        };
        return JsonSerializer.Serialize(payload);
    }
}
