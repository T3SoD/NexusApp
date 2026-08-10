using System;
using System.Collections.Generic;
using System.Linq;
using NexusApp.Models;
using NexusApp.Services;

namespace NexusApp.Views;

// The wallet block's pure folds (OCR wallet spec sections 5 and 11; superseded-mock state
// vocabulary carried forward). Everything the WALLET block and the untracked ledger rows render
// is derived here so MoneyPanel.cs (was TradePage.Profit.cs; moved 2026-08-09, task B4) keeps
// doing no arithmetic, the ProfitDisplay split.
internal static class WalletDisplay
{
    internal const string BlockLabel = "WALLET";
    internal const string SetupHint =
        "Set your balance below, or open your mobiGlas in game with the wallet region configured in Settings.";
    internal const string UntrackedWhere = "outside commodity trading";
    internal const string UntrackedBadge = "UNTRACKED";

    // Mock derivation order: no anchor, then a provably wrong number (red, never clamped), then
    // offline dimming, then age, then healthy.
    internal static WalletUiState State(bool hasAnchor, long? estimate, DateTime? anchorUtc,
                                        DateTime nowUtc, bool sessionLive)
    {
        if (!hasAnchor) return WalletUiState.NotSet;
        if (estimate is < 0) return WalletUiState.Impossible;
        if (!sessionLive) return WalletUiState.Offline;
        if (anchorUtc is { } a && nowUtc - a >= WalletTracker.AgingThreshold) return WalletUiState.Aging;
        return WalletUiState.Current;
    }

    internal static string StateChipWord(WalletUiState state) => state switch
    {
        WalletUiState.NotSet => "NOT SET",
        WalletUiState.Impossible => "IMPOSSIBLE",
        WalletUiState.Offline => "GAME OFFLINE",
        WalletUiState.Aging => "AGING",
        _ => "TRACKING",
    };

    internal static string Provenance(string? source, DateTime? anchorUtc, DateTime nowUtc)
    {
        if (source is null || anchorUtc is null) return SetupHint;
        var age = AgeText(nowUtc - anchorUtc.Value);
        return source == "Manual" ? $"manual entry, {age} ago" : $"captured from mobiGlas {age} ago";
    }

    internal static string AgeText(TimeSpan age)
    {
        if (age < TimeSpan.FromMinutes(1)) return "moments";
        if (age < TimeSpan.FromHours(1)) return $"{(int)age.TotalMinutes}m";
        if (age < TimeSpan.FromHours(24)) return $"{(int)age.TotalHours}h {age.Minutes}m";
        return $"{(int)age.TotalDays}d";
    }

    /// <summary>The diagnostic snapshot's wallet line (spec section 9): anchored presence,
    /// source, age and the untracked count only. A snapshot is shared, so the balance and the
    /// row amounts never land in it.</summary>
    internal static string SnapshotLine(bool hasAnchor, string? source, DateTime? anchorUtc,
                                        DateTime nowUtc, int untrackedCount)
    {
        if (!hasAnchor || anchorUtc is null) return "not anchored";
        return $"anchored ({source}, {AgeText(nowUtc - anchorUtc.Value)} ago), "
             + $"{untrackedCount} untracked this session";
    }

    /// <summary>The header chip and card value: the exact estimate (negatives render, never
    /// clamp) or the not-set word.</summary>
    internal static string HeaderValue(long? estimate) =>
        estimate is { } e ? ProfitDisplay.Format(e) + " aUEC" : "not set";

    // The panel's SetupHint says "below", which is only true on the Trade panel; card and chip
    // surfaces use this location-free variant.
    internal const string CardHint = "No anchor yet. Open your mobiGlas in game, or set a balance on Trade.";

    // The attribution label (contract name or "N contracts completed") replaces the generic
    // title when the tracker found one; the UNTRACKED badge beside it keeps the row's nature.
    internal static string UntrackedTitle(long amount, string? label = null) =>
        label ?? (amount >= 0 ? "Untracked income" : "Untracked purchase");

    /// <summary>The session timeline with untracked rows and shop purchases interleaved by stamp,
    /// newest first, capped like the plain ledger. Items are CommodityTransaction, UntrackedEntry,
    /// or ShopPurchase.</summary>
    internal static IReadOnlyList<object> MergeRows(IReadOnlyList<CommodityTransaction> txs,
                                                    IReadOnlyList<UntrackedEntry> untracked,
                                                    IReadOnlyList<ShopPurchase> purchases, int cap)
    {
        var all = new List<(DateTime T, object Item)>(txs.Count + untracked.Count + purchases.Count);
        foreach (var tx in txs) all.Add((tx.TimestampUtc, tx));
        foreach (var u in untracked) all.Add((u.Utc, u));
        foreach (var p in purchases) all.Add((p.TimestampUtc, p));
        return all.OrderByDescending(x => x.T).Take(cap).Select(x => x.Item).ToList();
    }

    // "SCShop_RestStop_Pharmacy-001" reads as "RestStop Pharmacy". Deterministic, no guessing.
    // Same cleaner the commodity WHERE line uses (review fix 2026-08-07: two independently-written
    // token cleaners were rendering into the same merged ledger column with different outputs);
    // this is now a thin alias so both surfaces can never drift again.
    public static string ShopDisplayName(string shopName) => ProfitDisplay.ShopLabel(shopName);

    /// <summary>The item's display name, or the cleaned shop name when the catalog knows neither
    /// the GUID nor the token. Never blank, never a guess. Reads the name resolved once at apply
    /// time (ProfitTracker.Ingest via ItemNameCatalog.ResolvePurchaseName) rather than touching the
    /// catalog here - a purchase whose name was never populated (tests construct ShopPurchase
    /// directly) still renders correctly, it just falls straight to the cleaned shop name.</summary>
    public static string PurchaseTitle(ShopPurchase p) => p.DisplayName ?? ShopDisplayName(p.ShopName);
}

// Public, not internal: xunit test methods are public and CS0051 forbids an internal type in
// their signatures, the same reason ProfitState is public.
public enum WalletUiState { NotSet, Current, Aging, Impossible, Offline }
