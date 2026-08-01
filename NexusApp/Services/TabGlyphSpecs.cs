using System.Text.Json;
using NexusApp.Views;

namespace NexusApp.Services;

/// <summary>One drawable stroke part of a tab glyph: an SVG-like element name plus its attributes,
/// both kept as strings so this layer stays WPF-free and headlessly testable.</summary>
public sealed record GlyphPart(string El, IReadOnlyDictionary<string, string> Attrs);

/// <summary>
/// Glyph source for the overlay tab strip. Tabs reuse the app's dock icons (owner ruling
/// 2026-07-27: overlay tabs match their related pages), read from the same DockIconSpecs JSON the
/// dock renders, so a regenerated dock pick can never drift from the overlay. Core parts only:
/// flourish parts (ids prefixed "f_") and animation tracks are dock-scale detail, unreadable at
/// 15px. Tabs with no app page (shopping) carry hand-authored glyphs at the same stroke weight,
/// mirroring the approved mock's ICONS map exactly.
///
/// <para>"trade" used to be hand-authored too - a trending-up arrow, written when the tab was
/// reserved for a commodity page that did not exist yet. That page shipped with its own dock icon
/// (the balance scale in DockIconSpecsCustom), so from 2026-08-01 the entry is gone and the tab
/// resolves through the dock like every other page-backed tab: the owner's report was simply that the
/// overlay's icon did not match the app's. The scale's core parts sit inside x 1.8-22.2, y 4-20.5,
/// so the default 24x24 crop frames them without clipping, and Geometry.Parse handles its two arc
/// segments.</para>
/// </summary>
public static class TabGlyphSpecs
{
    // tab id -> dock spec key. Missing key = hand-authored glyph below.
    private static readonly Dictionary<string, string> DockKeyByTab = new()
    {
        ["stats"] = "operations",
        ["scan"] = "rs",
        ["orders"] = "refinery",
        ["hauling"] = "cargo",
        ["guides"] = "guides",
    };

    // Per-tab crop, chosen in the mock so every glyph fills its 15px frame optically. The dock
    // specs pad their viewbox for flourishes and hover growth; rendering that padding at 15px
    // would shrink the core glyph to ~9px.
    public static (double X, double Y, double W, double H) CropFor(string tabId) => tabId switch
    {
        "scan" => (6, 6, 20, 20),
        "guides" => (2, 2, 20, 20),
        _ => (0, 0, 24, 24),
    };

    // Dock stroke weights: rs is authored at 1.6, everything else (including the hand glyphs) 1.5.
    public static double StrokeFor(string tabId) => tabId == "scan" ? 1.6 : 1.5;

    private static readonly Dictionary<string, IReadOnlyList<GlyphPart>> HandGlyphs = new()
    {
        // Cart (the shopping list has no dedicated app page; the cart lives inside the decoder).
        ["shopping"] = new List<GlyphPart>
        {
            new("circle", new Dictionary<string, string> { ["cx"] = "8", ["cy"] = "21", ["r"] = "1" }),
            new("circle", new Dictionary<string, string> { ["cx"] = "19", ["cy"] = "21", ["r"] = "1" }),
            new("path", new Dictionary<string, string>
            {
                ["d"] = "M2.05 2.05h2l2.66 12.42a2 2 0 0 0 2 1.58h9.78a2 2 0 0 0 1.95-1.57l1.65-7.43H5.12",
            }),
        },
    };

    private static readonly Dictionary<string, IReadOnlyList<GlyphPart>> Cache = new();
    private static readonly object Gate = new();

    public static IReadOnlyList<GlyphPart> PartsFor(string tabId)
    {
        lock (Gate)
        {
            if (Cache.TryGetValue(tabId, out var hit)) return hit;
            var parts = HandGlyphs.TryGetValue(tabId, out var hand)
                ? hand
                : ParseDockParts(DockKeyByTab.TryGetValue(tabId, out var key) ? key : tabId);
            Cache[tabId] = parts;
            return parts;
        }
    }

    // Reads the dock spec's parts array, keeping core parts (id not f_-prefixed) and only the
    // geometry attributes. Numeric attribute values appear both as JSON numbers and as strings in
    // the generated specs; both forms normalize to invariant strings here.
    private static IReadOnlyList<GlyphPart> ParseDockParts(string dockKey)
    {
        string[] geomAttrs = ["d", "points", "x1", "y1", "x2", "y2", "cx", "cy", "r"];
        foreach (var json in new[] { DockIconSpecs.Json, DockIconSpecsCustom.Json })
        {
            using var doc = JsonDocument.Parse(json);
            if (!doc.RootElement.TryGetProperty(dockKey, out var spec)) continue;
            var parts = new List<GlyphPart>();
            foreach (var part in spec.GetProperty("parts").EnumerateArray())
            {
                var id = part.GetProperty("id").GetString() ?? "";
                if (id.StartsWith("f_", StringComparison.Ordinal)) continue;
                var el = part.GetProperty("el").GetString() ?? "";
                var attrs = new Dictionary<string, string>();
                foreach (var name in geomAttrs)
                    if (part.TryGetProperty(name, out var v))
                        attrs[name] = v.ValueKind == JsonValueKind.String ? v.GetString()! : v.GetRawText();
                parts.Add(new GlyphPart(el, attrs));
            }
            return parts;
        }
        return [];
    }
}
