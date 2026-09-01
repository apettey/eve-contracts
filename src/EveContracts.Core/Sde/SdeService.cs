using EveContracts.Core.Data;
using EveContracts.Core.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace EveContracts.Core.Sde;

/// <summary>
/// Downloads the EVE static data export (Fuzzwork CSV conversion) on first run and
/// re-downloads when stale (game patches change item data). Loads types, groups,
/// packaged volumes, solar systems, stations, and computes jumps-to-Jita offline
/// via BFS over the stargate graph - no ESI route calls needed.
/// </summary>
public class SdeService
{
    private const string DumpBase = "https://www.fuzzwork.co.uk/dump/latest/csv/";
    public static readonly TimeSpan MaxAge = TimeSpan.FromDays(7);

    public static readonly IReadOnlyDictionary<string, int> Regions = new Dictionary<string, int>
    {
        ["The Forge"] = 10000002,
        ["Domain"] = 10000043,
        ["Sinq Laison"] = 10000032,
        ["Heimatar"] = 10000030,
        ["Metropolis"] = 10000042,
    };

    private readonly IServiceScopeFactory _scopes;
    private readonly IHttpClientFactory _httpFactory;
    private readonly Services.StaticDataCache _static;
    private readonly ILogger<SdeService> _log;

    public SdeService(IServiceScopeFactory scopes, IHttpClientFactory httpFactory,
        Services.StaticDataCache staticData, ILogger<SdeService> log)
    {
        _scopes = scopes;
        _httpFactory = httpFactory;
        _static = staticData;
        _log = log;
    }

    public event Action<string>? Progress;

    public async Task<bool> IsFreshAsync(CancellationToken ct = default)
    {
        using var scope = _scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDb>();
        var stamp = await db.AppSettings.FindAsync(["sde_downloaded_at"], ct);
        if (stamp is null) return false;
        if (!DateTime.TryParse(stamp.Value, null, System.Globalization.DateTimeStyles.RoundtripKind, out var at)) return false;
        if (DateTime.UtcNow - at > MaxAge) return false;
        return await db.ItemTypes.AnyAsync(ct);
    }

    public async Task<bool> HasAnyDataAsync(CancellationToken ct = default)
    {
        using var scope = _scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDb>();
        return await db.ItemTypes.AnyAsync(ct);
    }

    /// <summary>
    /// Blocks only when there is no usable static data at all. A stale-but-present
    /// SDE lets the app start scanning immediately while the refresh runs behind it
    /// (game-data changes land within the same session, just not before first paint).
    /// </summary>
    public async Task EnsureLoadedAsync(CancellationToken ct = default)
    {
        if (await IsFreshAsync(ct)) return;
        if (await HasAnyDataAsync(ct))
        {
            _log.LogInformation("SDE stale; refreshing in background while scans continue");
            _ = Task.Run(async () =>
            {
                try { await DownloadAndLoadAsync(ct); }
                catch (Exception ex) { _log.LogWarning("Background SDE refresh failed: {Error}", ex.Message); }
            }, ct);
            return;
        }
        await DownloadAndLoadAsync(ct);
    }

    public async Task DownloadAndLoadAsync(CancellationToken ct = default)
    {
        var http = _httpFactory.CreateClient("sde");
        string[] files = ["invTypes.csv", "invGroups.csv", "invVolumes.csv",
                          "mapSolarSystems.csv", "staStations.csv", "mapSolarSystemJumps.csv"];
        foreach (var f in files)
        {
            ct.ThrowIfCancellationRequested();
            var dest = Path.Combine(AppPaths.SdeDir, f);
            Progress?.Invoke($"Downloading {f}...");
            _log.LogInformation("SDE: downloading {File}", f);
            using var resp = await http.GetAsync(DumpBase + f, HttpCompletionOption.ResponseHeadersRead, ct);
            resp.EnsureSuccessStatusCode();
            await using var fs = File.Create(dest + ".tmp");
            await resp.Content.CopyToAsync(fs, ct);
            fs.Close();
            File.Move(dest + ".tmp", dest, overwrite: true);
        }
        await LoadFromDiskAsync(ct);
    }

    public async Task LoadFromDiskAsync(CancellationToken ct = default)
    {
        Progress?.Invoke("Parsing static data...");
        var groups = new Dictionary<int, int>(); // groupID -> categoryID
        foreach (var row in ReadCsv("invGroups.csv", out var gCols))
        {
            if (int.TryParse(row[gCols["groupID"]], out var gid) && int.TryParse(row[gCols["categoryID"]], out var cid))
                groups[gid] = cid;
        }

        var packaged = new Dictionary<int, double>();
        foreach (var row in ReadCsv("invVolumes.csv", out var vCols))
        {
            if (int.TryParse(row[vCols["typeID"]], out var tid) && double.TryParse(row[vCols["volume"]], System.Globalization.CultureInfo.InvariantCulture, out var vol))
                packaged[tid] = vol;
        }

        var types = new List<ItemType>(50_000);
        foreach (var row in ReadCsv("invTypes.csv", out var tCols))
        {
            if (!int.TryParse(row[tCols["typeID"]], out var tid)) continue;
            if (row[tCols["published"]] != "1") continue;
            int.TryParse(row[tCols["groupID"]], out var gid);
            double.TryParse(row[tCols["volume"]], System.Globalization.CultureInfo.InvariantCulture, out var vol);
            types.Add(new ItemType
            {
                TypeId = tid,
                Name = row[tCols["typeName"]],
                GroupId = gid,
                CategoryId = groups.GetValueOrDefault(gid),
                Volume = vol,
                PackagedVolume = packaged.GetValueOrDefault(tid, vol),
            });
        }

        var systems = new List<SolarSystem>(9_000);
        foreach (var row in ReadCsv("mapSolarSystems.csv", out var sCols))
        {
            if (!int.TryParse(row[sCols["solarSystemID"]], out var sid)) continue;
            int.TryParse(row[sCols["regionID"]], out var rid);
            double.TryParse(row[sCols["security"]], System.Globalization.CultureInfo.InvariantCulture, out var sec);
            systems.Add(new SolarSystem { SolarSystemId = sid, Name = row[sCols["solarSystemName"]], RegionId = rid, Security = sec });
        }

        var stations = new List<Station>(6_000);
        foreach (var row in ReadCsv("staStations.csv", out var stCols))
        {
            if (!long.TryParse(row[stCols["stationID"]], out var stid)) continue;
            int.TryParse(row[stCols["solarSystemID"]], out var ssid);
            stations.Add(new Station { StationId = stid, Name = row[stCols["stationName"]], SolarSystemId = ssid });
        }

        // BFS jumps-to-Jita over the stargate graph
        Progress?.Invoke("Computing routes to Jita...");
        var adj = new Dictionary<int, List<int>>();
        foreach (var row in ReadCsv("mapSolarSystemJumps.csv", out var jCols))
        {
            if (int.TryParse(row[jCols["fromSolarSystemID"]], out var from) && int.TryParse(row[jCols["toSolarSystemID"]], out var to))
            {
                if (!adj.TryGetValue(from, out var l)) adj[from] = l = new List<int>();
                l.Add(to);
            }
        }
        var dist = new Dictionary<int, int> { [Esi.EsiClient.JitaSystemId] = 0 };
        var queue = new Queue<int>();
        queue.Enqueue(Esi.EsiClient.JitaSystemId);
        while (queue.Count > 0)
        {
            var cur = queue.Dequeue();
            if (!adj.TryGetValue(cur, out var neighbors)) continue;
            foreach (var n in neighbors)
            {
                if (dist.ContainsKey(n)) continue;
                dist[n] = dist[cur] + 1;
                queue.Enqueue(n);
            }
        }
        foreach (var s in systems)
            s.JumpsToJita = dist.TryGetValue(s.SolarSystemId, out var d) ? d : -1;

        Progress?.Invoke("Saving static data...");
        using var scope = _scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDb>();
        await Data.BulkOps.ReplaceStaticDataAsync(db, types, systems, stations, ct);
        var stampRow = await db.AppSettings.FindAsync(["sde_downloaded_at"], ct);
        if (stampRow is null) db.AppSettings.Add(new AppSetting { Key = "sde_downloaded_at", Value = DateTime.UtcNow.ToString("o") });
        else stampRow.Value = DateTime.UtcNow.ToString("o");
        await db.SaveChangesAsync(ct);
        _log.LogInformation("SDE loaded: {Types} types, {Systems} systems, {Stations} stations", types.Count, systems.Count, stations.Count);
        await _static.LoadAsync(ct);
        Progress?.Invoke("Static data ready.");
    }

    private IEnumerable<string[]> ReadCsv(string file, out Dictionary<string, int> cols)
    {
        var path = Path.Combine(AppPaths.SdeDir, file);
        var reader = new StreamReader(File.OpenRead(path));
        var enumerator = Csv.ReadRecords(reader).GetEnumerator();
        if (!enumerator.MoveNext()) { cols = []; return []; }
        var header = enumerator.Current;
        cols = header.Select((name, i) => (name, i)).ToDictionary(x => x.name, x => x.i);
        return Iterate(enumerator, reader);

        static IEnumerable<string[]> Iterate(IEnumerator<string[]> e, StreamReader r)
        {
            using (r)
            {
                while (e.MoveNext()) yield return e.Current;
            }
        }
    }
}
