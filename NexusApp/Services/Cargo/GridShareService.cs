using System.IO;
using System.Text.Json;
using NexusApp.Models.Cargo;

namespace NexusApp.Services.Cargo;

public sealed class GridShareException : Exception
{
    public GridShareException(string message) : base(message) { }
}

// Reads and writes .nexusgrid files (one ship's cargo-grid layout for crowdsourced fixes).
// Pure I/O plus structural validation; no UI. Grid geometry validation is deferred to
// CargoShipCatalog.BuildPreview so the canonical BuildGrid rules stay the single source of truth.
public static class GridShareService
{
    public const string FormatV1 = "nexus.cargo.grid.v1";
    public const string FileExtension = ".nexusgrid";
    public const int MaxFileBytes = 1_000_000;   // a real .nexusgrid is a few KB; anything larger is hostile
    public const int MaxGrids = 64;              // the largest real ship is far below this

    private static readonly JsonSerializerOptions Opts = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
    };

    public static void Export(GridSharePackage package, string path) =>
        File.WriteAllText(path, JsonSerializer.Serialize(package, Opts));

    // A .nexusgrid is untrusted third-party input. Guard the file size, grid count, and free-text
    // fields here so a hostile or hand-mangled file cannot exhaust memory or inject nexus.log lines;
    // grid geometry is validated downstream by BuildPreview/BuildGrid.
    public static GridSharePackage Import(string path)
    {
        var len = new FileInfo(path).Length;
        if (len > MaxFileBytes)
            throw new GridShareException($"File is too large ({len / 1024} KB); a .nexusgrid is only a few KB.");

        GridSharePackage? pkg;
        try { pkg = JsonSerializer.Deserialize<GridSharePackage>(File.ReadAllText(path), Opts); }
        catch (JsonException ex) { throw new GridShareException($"Not a valid .nexusgrid file: {ex.Message}"); }

        if (pkg == null) throw new GridShareException("Empty or unreadable .nexusgrid file.");
        if (pkg.Format != FormatV1) throw new GridShareException($"Unknown file format '{pkg.Format}'.");
        if (string.IsNullOrWhiteSpace(pkg.ShipId)) throw new GridShareException("File is missing a ship id.");
        if (pkg.Grids is null || pkg.Grids.Count == 0) throw new GridShareException("File has no cargo grids.");
        if (pkg.Grids.Count > MaxGrids)
            throw new GridShareException($"File has too many grids ({pkg.Grids.Count}); the limit is {MaxGrids}.");

        // TextSanitizer.Clean strips control/bidi/format chars and truncates, so a handle like
        // "pilot\r\n[SCAN] forged" cannot forge nexus.log lines and notes cannot be multi-MB.
        return pkg with
        {
            RsiHandle = TextSanitizer.Clean(pkg.RsiHandle, 64, false),
            ShipName = TextSanitizer.Clean(pkg.ShipName, 80, false),
            Summary = TextSanitizer.Clean(pkg.Summary, 200, false),
            Notes = TextSanitizer.Clean(pkg.Notes, 4000, true),
            FlagNote = TextSanitizer.Clean(pkg.FlagNote, 500, true),
        };
    }

    // Convert a ship's effective grids (GridDef) to the override/share shape. Positions round-trip as
    // null when the ship has no datamined position (never fabricate 0,0,0, which stacks every grid).
    public static List<GridOverride> ToOverrides(IReadOnlyList<GridDef> grids) =>
        grids.Select(g => new GridOverride
        {
            Id = g.Id, W = g.W, D = g.D, H = g.H,
            Cap = g.MaxContainerScu,
            Accepts = g.AcceptedCaps.ToList(),
            Px = g.PosX, Py = g.PosY, Pz = g.PosZ,
            Wy = g.WAlongShipY,
            Rot = g.Rot?.ToList(),   // preserve tilt through export / import / the import-review baseline
        }).ToList();
}
