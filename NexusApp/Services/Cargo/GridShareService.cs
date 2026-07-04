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

    private static readonly JsonSerializerOptions Opts = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
    };

    public static void Export(GridSharePackage package, string path) =>
        File.WriteAllText(path, JsonSerializer.Serialize(package, Opts));

    public static GridSharePackage Import(string path)
    {
        GridSharePackage? pkg;
        try { pkg = JsonSerializer.Deserialize<GridSharePackage>(File.ReadAllText(path), Opts); }
        catch (JsonException ex) { throw new GridShareException($"Not a valid .nexusgrid file: {ex.Message}"); }

        if (pkg == null) throw new GridShareException("Empty or unreadable .nexusgrid file.");
        if (pkg.Format != FormatV1) throw new GridShareException($"Unknown file format '{pkg.Format}'.");
        if (string.IsNullOrWhiteSpace(pkg.ShipId)) throw new GridShareException("File is missing a ship id.");
        if (pkg.Grids is null || pkg.Grids.Count == 0) throw new GridShareException("File has no cargo grids.");
        return pkg;
    }

    // Convert a ship's effective grids (GridDef) to the override/share shape.
    public static List<GridOverride> ToOverrides(IReadOnlyList<GridDef> grids) =>
        grids.Select(g => new GridOverride
        {
            Id = g.Id, W = g.W, D = g.D, H = g.H,
            Cap = g.MaxContainerScu,
            Accepts = g.AcceptedCaps.ToList(),
            Px = g.PosX ?? 0, Py = g.PosY ?? 0, Pz = g.PosZ ?? 0,
            Wy = g.WAlongShipY,
        }).ToList();
}
