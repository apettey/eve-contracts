using System.Globalization;
using EveContracts.Core.Data;
using EveContracts.Core.Models;
using Microsoft.Extensions.DependencyInjection;

namespace EveContracts.Core.Services;

/// <summary>Typed accessors over the AppSettings key/value table with an in-memory cache.</summary>
public class SettingsService
{
    private readonly IServiceScopeFactory _scopes;
    private Dictionary<string, string> _cache = new();

    public SettingsService(IServiceScopeFactory scopes) => _scopes = scopes;

    public async Task LoadAsync(CancellationToken ct = default)
    {
        using var scope = _scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDb>();
        _cache = (await Microsoft.EntityFrameworkCore.EntityFrameworkQueryableExtensions
            .ToListAsync(db.AppSettings, ct)).ToDictionary(s => s.Key, s => s.Value);
    }

    private string Get(string key, string fallback) => _cache.GetValueOrDefault(key, fallback);

    public async Task SetAsync(string key, string value, CancellationToken ct = default)
    {
        _cache[key] = value;
        using var scope = _scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDb>();
        var row = await db.AppSettings.FindAsync([key], ct);
        if (row is null) db.AppSettings.Add(new AppSetting { Key = key, Value = value });
        else row.Value = value;
        await db.SaveChangesAsync(ct);
    }

    private double GetD(string key, double fallback) =>
        double.TryParse(Get(key, ""), NumberStyles.Any, CultureInfo.InvariantCulture, out var v) ? v : fallback;

    // Scanner thresholds (defaults match the design prototype)
    public double MinMarginPct { get => GetD("min_margin_pct", 10); }
    public double MinDailyVolume { get => GetD("min_daily_volume", 20); }
    public double MaxPrice { get => GetD("max_price", 800e6); }
    public double FeePct { get => GetD("fee_pct", 4.5); }
    public double HaulRate { get => GetD("haul_rate", 800); }
    public bool HighsecOnly { get => Get("highsec_only", "1") == "1"; }
    /// <summary>"sell" = liquid items at Jita sell min (illiquid at buy); "buy" = everything at Jita buy max.</summary>
    public string PriceBasis { get => Get("price_basis", "sell") == "buy" ? "buy" : "sell"; }
    public bool SoundAlerts { get => Get("sound_alerts", "1") == "1"; }
    public double AlertProfitMin { get => GetD("alert_profit_min", 100e6); }
    public bool IncludeAuctions { get => Get("include_auctions", "0") == "1"; }
    public bool IncludeCharges { get => Get("include_charges", "0") == "1"; }
    public string Region { get => Get("region", "The Forge"); }
    public string EsiClientId { get => Get("esi_client_id", ""); }
    public DateTime LastPublicScan
    {
        get => DateTime.TryParse(Get("last_public_scan", ""), null, DateTimeStyles.RoundtripKind, out var v) ? v : DateTime.MinValue;
    }

    public Task SetLastPublicScanAsync(DateTime utc, CancellationToken ct = default) => SetAsync("last_public_scan", utc.ToString("o"), ct);
}
