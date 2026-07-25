using System.Text.Json;

namespace NexusApp.Services;

// One friendly exception type for every way a manifest can be unacceptable, mirroring
// NetworkFileException: callers log the message and fail closed, never crash.
public sealed class UpdateManifestException : Exception
{
    // True only for a manifest whose schema is NEWER than this build understands. That is
    // not a fault: the bytes were signature-verified, so the publisher really did ship a
    // manifest for a later Nexus. Callers treat it as "nothing to do here", not a failure.
    public bool SchemaTooNew { get; }

    public UpdateManifestException(string message) : base(message) { }

    public UpdateManifestException(string message, bool schemaTooNew) : base(message) =>
        SchemaTooNew = schemaTooNew;

    // Keeps the underlying parser detail (JSON path and line) attached for diagnostics
    // while callers still show only the friendly message.
    public UpdateManifestException(string message, Exception inner) : base(message, inner) { }
}

public sealed class UpdateAsset
{
    public string Name { get; init; } = "";
    public string Sha256 { get; init; } = "";
    public long Size { get; init; }
}

// The signed update manifest published as a release asset. Parse is deliberately strict:
// it runs on bytes that already passed the signature check, but strictness here means a
// signing-script bug can never push the app into an undefined state.
public sealed class UpdateManifest
{
    public const int CurrentSchema = 1;

    // A real manifest is ~400 bytes; 16 KB leaves room for growth while making a
    // flooding response die before it is ever buffered whole.
    public const int MaxManifestBytes = 16 * 1024;

    // Sanity ceiling on a single asset. Current releases are well under 200 MB;
    // anything above half a GB is not a Nexus release.
    public const long MaxAssetBytes = 524_288_000;

    // The only asset names this app will ever download. Fixed here, never read from
    // the manifest, so a manifest string can never influence a filesystem path or URL.
    public static readonly string[] KnownAssetNames = ["Nexus_Setup.exe", "NexusApp_portable.zip"];

    public int Schema { get; init; }
    public Version Version { get; init; } = new(0, 0, 0);
    public DateTime PublishedUtc { get; init; }
    public IReadOnlyList<UpdateAsset> Assets { get; init; } = [];

    // Maps AppInfo.Distribution to the asset that updates it. "Unknown" gets null:
    // no guessing about what to install on a machine we cannot classify.
    public UpdateAsset? AssetFor(string distribution) => distribution switch
    {
        "Installer" => Assets.FirstOrDefault(a => a.Name == KnownAssetNames[0]),
        "Portable" => Assets.FirstOrDefault(a => a.Name == KnownAssetNames[1]),
        _ => null,
    };

    private sealed class Dto
    {
        public int Schema { get; set; }
        public string? Version { get; set; }
        public string? Published { get; set; }
        public List<AssetDto>? Assets { get; set; }
    }

    private sealed class AssetDto
    {
        public string? Name { get; set; }
        public string? Sha256 { get; set; }
        public long Size { get; set; }
    }

    private static readonly JsonSerializerOptions JsonOpts = new() { PropertyNameCaseInsensitive = true };

    public static UpdateManifest Parse(byte[] utf8)
    {
        if (utf8 is null || utf8.Length == 0)
            throw new UpdateManifestException("The update manifest is empty.");
        if (utf8.Length > MaxManifestBytes)
            throw new UpdateManifestException("The update manifest is larger than expected.");

        Dto? dto;
        try { dto = JsonSerializer.Deserialize<Dto>(utf8, JsonOpts); }
        catch (JsonException ex) { throw new UpdateManifestException("The update manifest is not valid JSON.", ex); }
        if (dto is null) throw new UpdateManifestException("The update manifest is empty.");

        // A schema ABOVE ours means a newer Nexus is required, which the caller reports as
        // up-to-date rather than as an error; anything below is a malformed or ancient
        // manifest and stays an ordinary failure.
        if (dto.Schema != CurrentSchema)
            throw new UpdateManifestException($"Unsupported manifest schema {dto.Schema}.", schemaTooNew: dto.Schema > CurrentSchema);

        // Three-part versions only (release tags are always vX.Y.Z); Build < 0 means a
        // two-part version parsed, Revision >= 0 means a four-part one did.
        if (!System.Version.TryParse(dto.Version, out var version) || version.Build < 0 || version.Revision >= 0)
            throw new UpdateManifestException("The update manifest version is not a valid three-part version.");

        if (dto.Assets is not { Count: 2 })
            throw new UpdateManifestException("The update manifest must list exactly the two known assets.");

        var assets = new List<UpdateAsset>(2);
        foreach (var known in KnownAssetNames)
        {
            // A JSON null survives deserialization as a null element and passes the count guard
            // above, so it is filtered here rather than dereferenced.
            var match = dto.Assets.Where(a => a is not null && a.Name == known).ToList();
            if (match.Count != 1)
                throw new UpdateManifestException($"The update manifest must list \"{known}\" exactly once.");
            var a = match[0];
            if (!IsLowerHex64(a.Sha256))
                throw new UpdateManifestException($"The hash for \"{known}\" is not 64 lowercase hex characters.");
            if (a.Size <= 0 || a.Size > MaxAssetBytes)
                throw new UpdateManifestException($"The size for \"{known}\" is outside the accepted range.");
            assets.Add(new UpdateAsset { Name = known, Sha256 = a.Sha256!, Size = a.Size });
        }

        // Published is informational (the signature covers it; nothing decides on it), so a
        // missing or odd timestamp is tolerated rather than blocking an otherwise valid update.
        DateTime published = default;
        if (!string.IsNullOrEmpty(dto.Published))
            DateTime.TryParse(dto.Published, null, System.Globalization.DateTimeStyles.AdjustToUniversal, out published);

        return new UpdateManifest { Schema = dto.Schema, Version = version, PublishedUtc = published, Assets = assets };
    }

    internal static bool IsLowerHex64(string? s)
    {
        if (s is null || s.Length != 64) return false;
        foreach (var c in s)
            if (c is not ((>= '0' and <= '9') or (>= 'a' and <= 'f'))) return false;
        return true;
    }
}
