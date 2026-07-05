using System;

namespace NexusApp.Services;

// Crafting and shopping amounts are stored in SCU. The in-game fabricator shows sub-1-SCU
// amounts in centiSCU (1 SCU = 100 cSCU), so we render an amount under 1 SCU as cSCU and leave
// whole-SCU-or-larger amounts, and non-SCU units like "units" or "x", exactly as they are. This
// keeps a mined resource ("1 SCU") reading naturally while a tiny crafting input reads "2 cSCU".
public static class CraftAmount
{
    private const string Scu = "SCU";
    private const string CentiScu = "cSCU";

    private static bool AsCentiScu(double quantity, string? unit) =>
        string.Equals(unit, Scu, StringComparison.OrdinalIgnoreCase) && quantity > 0 && quantity < 1;

    // The numeric part, converted to cSCU when applicable (0.02 -> "2", 0.015 -> "1.5", 5 -> "5").
    public static string Value(double quantity, string? unit) =>
        (AsCentiScu(quantity, unit) ? quantity * 100 : quantity).ToString("0.##");

    // The unit label to pair with Value(): "cSCU", "SCU", "units", ...
    public static string Unit(double quantity, string? unit) =>
        AsCentiScu(quantity, unit) ? CentiScu : (unit ?? "");

    // Combined "2 cSCU" / "5 SCU" / "3 units".
    public static string Format(double quantity, string? unit) =>
        $"{Value(quantity, unit)} {Unit(quantity, unit)}";
}
