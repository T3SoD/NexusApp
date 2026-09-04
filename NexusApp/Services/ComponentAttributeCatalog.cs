using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;

namespace NexusApp.Services;

/// <summary>A graded ship component's datamined attributes: size ("1"), grade ("A"),
/// class ("Stealth"). Reference data only.</summary>
public sealed record ComponentAttributes(string Size, string Grade, string Class);

// Display-name -> size/grade/class table for graded ship components (shields, coolers,
// power plants, quantum drives, radars, mining lasers). Derived data: DataCore AttachDef
// records joined to CIG's packed localization, extracted per SC patch (check_p4k_update.ps1
// ritual); the extraction tooling stays local, only this table ships, and it reaches users
// through a new build (no OTA). 328 components at 4.10.0; name collisions are dropped at
// generation so a label can never show another component's attributes.
public sealed class ComponentAttributeCatalog
{
    private readonly Dictionary<string, ComponentAttributes> _byName;
    private readonly HashSet<string> _tokenFamilies;

    public ComponentAttributeCatalog(Dictionary<string, ComponentAttributes> byName,
        IEnumerable<string>? tokenFamilies = null)
    {
        _byName = new Dictionary<string, ComponentAttributes>(byName, StringComparer.OrdinalIgnoreCase);
        _tokenFamilies = new HashSet<string>(tokenFamilies ?? [], StringComparer.OrdinalIgnoreCase);
    }

    public int Count => _byName.Count;

    /// <summary>Attributes for a component display name, or null when the table does not
    /// know it (a non-component, or a newer game build than the table). Callers render the
    /// plain name rather than a guess.</summary>
    public ComponentAttributes? Resolve(string? name) =>
        !string.IsNullOrWhiteSpace(name) && _byName.TryGetValue(name, out var a) ? a : null;

    /// <summary>Whether a Game.log purchase token names a graded component, judged by its
    /// family prefix (COOL_, SHLD_, ...). Ships, weapons, and vehicles share display names
    /// with components (Eclipse, Predator, Nova), so the wallet gates decoration on this
    /// rather than on the name alone.</summary>
    public bool IsComponentToken(string? token)
    {
        if (string.IsNullOrWhiteSpace(token)) return false;
        int us = token.IndexOf('_');
        var family = us < 0 ? token : token[..us];
        return _tokenFamilies.Contains(family);
    }

    /// <summary>The name with its attribute label: "Mirage - S1 - Stealth - A"
    /// ([Name]-[Size]-[Type]-[Rating]). Blank attributes are skipped, and an unknown name
    /// comes back unchanged, so this is safe to call on any display string.</summary>
    public string Decorate(string name)
    {
        var a = Resolve(name);
        if (a is null) return name;
        var parts = new List<string>(4) { name };
        if (!string.IsNullOrWhiteSpace(a.Size)) parts.Add($"S{a.Size}");
        if (!string.IsNullOrWhiteSpace(a.Class)) parts.Add(a.Class);
        if (!string.IsNullOrWhiteSpace(a.Grade)) parts.Add(a.Grade);
        return string.Join(" - ", parts);
    }

    // Lazy app-wide instance, the ContractCapCatalog idiom: loaded on first decorated
    // render, never on the startup path.
    private static ComponentAttributeCatalog? _instance;
    public static ComponentAttributeCatalog Instance => _instance ??= LoadEmbedded();

    public static ComponentAttributeCatalog LoadEmbedded()
    {
        using var stream = typeof(ComponentAttributeCatalog).Assembly
            .GetManifestResourceStream("NexusApp.Data.component_attributes.json")
            ?? throw new InvalidOperationException("component_attributes.json embedded resource not found");
        return Load(stream);
    }

    public static ComponentAttributeCatalog Load(Stream stream)
    {
        var raw = JsonSerializer.Deserialize<RawFile>(stream)
                  ?? throw new InvalidOperationException("component_attributes.json deserialized to null");
        var map = new Dictionary<string, ComponentAttributes>();
        foreach (var (name, e) in raw.Names ?? [])
            map[name] = new ComponentAttributes(e.Size ?? "", e.Grade ?? "", e.Class ?? "");
        return new ComponentAttributeCatalog(map, raw.TokenFamilies);
    }

    private sealed class RawFile
    {
        [System.Text.Json.Serialization.JsonPropertyName("tokenFamilies")]
        public List<string>? TokenFamilies { get; set; }

        [System.Text.Json.Serialization.JsonPropertyName("names")]
        public Dictionary<string, RawEntry>? Names { get; set; }
    }

    private sealed class RawEntry
    {
        [System.Text.Json.Serialization.JsonPropertyName("size")]
        public string? Size { get; set; }

        [System.Text.Json.Serialization.JsonPropertyName("grade")]
        public string? Grade { get; set; }

        [System.Text.Json.Serialization.JsonPropertyName("class")]
        public string? Class { get; set; }
    }
}
