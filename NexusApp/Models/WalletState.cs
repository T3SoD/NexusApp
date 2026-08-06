using NexusApp.Services;

namespace NexusApp.Models;

// One unexplained wallet delta discovered by an OCR capture. Amount is signed: positive is
// untracked income, negative is an untracked purchase. Timestamps live in the Game.log clock
// domain like every ledger figure.
public sealed class UntrackedEntry
{
    public DateTime Utc { get; set; }
    public long Amount { get; set; }
}

// Per-channel wallet anchor plus the untracked rows it has accumulated. The anchor is the last
// trusted balance (OCR capture or manual entry); the estimate everywhere else is derived, never
// stored.
public sealed class WalletChannelState
{
    public GameChannel Channel { get; set; }
    public long Anchor { get; set; }
    public DateTime AnchorUtc { get; set; }
    public string Source { get; set; } = "Ocr"; // "Ocr" | "Manual"
    public List<UntrackedEntry> Untracked { get; set; } = new();
}

public sealed class WalletState
{
    public int Schema { get; set; } = 1;
    public List<WalletChannelState> Channels { get; set; } = new();
}
