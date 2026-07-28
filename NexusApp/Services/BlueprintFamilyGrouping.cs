using System.Text.RegularExpressions;
using NexusApp.Models;

namespace NexusApp.Services;

/// <summary>
/// Groups Blueprint Library entries by "family" so skin/colour variants of the same model
/// collapse into one row: strips quoted skin names and parenthetical decorations, then (for
/// non-armor items) trims trailing colour/edition words, or (for armor) keeps everything up to
/// and including the recognized armor-piece word and drops the rest. Extracted from
/// MainWindow.xaml.cs following the app's existing RsiHandleParser/ComponentStringReference
/// precedent - a pure static class the WPF layer calls.
/// </summary>
public static class BlueprintFamilyGrouping
{
    private static readonly string[] ArmorPieces = ["Helmet", "Core", "Arms", "Legs", "Backpack", "Undersuit", "Suit"];
    private static readonly HashSet<string> VariantWords = new(StringComparer.OrdinalIgnoreCase)
    {
        "black","blue","green","red","grey","gray","white","dark","aqua","crusader","edition","woodland",
        "desert","tan","olive","sand","orange","yellow","purple","pink","brown","navy","teal","crimson",
        "forest","storm","snow","arctic","modified","light","silver","gold","bronze","maroon","khaki",
        "digital","urban","jungle","midnight","obsidian","frost","ember","rust","slate","charcoal","ivory",
        "copper","azure","emerald","ruby","onyx","steel","carbon","ash","coal","mint","lime","rose","plum",
        "cobalt","sage","clay","stone","smoke","blood","ghost","shadow","night","solar","lunar","nova",
    };

    // subgroup = real subcategory, or armor piece, or null (no grouping level)
    public static string? Subgroup(Blueprint b)
    {
        if (!string.IsNullOrEmpty(b.SubCategory)) return b.SubCategory;
        if (b.Category == "Armor") return ArmorPiece(b.Name);
        return null;
    }

    public static string ArmorPiece(string name)
    {
        foreach (var p in ArmorPieces)
            if (Regex.IsMatch(name, $"\\b{p}\\b", RegexOptions.IgnoreCase))
                return p;
        return "Other";
    }

    // Drops quoted skins + parentheticals and collapses whitespace, leaving the bare model words.
    public static string StripDecorations(string name)
    {
        var s = Regex.Replace(name, "\"[^\"]*\"", "");
        s = Regex.Replace(s, "\\([^)]*\\)", "");
        return Regex.Replace(s, "\\s+", " ").Trim();
    }

    // family = name with quoted skins / parentheticals / trailing colour words removed (collapses variants)
    public static string FamilyKey(string name)
    {
        var s = StripDecorations(name);
        var parts = s.Split(' ').ToList();
        while (parts.Count > 0 && VariantWords.Contains(parts[^1])) parts.RemoveAt(parts.Count - 1);
        return parts.Count > 0 ? string.Join(" ", parts) : (s.Length > 0 ? s : name);
    }

    // Family key used for grouping variants together. Weapon/ship skins are quoted
    // or parenthesised, so the colour-list FamilyKey handles them. Armor skins are
    // free-text words trailing the piece ("Antium Helmet Moss Camo") that a fixed
    // colour list can't catch - so for armor we keep everything up to and including
    // the piece word and drop the rest, collapsing all of a model's skins into one.
    public static string FamilyKeyOf(Blueprint b)
        => b.Category == "Armor" ? ArmorFamilyKey(b.Name) : FamilyKey(b.Name);

    public static string ArmorFamilyKey(string name)
    {
        var piece = ArmorPiece(name);
        if (piece != "Other")
        {
            var parts = StripDecorations(name).Split(' ');
            for (int i = 0; i < parts.Length; i++)
                if (string.Equals(parts[i], piece, StringComparison.OrdinalIgnoreCase))
                    return string.Join(" ", parts.Take(i + 1));
        }
        return FamilyKey(name);   // piece word not found as a standalone token; fall back
    }
}
