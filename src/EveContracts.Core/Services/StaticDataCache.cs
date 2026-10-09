using EveContracts.Core.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace EveContracts.Core.Services;

public record TypeInfo(string Name, int CategoryId, double PackagedVolume, bool IsRig = false);
public record SystemInfo(string Name, double Security, int JumpsToJita);
public record StationInfo(string Name, int SolarSystemId);

/// <summary>
/// In-memory snapshot of the SDE tables. Static data changes only on game patches
/// (when SdeService reloads it and refreshes this cache), so every hot path reads
/// from these dictionaries instead of re-querying SQLite per scan/evaluation.
/// ~60k entries, a few MB.
/// </summary>
public class StaticDataCache
{
    private readonly IServiceScopeFactory _scopes;
    private readonly ILogger<StaticDataCache> _log;

    public IReadOnlyDictionary<int, TypeInfo> Types { get; private set; } = new Dictionary<int, TypeInfo>();
    public IReadOnlyDictionary<int, SystemInfo> Systems { get; private set; } = new Dictionary<int, SystemInfo>();
    public IReadOnlyDictionary<long, StationInfo> Stations { get; private set; } = new Dictionary<long, StationInfo>();
    public IReadOnlyDictionary<int, string> Regions { get; private set; } = new Dictionary<int, string>();
    // Trade-hub ids are fixed, so they resolve even before region names are loaded.
    private static readonly IReadOnlyDictionary<string, int> HubRegionIds = new Dictionary<string, int>
    {
        ["The Forge"] = 10000002, ["Domain"] = 10000043, ["Sinq Laison"] = 10000032,
        ["Heimatar"] = 10000030, ["Metropolis"] = 10000042,
    };
    private IReadOnlyDictionary<string, int> _regionIds = HubRegionIds;

    /// <summary>Region picker order: All regions, the trade hubs, then everything else A–Z.</summary>
    public IReadOnlyList<string> RegionChoices { get; private set; } = [Sde.SdeService.AllRegions, .. Sde.SdeService.TradeHubRegions];

    /// <summary>Region id for a picker name; 0 means all regions. Unknown names fall back to The Forge.</summary>
    public int ResolveRegion(string name) =>
        name == Sde.SdeService.AllRegions ? 0
        : _regionIds.TryGetValue(name, out var id) ? id
        : Esi.EsiClient.TheForgeRegionId;

    public string RegionName(int regionId) => Regions.TryGetValue(regionId, out var n) ? n : "";
    public bool Ready => Types.Count > 0;

    public StaticDataCache(IServiceScopeFactory scopes, ILogger<StaticDataCache> log)
    {
        _scopes = scopes;
        _log = log;
    }

    public async Task LoadAsync(CancellationToken ct = default)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        using var scope = _scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDb>();
        Types = await db.ItemTypes.AsNoTracking()
            .ToDictionaryAsync(t => t.TypeId, t => new TypeInfo(t.Name, t.CategoryId, t.PackagedVolume, t.IsRig), ct);
        Systems = await db.SolarSystems.AsNoTracking()
            .ToDictionaryAsync(s => s.SolarSystemId, s => new SystemInfo(s.Name, s.Security, s.JumpsToJita), ct);
        Stations = await db.Stations.AsNoTracking()
            .ToDictionaryAsync(s => s.StationId, s => new StationInfo(s.Name, s.SolarSystemId), ct);
        var regions = await db.Regions.AsNoTracking().ToDictionaryAsync(r => r.RegionId, r => r.Name, ct);
        Regions = regions;
        _regionIds = regions.Count > 0 ? regions.ToDictionary(kv => kv.Value, kv => kv.Key) : HubRegionIds;
        RegionChoices = [Sde.SdeService.AllRegions, .. Sde.SdeService.TradeHubRegions,
            .. regions.Values.Except(Sde.SdeService.TradeHubRegions).Order(StringComparer.OrdinalIgnoreCase)];
        _log.LogInformation("Static cache loaded: {Types} types, {Systems} systems, {Stations} stations in {Ms} ms",
            Types.Count, Systems.Count, Stations.Count, sw.ElapsedMilliseconds);
    }
}
