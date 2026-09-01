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
    private readonly ILogger<PriceService> _log;

    public PriceService(IServiceScopeFactory scopes, IHttpClientFactory httpFactory, EsiClient esi, ILogger<PriceService> log)
    {
        _scopes = scopes;
        _httpFactory = httpFactory;
        _esi = esi;
        _log = log;
    }

    /// <summary>Type ids that appear in any cached contract (the only ones worth pricing).</summary>
    public async Task<List<int>> GetNeededTypeIdsAsync(CancellationToken ct = default)
    {
        using var scope = _scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDb>();
        return await db.ContractItems.Select(i => i.TypeId).Distinct().ToListAsync(ct);
    }

    /// <summary>Drop type ids whose Jita prices are fresh (&lt;1 h old).</summary>
    public async Task<List<int>> FilterStaleAsync(List<int> typeIds, CancellationToken ct = default)
    {
        if (typeIds.Count == 0) return typeIds;
        using var scope = _scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDb>();
        var cutoff = DateTime.UtcNow.AddHours(-1);
        var fresh = await db.Prices.Where(p => typeIds.Contains(p.TypeId) && p.PricesUpdatedAt > cutoff)
            .Select(p => p.TypeId).ToListAsync(ct);
        return typeIds.Except(fresh).ToList();
    }

    public async Task RefreshPricesAsync(IReadOnlyList<int> typeIds, CancellationToken ct = default)
    {
        if (typeIds.Count == 0) return;
        var http = _httpFactory.CreateClient("fuzzwork");
        var now = DateTime.UtcNow;
        var updates = new Dictionary<int, (double sell, double buy)>();

        foreach (var batch in typeIds.Chunk(BatchSize))
        {
            ct.ThrowIfCancellationRequested();
            var url = $"{FuzzworkAggregates}?station={EsiClient.Jita44StationId}&types={string.Join(',', batch)}";
            using var resp = await http.GetAsync(url, ct);
            resp.EnsureSuccessStatusCode();
            using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync(ct));
            foreach (var prop in doc.RootElement.EnumerateObject())
            {
                if (!int.TryParse(prop.Name, out var tid)) continue;
                var sell = ReadNum(prop.Value, "sell", "min");
                var buy = ReadNum(prop.Value, "buy", "max");
                updates[tid] = (sell, buy);
            }
        }

        using var scope = _scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDb>();
        var existing = await db.Prices.Where(p => typeIds.Contains(p.TypeId)).ToDictionaryAsync(p => p.TypeId, ct);
        foreach (var (tid, (sell, buy)) in updates)
        {
            if (existing.TryGetValue(tid, out var row))
            {
                row.JitaSell = sell; row.JitaBuy = buy; row.PricesUpdatedAt = now;
            }
            else
            {
                db.Prices.Add(new Price { TypeId = tid, JitaSell = sell, JitaBuy = buy, PricesUpdatedAt = now });
            }
        }
        await db.SaveChangesAsync(ct);
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
            var fresh = await db.Prices
                .Where(p => typeIds.Contains(p.TypeId) && p.VolumeUpdatedAt > cutoff)
                .Select(p => p.TypeId).ToListAsync(ct);
            stale = typeIds.Except(fresh).ToList();
        }
        if (stale.Count == 0) return;
        _log.LogInformation("Fetching market history for {Count} types", stale.Count);

        var results = new System.Collections.Concurrent.ConcurrentDictionary<int, double>();
        await Parallel.ForEachAsync(stale, new ParallelOptions { MaxDegreeOfParallelism = maxConcurrency, CancellationToken = ct },
            async (tid, token) =>
            {
                var resp = await _esi.GetAsync<List<EsiMarketHistoryDay>>(
                    $"/markets/{EsiClient.TheForgeRegionId}/history/?type_id={tid}", ct: token);
                var last = resp.Data?.LastOrDefault();
                results[tid] = last?.Volume ?? 0;
            });

        using (var scope = _scopes.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDb>();
            var now = DateTime.UtcNow;
            var rows = await db.Prices.Where(p => stale.Contains(p.TypeId)).ToDictionaryAsync(p => p.TypeId, ct);
            foreach (var (tid, vol) in results)
            {
                if (rows.TryGetValue(tid, out var row)) { row.PrevDayVolume = vol; row.VolumeUpdatedAt = now; }
                else db.Prices.Add(new Price { TypeId = tid, PrevDayVolume = vol, VolumeUpdatedAt = now });
            }
            await db.SaveChangesAsync(ct);
        }
    }
}
