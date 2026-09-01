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
/// </summary>
public class PublicContractSync
{
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
        _log.LogInformation("Region {Region}: {Count} public contracts over {Pages} pages", regionId, seen.Count, first.Pages);

        List<long> newIds;
        using (var scope = _scopes.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDb>();
            var systems = await db.SolarSystems.ToDictionaryAsync(s => s.SolarSystemId, ct);
            var stations = await db.Stations.ToDictionaryAsync(s => s.StationId, ct);

            var known = await db.PublicContracts.Where(c => c.RegionId == regionId)
                .ToDictionaryAsync(c => c.ContractId, ct);

            foreach (var c in seen)
            {
                if (!known.TryGetValue(c.ContractId, out var row))
                {
                    row = new PublicContract { ContractId = c.ContractId, RegionId = regionId, FirstSeen = now };
                    db.PublicContracts.Add(row);
                }
                row.Type = c.Type;
                row.Title = c.Title ?? "";
                row.Price = c.Price ?? 0;
                row.StartLocationId = c.StartLocationId ?? 0;
                row.DateIssued = c.DateIssued;
                row.DateExpired = c.DateExpired;
                row.VolumeM3 = c.Volume ?? 0;
                row.LastSeen = now;

                if (row.StartLocationId != 0 && stations.TryGetValue(row.StartLocationId, out var station))
                {
                    row.StationName = station.Name;
                    row.SolarSystemId = station.SolarSystemId;
                }
                else if (row.SolarSystemId == 0)
                {
                    row.StationName = "Player structure";
                }
                if (row.SolarSystemId != 0 && systems.TryGetValue(row.SolarSystemId, out var sys))
                {
                    row.SystemName = sys.Name;
                    row.SecurityStatus = sys.Security;
                    row.JumpsToJita = sys.JumpsToJita;
                }

                known[c.ContractId] = row;
            }

            if (first.Etag is not null)
            {
                var etagRow = await db.EsiEtags.FindAsync([page1Url], ct);
                if (etagRow is null) db.EsiEtags.Add(new EsiEtag { Url = page1Url, Etag = first.Etag, UpdatedAt = now });
                else { etagRow.Etag = first.Etag; etagRow.UpdatedAt = now; }
            }
            await db.SaveChangesAsync(ct);

            newIds = await db.PublicContracts
                .Where(c => c.RegionId == regionId && !c.ItemsFetched && c.Type != "courier")
                .Select(c => c.ContractId).ToListAsync(ct);
        }

        await FetchItemsAsync(newIds, ct: ct);

        // Prices for any types we haven't priced yet, then evaluate.
        var needed = await _prices.GetNeededTypeIdsAsync(ct);
        await _prices.RefreshPricesAsync(await FilterUnpricedAsync(needed, ct), ct);
        await _prices.RefreshVolumesAsync(needed, ct: ct);
        await EvaluateAllAsync(ct);
        await _settings.SetLastPublicScanAsync(now, ct);
        Updated?.Invoke();
    }

    private async Task<List<int>> FilterUnpricedAsync(List<int> typeIds, CancellationToken ct)
    {
        using var scope = _scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDb>();
        var cutoff = DateTime.UtcNow.AddHours(-1);
        var fresh = await db.Prices.Where(p => typeIds.Contains(p.TypeId) && p.PricesUpdatedAt > cutoff)
            .Select(p => p.TypeId).ToListAsync(ct);
        return typeIds.Except(fresh).ToList();
    }

    /// <summary>Fetch item lists for contracts we have never itemized. Never re-fetches known ids.</summary>
    public async Task FetchItemsAsync(IReadOnlyList<long> contractIds, int maxConcurrency = 6, CancellationToken ct = default)
    {
        if (contractIds.Count == 0) return;
        _log.LogInformation("Fetching items for {Count} new contracts", contractIds.Count);
        var results = new System.Collections.Concurrent.ConcurrentDictionary<long, List<EsiContractItem>>();

        await Parallel.ForEachAsync(contractIds, new ParallelOptions { MaxDegreeOfParallelism = maxConcurrency, CancellationToken = ct },
            async (id, token) =>
            {
                var resp = await _esi.GetAsync<List<EsiContractItem>>($"/contracts/public/items/{id}/", ct: token);
                results[id] = resp.Data ?? [];
            });

        using var scope = _scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDb>();
        var ids = results.Keys.ToList();
        var rows = await db.PublicContracts.Where(c => ids.Contains(c.ContractId)).ToDictionaryAsync(c => c.ContractId, ct);
        foreach (var (id, items) in results)
        {
            if (!rows.TryGetValue(id, out var row)) continue;
            row.ItemsFetched = true;
            foreach (var it in items)
            {
                db.ContractItems.Add(new ContractItem
                {
                    ContractId = id,
                    TypeId = it.TypeId,
                    Quantity = it.Quantity,
                    IsIncluded = it.IsIncluded ?? true,
                });
            }
        }
        await db.SaveChangesAsync(ct);
    }

    /// <summary>Re-run the evaluation pipeline over every live contract and persist verdicts.</summary>
    public async Task EvaluateAllAsync(CancellationToken ct = default)
    {
        using var scope = _scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDb>();
        var now = DateTime.UtcNow;

        var thresholds = new EvalThresholds(_settings.MinMarginPct, _settings.MinDailyVolume, _settings.MaxPrice,
            _settings.FeePct, _settings.HaulRate, _settings.HighsecOnly, _settings.IncludeAuctions, _settings.IncludeCharges);

        var contracts = await db.PublicContracts
            .Where(c => c.DateExpired > now && c.ItemsFetched)
            .Include(c => c.Items)
            .ToListAsync(ct);

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
        _log.LogInformation("Evaluated {Count} live contracts", contracts.Count);
    }
}
