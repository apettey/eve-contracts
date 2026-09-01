using System.Net.Http.Json;
using EveContracts.Core.Data;
using EveContracts.Core.Esi;
using EveContracts.Core.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace EveContracts.Core.Services;

/// <summary>Polls /characters/{id}/contracts/ for every authed character every 5–10 min.</summary>
public class OwnContractSync
{
    private readonly IServiceScopeFactory _scopes;
    private readonly EsiClient _esi;
    private readonly EsiAuthService _auth;
    private readonly IHttpClientFactory _httpFactory;
    private readonly ILogger<OwnContractSync> _log;

    public event Action? Updated;

    /// <summary>Fired when a sync discovers contracts newly assigned to our characters (count).</summary>
    public event Action<int>? NewInboundContracts;

    public OwnContractSync(IServiceScopeFactory scopes, EsiClient esi, EsiAuthService auth,
        IHttpClientFactory httpFactory, ILogger<OwnContractSync> log)
    {
        _scopes = scopes;
        _esi = esi;
        _auth = auth;
        _httpFactory = httpFactory;
        _log = log;
    }

    public async Task SyncAllAsync(CancellationToken ct = default)
    {
        List<Character> chars;
        using (var scope = _scopes.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDb>();
            chars = await db.Characters.AsNoTracking().ToListAsync(ct);
        }
        var newInbound = 0;
        foreach (var ch in chars)
        {
            ct.ThrowIfCancellationRequested();
            try { newInbound += await SyncCharacterAsync(ch, ct); }
            catch (Exception ex) { _log.LogWarning("Own-contract sync failed for {Name}: {Error}", ch.Name, ex.Message); }
        }
        if (newInbound > 0)
        {
            _log.LogInformation("{Count} new inbound contract(s) received", newInbound);
            NewInboundContracts?.Invoke(newInbound);
        }
        Updated?.Invoke();
    }

    /// <summary>Returns how many previously unseen inbound contracts this sync found.</summary>
    private async Task<int> SyncCharacterAsync(Character ch, CancellationToken ct)
    {
        var token = await _auth.GetAccessTokenAsync(ch.CharacterId, ct);
        if (token is null) return 0;

        var all = new List<EsiCharacterContract>();
        var first = await _esi.GetAsync<List<EsiCharacterContract>>($"/characters/{ch.CharacterId}/contracts/?page=1", accessToken: token, ct: ct);
        if (first.Data is not null) all.AddRange(first.Data);
        for (var page = 2; page <= first.Pages; page++)
        {
            var resp = await _esi.GetAsync<List<EsiCharacterContract>>($"/characters/{ch.CharacterId}/contracts/?page={page}", accessToken: token, ct: ct);
            if (resp.Data is not null) all.AddRange(resp.Data);
        }

        // Resolve counterparty names via /universe/names/
        var partyIds = all.SelectMany(c => new[] { c.IssuerId, c.AssigneeId, c.AcceptorId })
            .Where(id => id > 0 && id != ch.CharacterId).Distinct().ToList();
        var names = await ResolveNamesAsync(partyIds, ct);

        var stationNames = await GetStationLookupAsync(ct);

        using var scope = _scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDb>();
        var known = await db.OwnContracts.Where(o => o.CharacterId == ch.CharacterId)
            .ToDictionaryAsync(o => o.ContractId, ct);

        // Only alert on inbound contracts found after this character's first-ever sync —
        // the initial backfill of historical contracts must not fire a wall of sounds.
        var isFirstSync = ch.LastSync == default;
        var newInbound = 0;
        foreach (var c in all)
        {
            var isIssuer = c.IssuerId == ch.CharacterId;
            if (!known.TryGetValue(c.ContractId, out var row))
            {
                row = new OwnContract { ContractId = c.ContractId, CharacterId = ch.CharacterId };
                db.OwnContracts.Add(row);
                if (!isIssuer && !isFirstSync) newInbound++;
            }
            row.CharacterName = ch.Name;
            row.Direction = isIssuer ? "OUT" : "IN";
            row.Type = c.Type;
            row.Title = string.IsNullOrWhiteSpace(c.Title) ? "(no title)" : c.Title!;
            row.Status = c.Status;
            row.Price = c.Price ?? 0;
            row.Reward = c.Reward ?? 0;
            row.Collateral = c.Collateral ?? 0;
            row.DateIssued = c.DateIssued;
            row.DateExpired = c.DateExpired;
            row.DateCompleted = c.DateCompleted;
            row.VolumeM3 = c.Volume ?? 0;
            row.DaysToComplete = c.DaysToComplete ?? 0;
            row.Buyout = c.Buyout ?? 0;
            var otherId = isIssuer ? (c.AcceptorId != 0 ? c.AcceptorId : c.AssigneeId) : c.IssuerId;
            row.OtherParty = otherId == 0 ? "Public" : names.GetValueOrDefault(otherId, $"#{otherId}");
            var startName = c.StartLocationId is long sl ? stationNames.GetValueOrDefault(sl, "Structure") : "?";
            if (c.Type == "courier" && c.EndLocationId is long el)
                row.Route = $"{startName} → {stationNames.GetValueOrDefault(el, "Structure")}";
            else
                row.Route = startName;
        }
        await db.SaveChangesAsync(ct);
        var last = await db.Characters.FindAsync([ch.CharacterId], ct);
        if (last is not null) { last.LastSync = DateTime.UtcNow; await db.SaveChangesAsync(ct); }
        return newInbound;
    }

    private async Task<Dictionary<long, string>> GetStationLookupAsync(CancellationToken ct)
    {
        using var scope = _scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDb>();
        // System-short names: "Jita IV - Moon 4 - Caldari Navy Assembly Plant" → keep first 2 segments
        return await db.Stations.AsNoTracking()
            .ToDictionaryAsync(s => s.StationId, s => Shorten(s.Name), ct);
    }

    private static string Shorten(string stationName)
    {
        var parts = stationName.Split(" - ");
        return parts.Length > 1 ? parts[0] : stationName;
    }

    /// <summary>
    /// Lazily fetch the item list for one own contract (first click in the UI).
    /// Contract contents are immutable, so once fetched they are cached for good.
    /// Returns false when items could not be fetched (expired token, ESI error).
    /// </summary>
    public async Task<bool> EnsureItemsAsync(long ownRowId, CancellationToken ct = default)
    {
        int characterId;
        long contractId;
        using (var scope = _scopes.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDb>();
            var row = await db.OwnContracts.AsNoTracking().FirstOrDefaultAsync(o => o.Id == ownRowId, ct);
            if (row is null) return false;
            if (row.ItemsFetched) return true;
            characterId = row.CharacterId;
            contractId = row.ContractId;
        }

        var token = await _auth.GetAccessTokenAsync(characterId, ct);
        if (token is null) return false;

        List<EsiContractItem> items;
        try
        {
            var resp = await _esi.GetAsync<List<EsiContractItem>>(
                $"/characters/{characterId}/contracts/{contractId}/items/", accessToken: token, ct: ct);
            items = resp.Data ?? [];
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _log.LogWarning("Own-contract item fetch failed for {Id}: {Error}", contractId, ex.Message);
            return false;
        }

        using (var scope = _scopes.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDb>();
            foreach (var it in items)
            {
                db.OwnContractItems.Add(new OwnContractItem
                {
                    OwnContractId = ownRowId,
                    TypeId = it.TypeId,
                    Quantity = it.Quantity,
                    IsIncluded = it.IsIncluded ?? true,
                });
            }
            var row = await db.OwnContracts.FirstOrDefaultAsync(o => o.Id == ownRowId, ct);
            if (row is not null) row.ItemsFetched = true;
            await db.SaveChangesAsync(ct);
        }
        return true;
    }

    private async Task<Dictionary<int, string>> ResolveNamesAsync(List<int> ids, CancellationToken ct)
    {
        var result = new Dictionary<int, string>();
        if (ids.Count == 0) return result;
        var http = _httpFactory.CreateClient("esi");
        foreach (var chunk in ids.Chunk(1000))
        {
            try
            {
                using var resp = await http.PostAsJsonAsync($"{EsiClient.BaseUrl}/universe/names/", chunk, ct);
                if (!resp.IsSuccessStatusCode) continue;
                var names = System.Text.Json.JsonSerializer.Deserialize<List<EsiName>>(
                    await resp.Content.ReadAsStringAsync(ct), EsiClient.JsonOpts) ?? [];
                foreach (var n in names) result[n.Id] = n.Name;
            }
            catch (Exception) { /* names are cosmetic; ids shown as fallback */ }
        }
        return result;
    }
}
