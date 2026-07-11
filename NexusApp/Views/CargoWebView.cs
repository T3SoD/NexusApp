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
    private int _editRev;   // bumped on save/revert so the page re-seeds its working copy
    private string? _lastShipId;

    // How the next render presents the scene. Edit renders the interactive layout editor; View renders
    // read-only grids/boxes (test fill and import preview). Passed explicitly per render rather than via
    // a mutable flag, so every call site states its intent and there is no order-dependent protocol.
    public enum CargoViewMode { View, Edit }

    // True while the in-page editor holds unsaved edits. Set from the page's dirty message, cleared on
    // save/revert and when the rendered ship changes. Grid Studio blocks export while dirty so a
    // contributor never shares a stale saved layout instead of what is on screen.
    public bool HasUnsavedEdits { get; private set; }

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

    // Grid Studio calls this after applying a save/revert/import so the next render tells the page to
    // re-seed its working copy from the reloaded catalog (which discards any in-page edits). Since the
    // page edits are dropped, the unsaved-edits flag is cleared here too.
    public void BumpEditRev() { _editRev++; HasUnsavedEdits = false; }

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

        try
        {
            await _web.EnsureCoreWebView2Async(await SharedEnvAsync());

            var core = _web.CoreWebView2!;
            var siteFolder = Path.Combine(AppContext.BaseDirectory, "Web", "cargo");
            core.SetVirtualHostNameToFolderMapping("nexus.cargo", siteFolder, CoreWebView2HostResourceAccessKind.Allow);
            Directory.CreateDirectory(HullsDir);
            core.SetVirtualHostNameToFolderMapping("nexus.hulls", HullsDir, CoreWebView2HostResourceAccessKind.Allow);
            Logger.Info($"[UI] cargo hologram: hulls folder mapped, {Directory.GetFiles(HullsDir, "*.bin").Length} outline file(s) present");
            core.Settings.AreDefaultContextMenusEnabled = false;
            core.Settings.IsStatusBarEnabled = false;
            core.Settings.AreDevToolsEnabled = _studio;   // dev tooling only; the shippable planner gets none
            core.NavigationCompleted += (_, _) =>
            {
                _ready = true;
                if (_pending != null) { core.PostWebMessageAsJson(_pending); _pending = null; }
            };
            core.WebMessageReceived += OnWebMessage;
            core.Navigate("https://nexus.cargo/index.html");
        }
        catch (Exception ex)
        {
            // WebView2 runtime missing, or the shared user-data folder is locked/corrupt. Do not let an
            // async-void Loaded handler take the whole app down; show a fallback and let a later view retry.
            Logger.Error("[UI] cargo 3D view failed to start; WebView2 could not initialize", ex);
            _sharedEnv = null;
            Content = new System.Windows.Controls.TextBlock
            {
                Text = "3D view unavailable. The WebView2 runtime could not be started. See nexus.log.",
                Foreground = System.Windows.Media.Brushes.Gainsboro,
                TextWrapping = System.Windows.TextWrapping.Wrap,
                Margin = new System.Windows.Thickness(24),
                HorizontalAlignment = System.Windows.HorizontalAlignment.Center,
                VerticalAlignment = System.Windows.VerticalAlignment.Center,
            };
        }
    }

    // Push one packed trip to the scene in the given mode. Queues until the page has loaded.
    public void RenderTrip(PackResult? trip, ShipCargoDef? ship, CargoViewMode mode = CargoViewMode.View)
    {
        if (ship?.Id != _lastShipId) { HasUnsavedEdits = false; _lastShipId = ship?.Id; }
        var json = BuildPayload(trip, ship, mode == CargoViewMode.Edit, _editRev, _studio, TestSelectedGrid);
        if (_ready && _web.CoreWebView2 != null) _web.CoreWebView2.PostWebMessageAsJson(json);
        else _pending = json;
    }

    // Acknowledge a saveGrids submission the host tried to persist. On success the host calls BumpEditRev
    // (which re-seeds the page and clears HasUnsavedEdits); this only needs to handle failure: tell the
    // page the save was rejected so the editor surfaces it and stays dirty. The on-screen edits are
    // untouched, so the user can fix and retry.
    public void AckSave(bool ok, string? message = null)
    {
        if (ok) return;
        var json = JsonSerializer.Serialize(new { type = "saveResult", ok = false, message = message ?? "Save failed - not saved." });
        if (_ready && _web.CoreWebView2 != null) _web.CoreWebView2.PostWebMessageAsJson(json);
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
                    // Do NOT clear HasUnsavedEdits here: the host may reject the save (validation or a
                    // failed disk write). The dirty flag is cleared only on a confirmed save, via
                    // BumpEditRev; a rejected save calls AckSave(false) and the edits stay on screen.
                    if (!string.IsNullOrEmpty(shipId) && root.TryGetProperty("grids", out var g) && g.ValueKind == JsonValueKind.Array)
                        GridsSaved?.Invoke(shipId, ParseGrids(g));
                    break;
                case "revertGrids":
                    if (!string.IsNullOrEmpty(shipId)) { HasUnsavedEdits = false; GridsReverted?.Invoke(shipId); }
                    break;
                case "dirty":   // the editor changed a grid; unsaved edits now exist
                    HasUnsavedEdits = true;
                    break;
                case "log":     // surface a page-side error/warning into nexus.log (App Log Monitor)
                    if (root.TryGetProperty("msg", out var lm) && lm.ValueKind == JsonValueKind.String)
                        Logger.Info($"[UI] cargo view: {lm.GetString()}");
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
                Rot = DblArray(g, "rot"),   // preserved across the edit round-trip (validated on apply)
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

    private static List<double>? DblArray(JsonElement o, string k)
    {
        if (!o.TryGetProperty(k, out var v) || v.ValueKind != JsonValueKind.Array) return null;
        var list = new List<double>();
        foreach (var e in v.EnumerateArray())
            if (e.ValueKind == JsonValueKind.Number) list.Add(e.GetDouble());
        return list.Count > 0 ? list : null;
    }

    // Cell coords in the payload: x = lateral, y = depth (fore-aft), z = vertical. Each grid is sent
    // as its own volume (min corner + size). When the catalog carries datamined ship-space positions
    // for every grid, the layout matches the real hull arrangement; otherwise grids fall back to the
    // old synthetic side-by-side strip. Boxes are absolute in the same frame.
    //
    // X/Y/Z is the grid's axis-aligned bounding-box (AABB) min corner; Ax/Ay/Az is its AABB full
    // extent. For an axis-aligned grid the AABB equals its footprint (Ax = SizeX, Ay = SizeY, Az = H);
    // for a tilted grid (Rot set) the AABB is the enclosing box of the rotated volume, so bounds,
    // centering, and the hull anchor stay correct. Rot is the orientation quaternion (x,y,z,w) in the
    // renderer's box-local frame, or null for the common axis-aligned case.
    internal sealed record GridFrame(double X, double Y, double Z, int SizeX, int SizeY, int H, bool Wy,
        double[]? Rot = null, double Ax = 0, double Ay = 0, double Az = 0);

    // Payload-space AABB full-extent (x, y, z cells) of a box with dims (w, h, d) along its local
    // X/Y/Z, oriented by a scene-frame quaternion (x, y, z, w). The renderer maps payload (x, y, z)
    // to scene (x, z, y), so the scene AABB's Y and Z swap back to payload. Kept internal so the
    // layout math is unit-testable against the datamine's own AABB.
    internal static (double X, double Y, double Z) RotatedAabb(IReadOnlyList<double> q, int w, int h, int d)
    {
        double x = q[0], y = q[1], z = q[2], ww = q[3];
        double m00 = 1 - 2 * (y * y + z * z), m01 = 2 * (x * y - z * ww), m02 = 2 * (x * z + y * ww);
        double m10 = 2 * (x * y + z * ww),    m11 = 1 - 2 * (x * x + z * z), m12 = 2 * (y * z - x * ww);
        double m20 = 2 * (x * z - y * ww),    m21 = 2 * (y * z + x * ww),    m22 = 1 - 2 * (x * x + y * y);
        double hw = w / 2.0, hh = h / 2.0, hd = d / 2.0;
        double sx = Math.Abs(m00) * hw + Math.Abs(m01) * hh + Math.Abs(m02) * hd;
        double sy = Math.Abs(m10) * hw + Math.Abs(m11) * hh + Math.Abs(m12) * hd;
        double sz = Math.Abs(m20) * hw + Math.Abs(m21) * hh + Math.Abs(m22) * hd;
        return (2 * sx, 2 * sz, 2 * sy);   // scene (x,y,z) -> payload (x, z, y)
    }

    // Lay a ship's grids into scene cells: each grid becomes a min-corner + size frame. Positioned
    // ships (every grid HasPos) use the datamined centers normalized so the layout's min corner sits at
    // the origin (returned so the hull hologram can anchor to it); Z is rounded to 2dp so the view
    // frames match the edit scene, which uses the raw pz. Schematic ships fall back to a synthetic
    // side-by-side strip with a null origin. Pure (no I/O) so the origin math is unit-testable.
    internal static (Dictionary<int, GridFrame> Frames, double[]? Origin) ComputeLayout(ShipCargoDef? ship)
    {
        var frames = new Dictionary<int, GridFrame>();
        double[]? origin = null;   // ship-space min corner (cells); anchors the hull hologram
        if (ship == null) return (frames, origin);

        if (ship.Grids.Count > 0 && ship.Grids.All(g => g.HasPos))
        {
            // Real layout: grid centers from hull geometry. An axis-aligned grid's footprint may run
            // fore-aft (Wy); a tilted grid (Rot) keeps its raw dims and rotates, and its AABB is the
            // enclosing box of the rotated volume so the hull anchor and centering stay right.
            foreach (var g in ship.Grids)
            {
                int sx, sy;
                double ax, ay, az;
                if (g.HasRot)
                {
                    sx = g.W; sy = g.D;                                   // raw dims; the renderer rotates
                    (ax, ay, az) = RotatedAabb(g.Rot!, g.W, g.H, g.D);   // enclosing AABB extent
                }
                else
                {
                    sx = g.WAlongShipY ? g.D : g.W;
                    sy = g.WAlongShipY ? g.W : g.D;
                    ax = sx; ay = sy; az = g.H;
                }
                frames[g.Id] = new GridFrame(
                    g.PosX!.Value - ax / 2.0, g.PosY!.Value - ay / 2.0,
                    Math.Round(g.PosZ!.Value - az / 2.0, 2), sx, sy, g.H, g.WAlongShipY,
                    g.HasRot ? g.Rot!.ToArray() : null, ax, ay, az);
            }
            // Normalize so the layout's min corner sits at the origin. Round Z the same way as X and
            // Y (2 decimals) so the view frames match the edit scene, which uses the raw pz.
            double minX = frames.Values.Min(f => f.X);
            double minY = frames.Values.Min(f => f.Y);
            double minZ = frames.Values.Min(f => f.Z);
            foreach (var (id, f) in frames.ToList())
                frames[id] = f with { X = Math.Round(f.X - minX, 2), Y = Math.Round(f.Y - minY, 2), Z = Math.Round(f.Z - minZ, 2) };
            origin = new[] { Math.Round(minX, 2), Math.Round(minY, 2), Math.Round(minZ, 2) };
        }
        else
        {
            int cursor = 0;
            foreach (var g in ship.Grids)
            {
                frames[g.Id] = new GridFrame(cursor, 0, 0, g.W, g.D, g.H, false, null, g.W, g.D, g.H);
                cursor += g.W + GapCells;
            }
        }
        return (frames, origin);
    }

    private static string BuildPayload(PackResult? trip, ShipCargoDef? ship, bool editMode, int editRev,
        bool studio, int testSel)
    {
        var (frames, origin) = ComputeLayout(ship);

        // Each grid carries its AABB min corner (x/y/z) + footprint (w/d/h) as before, plus its volume
        // center (cx/cy/cz) and orientation (rot). A tilted grid is drawn as a box of its raw dims,
        // centered and rotated by rot; axis-aligned grids leave rot null and render exactly as before.
        var grids = frames.Values
            .Select(f => new
            {
                x = f.X, y = f.Y, z = f.Z, w = f.SizeX, d = f.SizeY, h = f.H,
                cx = f.X + f.Ax / 2, cy = f.Y + f.Ay / 2, cz = f.Z + f.Az / 2,
                rot = f.Rot,
            })
            .ToList();
        double boundsW = frames.Count > 0 ? frames.Values.Max(f => f.X + f.Ax) : 8;
        double boundsD = frames.Count > 0 ? frames.Values.Max(f => f.Y + f.Ay) : 4;
        double boundsH = frames.Count > 0 ? frames.Values.Max(f => f.Z + f.Az) : 4;

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
                    rot = g.Rot,   // carried so an edit-save round-trips it and never silently un-tilts
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
            reduced = Motion.Reduced,   // page snaps the camera and shows boxes at full opacity when true
        };
        return JsonSerializer.Serialize(payload);
    }

    // Test seam for the payload's motion flag: builds a plain View-mode payload (no edit / studio /
    // test-fill), matching ComputeLayout's internal-for-tests precedent. Kept minimal on purpose.
    internal static string BuildPayloadForTest(PackResult? trip, ShipCargoDef? ship) =>
        BuildPayload(trip, ship, editMode: false, editRev: 0, studio: false, testSel: -1);
}
