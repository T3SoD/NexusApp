using System.Text.Json;
using System.Windows.Media;
using NexusApp.Views;
using Xunit;

namespace NexusApp.Tests;

// The hand-authored dock glyphs (guides, trade, map) live as a raw JSON string literal that is
// only ever parsed at RUNTIME, when AnimatedDockIcon builds the icon. A malformed path string or
// a schema typo therefore shows up as a silently missing glyph in the dock rather than as a build
// error - which is precisely the class of defect nobody notices until they look at the rail. This
// walks the same two steps the control walks (System.Text.Json, then Geometry.Parse per path
// part, AnimatedDockIcon.MakeGeometry) so those failures surface at build time instead.
public class DockIconSpecsCustomTests
{
    private static JsonDocument Doc() => JsonDocument.Parse(DockIconSpecsCustom.Json);

    [Fact]
    public void Json_IsWellFormed_AndCarriesTheHandAuthoredKeys()
    {
        using var doc = Doc();
        foreach (var key in new[] { "guides", "trade", "map" })
            Assert.True(doc.RootElement.TryGetProperty(key, out _), $"missing hand-authored glyph: {key}");
    }

    [Fact]
    public void EveryPathPart_SurvivesTheSameGeometryParseTheControlUses()
    {
        using var doc = Doc();
        foreach (var glyph in doc.RootElement.EnumerateObject())
        foreach (var part in glyph.Value.GetProperty("parts").EnumerateArray())
        {
            if (part.GetProperty("el").GetString() != "path") continue;
            var d = part.GetProperty("d").GetString();
            var id = part.GetProperty("id").GetString();

            // Geometry.Parse throws on malformed data; a non-empty bounds also rules out a string
            // that parses to nothing and would draw an invisible glyph.
            var geo = Geometry.Parse(d ?? "");
            Assert.False(geo.Bounds.IsEmpty, $"{glyph.Name}/{id} parsed to empty bounds: {d}");
        }
    }

    [Fact]
    public void MapInnerOrbit_KeepsItsRotation_NotSilentlyFlattened()
    {
        // The starmap glyph's inner orbit is an ellipse tilted -25 degrees, and that tilt is
        // load-bearing: drawn level, the outer orbit + inner orbit + filled centre dot stack into
        // an unmistakable eyeball (the exact reason the previous glyph was replaced). WPF's path
        // mini-language accepts a rotation angle in the arc segment, but if it were ever ignored
        // the icon would regress to the eye read with no other symptom, so this asserts the shape
        // of the parsed geometry rather than trusting the string.
        using var doc = Doc();
        var inner = doc.RootElement.GetProperty("map").GetProperty("parts")
            .EnumerateArray().First(p => p.GetProperty("id").GetString() == "p1");

        var bounds = Geometry.Parse(inner.GetProperty("d").GetString()!).Bounds;

        // rx=6, ry=2.2 at -25 degrees: half-height grows to ~3.23 (so height ~6.45). Drawn level
        // it would be exactly ry*2 = 4.4, which this range excludes.
        Assert.InRange(bounds.Height, 6.0, 7.0);
        Assert.InRange(bounds.Width, 10.5, 11.5);
    }
}
