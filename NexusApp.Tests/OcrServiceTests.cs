using NexusApp.Services;
using Xunit;

namespace NexusApp.Tests;

// OcrService.ExtractRsValue (Services/OcrService.cs) is the RS Decoder's digit parser: comma/period
// stripping between digits, a regex collapse for OCR-misread thousands separators, then a bounded
// digit-run scan. OcrService.cs preprocessing is locked by project rule; the ONLY permitted edit here (per
// the 2026-07-28 app-wide review spec's seam ruling) was widening ExtractRsValue's access modifier
// from private to internal, method body byte-identical, so it is reachable via the existing
// InternalsVisibleTo("NexusApp.Tests") entry in NexusApp.csproj. These tests PIN the method's current
// shipped behavior - they assert what it does, not what it "should" do.
public class OcrServiceTests
{
    [Theory]
    // -- comma/period stripping between digits ("17,200"/"17.200" -> "17200") --
    [InlineData("RS DECODER 17,200 CREDITS", 17200)]
    [InlineData("RS DECODER 17.200 CREDITS", 17200)]
    // -- thousands-separator regex collapse: "X XXX" -> "XXXX", but ONLY a single leading digit --
    [InlineData("value 5 000 detected", 5000)]
    // Documented current-behavior limit (not adjusted to "fix"): the collapse regex is
    // "(?<!\d)(\d) (\d{3})(?!\d)" - exactly one digit before the space. A two-digit thousands
    // group misread with a space ("17 200" standing in for 17,200) does NOT collapse, so the two
    // halves ("17", "200") are each under the 4-digit minimum and the call returns null.
    [InlineData("value 17 200 detected", null)]
    // -- 2000-200000 bounds: acceptance at both ends --
    [InlineData("value 2000 detected", 2000)]
    [InlineData("value 200000 detected", 200000)]
    // -- 2000-200000 bounds: rejection just outside both ends --
    [InlineData("value 1999 detected", null)]
    [InlineData("value 200001 detected", null)]
    // -- no digits at all --
    [InlineData("no value found here", null)]
    // -- multiple digit runs: a too-short/out-of-range run is skipped, scan continues to the next --
    [InlineData("12 45000", 45000)]
    [InlineData("1500 8000", 8000)]
    public void ExtractRsValue_PinsCurrentBehavior(string ocrText, int? expected)
    {
        Assert.Equal(expected, OcrService.ExtractRsValue(ocrText));
    }
}
