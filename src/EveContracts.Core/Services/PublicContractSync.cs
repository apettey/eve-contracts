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
    private readonly StaticDataCache _static;
    private readonly ILogger<PublicContractSync> _log;

    public event Action? Updated;

    public PublicContractSync(IServiceScopeFactory scopes, EsiClient esi, PriceService prices,
        SettingsService settings, StaticDataCache staticData, ILogger<PublicContractSync> log)
    {
        _scopes = scopes;
        _esi = esi;
        _prices = prices;
        _settings = settings;
        _static = staticData;
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
        List<long> newIds;
        if (first.NotModified)
        {
            // Contract list unchanged — but a previous run may have left contracts
            // without items (crash/shutdown mid-fetch), so still drain the backlog.
            _log.LogInformation("Region {Region}: contracts unchanged (304)", regionId);
            using var scope = _scopes.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<AppDb>();
            newIds = await db.PublicContracts
                .Where(c => c.RegionId == regionId && !c.ItemsFetched && c.Type != "courier" && c.DateExpired > now)
                .Select(c => c.ContractId).ToListAsync(ct);
        }
        else
        {
            if (first.Data is not null) seen.AddRange(first.Data);

            if (first.Pages > 1)
            {
                var pageBag = new System.Collections.Concurrent.ConcurrentBag<List<EsiPublicContract>>();
                await Parallel.ForEachAsync(Enumerable.Range(2, first.Pages - 1),
                    new ParallelOptions { MaxDegreeOfParallelism = 8, CancellationToken = ct },
                    async (page, token) =>
                    {
                        var resp = await _esi.GetAsync<List<EsiPublicContract>>($"/contracts/public/{regionId}/?page={page}", ct: token);
                        if (resp.Data is not null) pageBag.Add(resp.Data);
                    });
                foreach (var pageData in pageBag) seen.AddRange(pageData);
            }
            _log.LogInformation("Region {Region}: {Count} public contracts over {Pages} pages in {Ms} ms",
                regionId, seen.Count, first.Pages, sw.ElapsedMilliseconds);

            newIds = await UpsertContractsAsync(regionId, seen, ct);
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
        }

        // Progressive: fetch/save/price/evaluate chunk by chunk.
        var done = 0;
        foreach (var chunk in newIds.Chunk(ItemChunkSize))
        {
            ct.ThrowIfCancellationRequested();
            await FetchItemsAsync(chunk, ItemConcurrency, ct);
            try
            {
                var chunkTypes = await GetTypeIdsForContractsAsync(chunk, ct);
                await _prices.RefreshPricesAsync(await _prices.FilterStaleAsync(chunkTypes, ct), ct);
                await EvaluateContractsAsync(chunk, ct);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // Pricing/evaluation of one chunk failing must not stop item fetching;
                // the post-fetch full pass covers these contracts.
                _log.LogWarning("Chunk price/evaluate failed: {Error}", ex.Message);
            }
            done += chunk.Length;
            if (done < newIds.Count)
                _log.LogInformation("Items: {Done}/{Total} contracts itemized at {Ms} ms", done, newIds.Count, sw.ElapsedMilliseconds);
            Updated?.Invoke();
        }
        if (newIds.Count > 0)
            _log.LogInformation("Items: all {Total} new contracts itemized in {Ms} ms total", newIds.Count, sw.ElapsedMilliseconds);

        // Volumes last (per-type ESI history is the slow tail, so only scoped types), then a full pass.
        var needed = await _prices.GetVolumeNeededTypeIdsAsync(ct);
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
        if (!_static.Ready) await _static.LoadAsync(ct);
        using var scope = _scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDb>();

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
            if (row.StartLocationId != 0 && _static.Stations.TryGetValue(row.StartLocationId, out var station))
            {
                row.StationName = station.Name;
                row.SolarSystemId = station.SolarSystemId;
            }
            else
            {
                row.StationName = "Player structure";
            }
            if (row.SolarSystemId != 0 && _static.Systems.TryGetValue(row.SolarSystemId, out var sys))
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
                try
                {
                    var resp = await _esi.GetAsync<List<EsiContractItem>>($"/contracts/public/items/{id}/", ct: token);
                    results[id] = resp.Data ?? [];
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    // Leave ItemsFetched=false so the next cycle retries this contract.
                    _log.LogWarning("Item fetch failed for contract {Id}: {Error}", id, ex.Message);
                }
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
    public Task EvaluateContractsAsync(IReadOnlyList<long> contractIds, CancellationToken ct = default) =>
        EvaluateCoreAsync(contractIds, ct);

    /// <summary>Re-run the evaluation pipeline over every live contract and persist verdicts.</summary>
    public Task EvaluateAllAsync(CancellationToken ct = default) => EvaluateCoreAsync(null, ct);

    private sealed record LeanContract(long ContractId, string Type, double Price, double Sec, int Jumps,
        DateTime Expired, string Title, string OldVerdict, double OldProfit, double OldSell, string OldFlags);

    private sealed record LeanItem(long ContractId, int TypeId, long Quantity, bool IsIncluded);

    /// <summary>
    /// Lean evaluation: untracked projections in, parallel pure evaluation, and only
    /// rows whose result actually changed are written back (prepared UPDATE, one tx).
    /// </summary>
    private async Task EvaluateCoreAsync(IReadOnlyList<long>? contractIds, CancellationToken ct)
    {
        if (contractIds is { Count: 0 }) return;
        if (!_static.Ready) await _static.LoadAsync(ct);
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var now = DateTime.UtcNow;

        var thresholds = new EvalThresholds(_settings.MinMarginPct, _settings.MinDailyVolume, _settings.MaxPrice,
            _settings.FeePct, _settings.HaulRate, _settings.HighsecOnly, _settings.IncludeAuctions, _settings.IncludeCharges);

        List<LeanContract> contracts;
        List<LeanItem> items;
        Dictionary<int, Price> prices;
        Dictionary<int, ItemSetting> itemSettings;
        using (var scope = _scopes.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDb>();
            var live = db.PublicContracts.AsNoTracking().Where(c => c.DateExpired > now && c.ItemsFetched);
            if (contractIds is not null) live = live.Where(c => contractIds.Contains(c.ContractId));

            contracts = await live.Select(c => new LeanContract(c.ContractId, c.Type, c.Price, c.SecurityStatus,
                c.JumpsToJita, c.DateExpired, c.Title, c.Verdict, c.NetProfit, c.JitaSellValue, c.FlagsJson)).ToListAsync(ct);

            items = await live
                .Join(db.ContractItems.AsNoTracking(), C => C.ContractId, I => I.ContractId,
                    (C, I) => new LeanItem(I.ContractId, I.TypeId, I.Quantity, I.IsIncluded))
                .ToListAsync(ct);

            // Prices holds only types seen in contracts — small; load whole table, no giant IN().
            prices = await db.Prices.AsNoTracking().ToDictionaryAsync(p => p.TypeId, ct);
            itemSettings = await db.ItemSettings.AsNoTracking().ToDictionaryAsync(s => s.TypeId, ct);
        }

        var itemsByContract = items.ToLookup(i => i.ContractId);
        var types = _static.Types;

        var results = new (long Id, EvalResult R)[contracts.Count];
        Parallel.For(0, contracts.Count, idx =>
        {
            var c = contracts[idx];
            var evalItems = itemsByContract[c.ContractId].Select(i =>
            {
                types.TryGetValue(i.TypeId, out var t);
                prices.TryGetValue(i.TypeId, out var p);
                itemSettings.TryGetValue(i.TypeId, out var s);
                return new EvalItem(i.TypeId, t?.Name ?? $"Type {i.TypeId}", i.Quantity, i.IsIncluded,
                    t?.CategoryId ?? 0, t?.PackagedVolume ?? 0,
                    p?.JitaSell ?? 0, p?.JitaBuy ?? 0, p?.PrevDayVolume ?? 0,
                    s?.MinDailyVolumeOverride, s?.Excluded ?? false);
            }).ToList();

            results[idx] = (c.ContractId, EvaluationService.Evaluate(
                new EvalInput(c.Type, c.Price, c.Sec, c.Jumps, c.Expired, c.Title, evalItems), thresholds));
        });

        var changed = new List<(long, EvalResult)>(contracts.Count);
        for (var idx = 0; idx < contracts.Count; idx++)
        {
            var c = contracts[idx];
            var r = results[idx].R;
            if (r.Verdict != c.OldVerdict || Math.Abs(r.NetProfit - c.OldProfit) > 1
                || Math.Abs(r.JitaSellValue - c.OldSell) > 1 || r.FlagsJson != c.OldFlags)
                changed.Add((c.ContractId, r));
        }

        if (changed.Count > 0)
        {
            using var scope = _scopes.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<AppDb>();
            await BulkOps.UpdateEvaluationsAsync(db, changed, ct);
        }
        if (contractIds is null)
            _log.LogInformation("Evaluated {Count} live contracts ({Changed} changed) in {Ms} ms",
                contracts.Count, changed.Count, sw.ElapsedMilliseconds);
    }
}
