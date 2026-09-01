using System.Text.Json;
using EveContracts.Core.Models;

namespace EveContracts.Core.Services;

public static class Categories
{
    public const int Ship = 6;
    public const int Module = 7;
    public const int Charge = 8;
}

public record EvalInput(
    string ContractType,
    double Price,
    double SecurityStatus,
    int JumpsToJita,
    DateTime DateExpired,
    string Title,
    IReadOnlyList<EvalItem> Items);

public record EvalItem(
    int TypeId,
    string Name,
    long Quantity,
    bool IsIncluded,
    int CategoryId,
    double PackagedVolumeM3,
    double JitaSell,
    double JitaBuy,
    double PrevDayVolume,
    double? MinVolumeOverride,
    bool Excluded);

public record EvalResult(
    string Verdict,
    double JitaSellValue,
    double Fees,
    double Hauling,
    double NetProfit,
    double Margin,
    double TotalM3,
    IReadOnlyList<string> Flags)
{
    public string FlagsJson => JsonSerializer.Serialize(Flags);
}

public record EvalThresholds(double MinMarginPct, double MinDailyVolume, double MaxPrice,
    double FeePct, double HaulRate, bool HighsecOnly, bool IncludeAuctions, bool IncludeCharges);

/// <summary>
/// Pure profit-evaluation pipeline per the handoff spec. Ships + modules only;
/// verdict order: EXCLUDED → LOWSEC → SKIP → LOW VOL → THIN → BUY.
/// </summary>
public static class EvaluationService
{
    public static EvalResult Evaluate(EvalInput c, EvalThresholds t)
    {
        var flags = new List<string>();

        // 1. Contract type gate + want-to-buy skip
        var included = c.Items.Where(i => i.IsIncluded).ToList();
        var requested = c.Items.Where(i => !i.IsIncluded).ToList();
        var typeOk = c.ContractType == "item_exchange" || (t.IncludeAuctions && c.ContractType == "auction");
        var wantToBuy = c.Price <= 0 && requested.Count > 0 && included.Count == 0;

        // Scope: every included item must be a ship or module (charges optional)
        bool inScope(EvalItem i) => i.CategoryId == Categories.Ship || i.CategoryId == Categories.Module
            || (t.IncludeCharges && i.CategoryId == Categories.Charge);
        var scopeOk = included.Count > 0 && included.All(i => !i.Excluded && inScope(i));

        // 5. Compute economics (always, so the UI can show them even on excluded rows)
        var sellValue = included.Sum(i => i.JitaSell * i.Quantity)
                      - requested.Sum(i => i.JitaBuy * i.Quantity);
        var totalM3 = included.Sum(i => i.PackagedVolumeM3 * i.Quantity);
        var fees = Math.Max(0, sellValue) * (t.FeePct / 100.0);
        var hauling = c.JumpsToJita <= 0 ? 0 : totalM3 * t.HaulRate * (0.5 + c.JumpsToJita / 10.0);
        var netProfit = sellValue - c.Price - fees - hauling;
        var margin = c.Price > 0 ? netProfit / c.Price : 0;

        // 3. Volume gate: every included item above its per-item (or global) floor
        var lowVol = included.Any(i => i.PrevDayVolume <= (i.MinVolumeOverride ?? t.MinDailyVolume));

        // Risk flags (informational, never block)
        var titleLower = c.Title.ToLowerInvariant();
        if (titleLower.Contains("cheap") || titleLower.Contains("quick sale") || c.Title.Contains('★'))
            flags.Add("SCAM_TITLE");
        if (c.DateExpired - DateTime.UtcNow < TimeSpan.FromHours(24)) flags.Add("EXPIRES_SOON");
        if (lowVol) flags.Add("LOW_VOLUME_ITEM");
        if (c.SecurityStatus < 0.5) flags.Add("LOWSEC_PICKUP");
        if (included.Any(i => i.JitaSell <= 0)) flags.Add("UNPRICED_ITEM");

        string verdict;
        if (!typeOk || wantToBuy || !scopeOk || c.Price > t.MaxPrice) verdict = "EXCLUDED";
        else if (t.HighsecOnly && c.SecurityStatus < 0.5) verdict = "LOWSEC";     // 2
        else if (netProfit <= 0) verdict = "SKIP";
        else if (lowVol) verdict = "LOW VOL";
        else if (margin < t.MinMarginPct / 100.0) verdict = "THIN";
        else verdict = "BUY";

        return new EvalResult(verdict, sellValue, fees, hauling, netProfit, margin, totalM3, flags);
    }

    /// <summary>Estimated liquidation window from the worst included-item daily volume.</summary>
    public static string TimeToLiquidate(IEnumerable<EvalItem> includedItems)
    {
        var vols = includedItems.Where(i => i.IsIncluded).Select(i => i.PrevDayVolume).DefaultIfEmpty(0);
        var worst = vols.Min();
        return worst > 100 ? "< 1 day" : worst > 30 ? "1–3 days" : "3–7 days";
    }
}
