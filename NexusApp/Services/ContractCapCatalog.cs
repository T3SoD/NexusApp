using System.IO;
using System.Text.Json;

namespace NexusApp.Services;

// Datamined map of contract debugName -> max container size (SCU). The Game.log records a haul's
// contract debugName on every marker, so this yields the container cap by exact name with no OCR.
// Embedded and offline; regenerate per patch with gen_contract_caps.py (same source as the seed).
public sealed class ContractCapCatalog
{
    private readonly Dictionary<string, int> _caps;

    private ContractCapCatalog(Dictionary<string, int> caps) => _caps = caps;

    public int Count => _caps.Count;

    // The contract's max container size, or null when this contract has no datamined cap.
    public int? Lookup(string? contractToken)
    {
        if (string.IsNullOrWhiteSpace(contractToken)) return null;
        return _caps.TryGetValue(contractToken.Trim(), out var cap) ? cap : null;
    }

    private static ContractCapCatalog? _instance;
    public static ContractCapCatalog Instance => _instance ??= LoadEmbedded();

    public static ContractCapCatalog LoadEmbedded()
    {
        using var stream = typeof(ContractCapCatalog).Assembly
            .GetManifestResourceStream("NexusApp.Data.contract_caps.json");
        if (stream is null) return new ContractCapCatalog(new Dictionary<string, int>());
        var caps = JsonSerializer.Deserialize<Dictionary<string, int>>(stream)
                   ?? new Dictionary<string, int>();
        return new ContractCapCatalog(caps);
    }
}
