using EveContracts.Core.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace EveContracts.Core.Services;

public record TypeInfo(string Name, int CategoryId, double PackagedVolume);
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
            .ToDictionaryAsync(t => t.TypeId, t => new TypeInfo(t.Name, t.CategoryId, t.PackagedVolume), ct);
        Systems = await db.SolarSystems.AsNoTracking()
            .ToDictionaryAsync(s => s.SolarSystemId, s => new SystemInfo(s.Name, s.Security, s.JumpsToJita), ct);
        Stations = await db.Stations.AsNoTracking()
            .ToDictionaryAsync(s => s.StationId, s => new StationInfo(s.Name, s.SolarSystemId), ct);
        _log.LogInformation("Static cache loaded: {Types} types, {Systems} systems, {Stations} stations in {Ms} ms",
            Types.Count, Systems.Count, Stations.Count, sw.ElapsedMilliseconds);
    }
}
