using EveContracts.Core.Data;
using EveContracts.Core.Esi;
using EveContracts.Core.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace EveContracts.Core.Services;

/// <summary>
/// Polls public contracts for the enabled region, upserts them, fetches items
/// only for never-seen contract ids (ESI error limits bite otherwise), then
/// re-runs profit evaluation over all live contracts.
///
/// Item fetching is progressive: chunks of contracts are fetched, saved,
/// priced and evaluated immediately, so the UI fills in during a long first
/// run instead of staying empty until the whole region is itemized.
/// </summary>
public class PublicContractSync
{
    private const int ItemChunkSize = 500;
    private const int ItemConcurrency = 8;

    private readonly IServiceScopeFactory _scopes;
    private readonly EsiClient _esi;
    private readonly PriceService _prices;
    private readonly SettingsService _settings;
    private readonly ILogger<PublicContractSync> _log;

    public event Action? Updated;

    public PublicContractSync(IServiceScopeFactory scopes, EsiClient esi, PriceService prices,
        SettingsService settings, ILogger<PublicContractSync> log)
    {
        _scopes = scopes;
        _esi = esi;
        _prices = prices;
        _settings = settings;
        _log = log;
    }

    public async Task SyncRegionAsync(int regionId, CancellationToken ct = default)
    {
        var now = DateTime.UtcNow;
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var seen = new List<EsiPublicContract>();

        // Page 1 with ETag; if 304, region contracts unchanged — still re-evaluate (prices may have moved).
        var page1Url = $"/contracts/public/{regionId}/?page=1";
        string? etag = null;
        using (var scope = _scopes.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDb>();
            etag = (await db.EsiEtags.FindAsync([page1Url], ct))?.Etag;
        }

        var first = await _esi.GetAsync<List<EsiPublicContract>>(page1Url, etag, ct: ct);
        if (first.NotModified)
        {
            _log.LogInformation("Region {Region}: contracts unchanged (304)", regionId);
            await EvaluateAllAsync(ct);
            Updated?.Invoke();
            return;
        }
        if (first.Data is not null) seen.AddRange(first.Data);

        for (var page = 2; page <= first.Pages; page++)
        {
            ct.ThrowIfCancellationRequested();
            var resp = await _esi.GetAsync<List<EsiPublicContract>>($"/contracts/public/{regionId}/?page={page}", ct: ct);
            if (resp.Data is not null) seen.AddRange(resp.Data);
        }
        _log.LogInformation("Region {Region}: {Count} public contracts over {Pages} pages in {Ms} ms",
            regionId, seen.Count, first.Pages, sw.ElapsedMilliseconds);

        var newIds = await UpsertContractsAsync(regionId, seen, ct);
        _log.LogInformation("Region {Region}: upsert done at {Ms} ms", regionId, sw.ElapsedMilliseconds);

        if (first.Etag is not null)
        {
            using var scope = _scopes.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<AppDb>();
            var etagRow = await db.EsiEtags.FindAsync([page1Url], ct);
            if (etagRow is null) db.EsiEtags.Add(new EsiEtag { Url = page1Url, Etag = first.Etag, UpdatedAt = now });
            else { etagRow.Etag = first.Etag; etagRow.UpdatedAt = now; }
            await db.SaveChangesAsync(ct);
        }

        // Progressive: fetch/save/price/evaluate chunk by chunk.
        var done = 0;
        foreach (var chunk in newIds.Chunk(ItemChunkSize))
        {
            ct.ThrowIfCancellationRequested();
            await FetchItemsAsync(chunk, ItemConcurrency, ct);
            var chunkTypes = await GetTypeIdsForContractsAsync(chunk, ct);
            await _prices.RefreshPricesAsync(await _prices.FilterStaleAsync(chunkTypes, ct), ct);
            await EvaluateContractsAsync(chunk, ct);
            done += chunk.Length;
            if (done < newIds.Count)
                _log.LogInformation("Items: {Done}/{Total} contracts itemized at {Ms} ms", done, newIds.Count, sw.ElapsedMilliseconds);
            Updated?.Invoke();
        }
        if (newIds.Count > 0)
            _log.LogInformation("Items: all {Total} new contracts itemized in {Ms} ms total", newIds.Count, sw.ElapsedMilliseconds);

        // Volumes last (per-type ESI history is the slow tail), then a full pass.
        var needed = await _prices.GetNeededTypeIdsAsync(ct);
        await _prices.RefreshVolumesAsync(needed, ct: ct);
        await EvaluateAllAsync(ct);
        await _settings.SetLastPublicScanAsync(now, ct);
        _log.LogInformation("Region {Region}: full sync finished in {Ms} ms", regionId, sw.ElapsedMilliseconds);
        Updated?.Invoke();
    }

    /// <summary>
    /// Upsert one scan's contracts via prepared raw-SQL statements (no change tracking).
    /// FirstSeen/ItemsFetched/evaluation columns are preserved for known ids.
    /// Returns the live contract ids that still need their items fetched.
    /// </summary>
    public async Task<List<long>> UpsertContractsAsync(int regionId, IReadOnlyList<EsiPublicContract> seen, CancellationToken ct = default)
    {
        var now = DateTime.UtcNow;
        using var scope = _scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDb>();
        var systems = await db.SolarSystems.AsNoTracking().ToDictionaryAsync(s => s.SolarSystemId, ct);
        var stations = await db.Stations.AsNoTracking().ToDictionaryAsync(s => s.StationId, ct);

        var rows = new List<PublicContract>(seen.Count);
        foreach (var c in seen)
        {
            var row = new PublicContract
            {
                ContractId = c.ContractId,
                RegionId = regionId,
                FirstSeen = now,
                LastSeen = now,
                Type = c.Type,
                Title = c.Title ?? "",
                Price = c.Price ?? 0,
                StartLocationId = c.StartLocationId ?? 0,
                DateIssued = c.DateIssued,
                DateExpired = c.DateExpired,
                VolumeM3 = c.Volume ?? 0,
            };
            if (row.StartLocationId != 0 && stations.TryGetValue(row.StartLocationId, out var station))
            {
                row.StationName = station.Name;
                row.SolarSystemId = station.SolarSystemId;
            }
            else
            {
                row.StationName = "Player structure";
            }
            if (row.SolarSystemId != 0 && systems.TryGetValue(row.SolarSystemId, out var sys))
            {
                row.SystemName = sys.Name;
                row.SecurityStatus = sys.Security;
                row.JumpsToJita = sys.JumpsToJita;
            }
            rows.Add(row);
        }

        await BulkOps.UpsertPublicContractsAsync(db, rows, ct);

        return await db.PublicContracts
            .Where(c => c.RegionId == regionId && !c.ItemsFetched && c.Type != "courier" && c.DateExpired > now)
            .Select(c => c.ContractId).ToListAsync(ct);
    }

    private async Task<List<int>> GetTypeIdsForContractsAsync(IReadOnlyList<long> contractIds, CancellationToken ct)
    {
        using var scope = _scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDb>();
        return await db.ContractItems.Where(i => contractIds.Contains(i.ContractId))
            .Select(i => i.TypeId).Distinct().ToListAsync(ct);
    }

    /// <summary>Fetch item lists for contracts we have never itemized. Never re-fetches known ids.</summary>
    public async Task FetchItemsAsync(IReadOnlyList<long> contractIds, int maxConcurrency = ItemConcurrency, CancellationToken ct = default)
    {
        if (contractIds.Count == 0) return;
        var results = new System.Collections.Concurrent.ConcurrentDictionary<long, List<EsiContractItem>>();

        await Parallel.ForEachAsync(contractIds, new ParallelOptions { MaxDegreeOfParallelism = maxConcurrency, CancellationToken = ct },
            async (id, token) =>
            {
                var resp = await _esi.GetAsync<List<EsiContractItem>>($"/contracts/public/items/{id}/", ct: token);
                results[id] = resp.Data ?? [];
            });

        await SaveItemsAsync(results.ToDictionary(
            kv => kv.Key,
            kv => kv.Value.Select(it => new ContractItem
            {
                ContractId = kv.Key,
                TypeId = it.TypeId,
                Quantity = it.Quantity,
                IsIncluded = it.IsIncluded ?? true,
            }).ToList()), ct);
    }

    public async Task SaveItemsAsync(Dictionary<long, List<ContractItem>> itemsByContract, CancellationToken ct = default)
    {
        using var scope = _scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDb>();
        await BulkOps.InsertContractItemsAsync(db, itemsByContract, ct);
    }

    /// <summary>Evaluate a specific set of contracts (progressive chunks during item fetch).</summary>
    public async Task EvaluateContractsAsync(IReadOnlyList<long> contractIds, CancellationToken ct = default)
    {
        if (contractIds.Count == 0) return;
        using var scope = _scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDb>();
        var contracts = await db.PublicContracts
            .Where(c => contractIds.Contains(c.ContractId) && c.ItemsFetched)
            .Include(c => c.Items)
            .ToListAsync(ct);
        await EvaluateCoreAsync(db, contracts, ct);
    }

    /// <summary>Re-run the evaluation pipeline over every live contract and persist verdicts.</summary>
    public async Task EvaluateAllAsync(CancellationToken ct = default)
    {
        using var scope = _scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDb>();
        var now = DateTime.UtcNow;
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var contracts = await db.PublicContracts
            .Where(c => c.DateExpired > now && c.ItemsFetched)
            .Include(c => c.Items)
            .ToListAsync(ct);
        await EvaluateCoreAsync(db, contracts, ct);
        _log.LogInformation("Evaluated {Count} live contracts in {Ms} ms", contracts.Count, sw.ElapsedMilliseconds);
    }

    private async Task EvaluateCoreAsync(AppDb db, List<PublicContract> contracts, CancellationToken ct)
    {
        var thresholds = new EvalThresholds(_settings.MinMarginPct, _settings.MinDailyVolume, _settings.MaxPrice,
            _settings.FeePct, _settings.HaulRate, _settings.HighsecOnly, _settings.IncludeAuctions, _settings.IncludeCharges);

        var typeIds = contracts.SelectMany(c => c.Items.Select(i => i.TypeId)).Distinct().ToList();
        var prices = await db.Prices.Where(p => typeIds.Contains(p.TypeId)).ToDictionaryAsync(p => p.TypeId, ct);
        var types = await db.ItemTypes.Where(t => typeIds.Contains(t.TypeId)).ToDictionaryAsync(t => t.TypeId, ct);
        var itemSettings = await db.ItemSettings.ToDictionaryAsync(s => s.TypeId, ct);

        foreach (var c in contracts)
        {
            var items = c.Items.Select(i =>
            {
                types.TryGetValue(i.TypeId, out var t);
                prices.TryGetValue(i.TypeId, out var p);
                itemSettings.TryGetValue(i.TypeId, out var s);
                return new EvalItem(i.TypeId, t?.Name ?? $"Type {i.TypeId}", i.Quantity, i.IsIncluded,
                    t?.CategoryId ?? 0, t?.PackagedVolume ?? 0,
                    p?.JitaSell ?? 0, p?.JitaBuy ?? 0, p?.PrevDayVolume ?? 0,
                    s?.MinDailyVolumeOverride, s?.Excluded ?? false);
            }).ToList();

            var result = EvaluationService.Evaluate(
                new EvalInput(c.Type, c.Price, c.SecurityStatus, c.JumpsToJita, c.DateExpired, c.Title, items),
                thresholds);

            c.JitaSellValue = result.JitaSellValue;
            c.Fees = result.Fees;
            c.Hauling = result.Hauling;
            c.NetProfit = result.NetProfit;
            c.Margin = result.Margin;
            c.Verdict = result.Verdict;
            c.FlagsJson = result.FlagsJson;
        }
        await db.SaveChangesAsync(ct);
    }
}
