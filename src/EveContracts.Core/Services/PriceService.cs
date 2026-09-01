using System.Globalization;
using System.Text.Json;
using EveContracts.Core.Data;
using EveContracts.Core.Esi;
using EveContracts.Core.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace EveContracts.Core.Services;

/// <summary>
/// Keeps the Prices table current: Jita 4-4 best buy/sell via the Fuzzwork
/// aggregates API (batched, one HTTP call per ~900 types) refreshed hourly,
/// and previous-day traded volume via ESI market history (per type, but only
/// for types that actually appear in live contracts, cached ~24 h since
/// history only changes daily).
/// </summary>
public class PriceService
{
    private const string FuzzworkAggregates = "https://market.fuzzwork.co.uk/aggregates/";
    private const int BatchSize = 900;

    private readonly IServiceScopeFactory _scopes;
    private readonly IHttpClientFactory _httpFactory;
    private readonly EsiClient _esi;
    private readonly StaticDataCache _static;
    private readonly SettingsService _settings;
    private readonly ILogger<PriceService> _log;

    public PriceService(IServiceScopeFactory scopes, IHttpClientFactory httpFactory, EsiClient esi,
        StaticDataCache staticData, SettingsService settings, ILogger<PriceService> log)
    {
        _scopes = scopes;
        _httpFactory = httpFactory;
        _esi = esi;
        _static = staticData;
        _settings = settings;
        _log = log;
    }

    /// <summary>Type ids that appear in any live contract (the only ones worth pricing).</summary>
    public async Task<List<int>> GetNeededTypeIdsAsync(CancellationToken ct = default)
    {
        using var scope = _scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDb>();
        var now = DateTime.UtcNow;
        return await db.ContractItems
            .Join(db.PublicContracts.Where(c => c.DateExpired > now),
                i => i.ContractId, c => c.ContractId, (i, c) => i.TypeId)
            .Distinct().ToListAsync(ct);
    }

    /// <summary>
    /// Types worth a per-type ESI history call: included items of live contracts that
    /// are in scanner scope (ships/modules, charges if enabled). Everything else is
    /// EXCLUDED by evaluation before the volume gate, so its history is never read.
    /// </summary>
    public async Task<List<int>> GetVolumeNeededTypeIdsAsync(CancellationToken ct = default)
    {
        using var scope = _scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDb>();
        var now = DateTime.UtcNow;
        var ids = await db.ContractItems.Where(i => i.IsIncluded)
            .Join(db.PublicContracts.Where(c => c.DateExpired > now),
                i => i.ContractId, c => c.ContractId, (i, c) => i.TypeId)
            .Distinct().ToListAsync(ct);
        if (!_static.Ready) await _static.LoadAsync(ct);
        var includeCharges = _settings.IncludeCharges;
        return ids.Where(id => _static.Types.TryGetValue(id, out var t) &&
                (t.CategoryId == Categories.Ship || t.CategoryId == Categories.Module ||
                 (includeCharges && t.CategoryId == Categories.Charge)))
            .ToList();
    }

    /// <summary>Drop type ids whose Jita prices are fresh (&lt;1 h old).</summary>
    public async Task<List<int>> FilterStaleAsync(List<int> typeIds, CancellationToken ct = default)
    {
        if (typeIds.Count == 0) return typeIds;
        using var scope = _scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDb>();
        var cutoff = DateTime.UtcNow.AddHours(-1);
        var fresh = (await db.Prices.AsNoTracking().Where(p => p.PricesUpdatedAt > cutoff)
            .Select(p => p.TypeId).ToListAsync(ct)).ToHashSet();
        return typeIds.Where(t => !fresh.Contains(t)).ToList();
    }

    public async Task RefreshPricesAsync(IReadOnlyList<int> typeIds, CancellationToken ct = default)
    {
        if (typeIds.Count == 0) return;
        var http = _httpFactory.CreateClient("fuzzwork");
        var now = DateTime.UtcNow;
        var updates = new System.Collections.Concurrent.ConcurrentDictionary<int, (double sell, double buy)>();

        await Parallel.ForEachAsync(typeIds.Chunk(BatchSize),
            new ParallelOptions { MaxDegreeOfParallelism = 3, CancellationToken = ct },
            async (batch, token) =>
            {
                var url = $"{FuzzworkAggregates}?station={EsiClient.Jita44StationId}&types={string.Join(',', batch)}";
                using var resp = await http.GetAsync(url, token);
                resp.EnsureSuccessStatusCode();
                using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync(token));
                foreach (var prop in doc.RootElement.EnumerateObject())
                {
                    if (!int.TryParse(prop.Name, out var tid)) continue;
                    var sell = ReadNum(prop.Value, "sell", "min");
                    var buy = ReadNum(prop.Value, "buy", "max");
                    updates[tid] = (sell, buy);
                }
            });

        using var scope = _scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDb>();
        await Data.BulkOps.UpsertPriceQuotesAsync(db,
            updates.Select(kv => (kv.Key, kv.Value.sell, kv.Value.buy)).ToList(), now, ct);
        _log.LogInformation("Prices refreshed for {Count} types", updates.Count);
    }

    private static double ReadNum(JsonElement el, string side, string field)
    {
        if (!el.TryGetProperty(side, out var s) || !s.TryGetProperty(field, out var f)) return 0;
        return f.ValueKind switch
        {
            JsonValueKind.Number => f.GetDouble(),
            JsonValueKind.String when double.TryParse(f.GetString(), NumberStyles.Any, CultureInfo.InvariantCulture, out var v) => v,
            _ => 0,
        };
    }

    /// <summary>Fetch previous-day traded volume for types whose history is stale (>20 h).</summary>
    public async Task RefreshVolumesAsync(IReadOnlyList<int> typeIds, int maxConcurrency = 8, CancellationToken ct = default)
    {
        List<int> stale;
        using (var scope = _scopes.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDb>();
            var cutoff = DateTime.UtcNow.AddHours(-20);
            var fresh = (await db.Prices.AsNoTracking()
                .Where(p => p.VolumeUpdatedAt > cutoff)
                .Select(p => p.TypeId).ToListAsync(ct)).ToHashSet();
            stale = typeIds.Where(t => !fresh.Contains(t)).ToList();
        }
        if (stale.Count == 0) return;
        _log.LogInformation("Fetching market history for {Count} types", stale.Count);

        var results = new System.Collections.Concurrent.ConcurrentDictionary<int, double>();
        await Parallel.ForEachAsync(stale, new ParallelOptions { MaxDegreeOfParallelism = maxConcurrency, CancellationToken = ct },
            async (tid, token) =>
            {
                try
                {
                    var resp = await _esi.GetAsync<List<EsiMarketHistoryDay>>(
                        $"/markets/{EsiClient.TheForgeRegionId}/history/?type_id={tid}", ct: token);
                    var last = resp.Data?.LastOrDefault();
                    results[tid] = last?.Volume ?? 0;
                }
                catch (HttpRequestException)
                {
                    // 400 = type not tracked on the market; record zero volume so the
                    // staleness window stops retrying it every cycle.
                    results[tid] = 0;
                }
            });

        using (var scope = _scopes.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDb>();
            await Data.BulkOps.UpsertPriceVolumesAsync(db,
                results.Select(kv => (kv.Key, kv.Value)).ToList(), DateTime.UtcNow, ct);
        }
    }
}
