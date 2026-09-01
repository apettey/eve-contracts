// Profit Scanner benchmark harness.
// Seeds a synthetic Forge-scale dataset into a throwaway SQLite DB, then times the
// REAL pipeline code paths: PublicContractSync.UpsertContractsAsync / SaveItemsAsync /
// EvaluateAllAsync and ContractQueryService queries.
// Run: dotnet run --project tests/EveContracts.Bench -c Release [-- <contracts> <iterations>]

using System.Diagnostics;
using EveContracts.Core.Data;
using EveContracts.Core.Esi;
using EveContracts.Core.Models;
using EveContracts.Core.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

var contractCount = args.Length > 0 && int.TryParse(args[0], out var c) ? c : 20_000;
var iterations = args.Length > 1 && int.TryParse(args[1], out var i) ? i : 3;

var dbPath = Path.Combine(Path.GetTempPath(), $"evebench_{Guid.NewGuid():N}.db");
var services = new ServiceCollection();
services.AddDbContext<AppDb>(o => o.UseSqlite($"Data Source={dbPath}"), ServiceLifetime.Transient);
services.AddLogging(l => l.SetMinimumLevel(LogLevel.Warning));
services.AddHttpClient();
services.AddSingleton<SettingsService>();
services.AddSingleton<StaticDataCache>();
services.AddSingleton(sp => new EsiClient(new HttpClient(), sp.GetRequiredService<ILogger<EsiClient>>()));
services.AddSingleton<PriceService>();
services.AddSingleton<PublicContractSync>();
services.AddSingleton<ContractQueryService>();
var sp = services.BuildServiceProvider();
var scopes = sp.GetRequiredService<IServiceScopeFactory>();
var sync = sp.GetRequiredService<PublicContractSync>();
var query = sp.GetRequiredService<ContractQueryService>();

Console.WriteLine($"Bench: {contractCount:N0} contracts, {iterations} iterations, db={dbPath}");

var rng = new Random(42);
const int TypeCount = 1500;
const int SystemCount = 400;
var now = DateTime.UtcNow;

// ---------- Seed static data + prices ----------
using (var scope = scopes.CreateScope())
{
    var db = scope.ServiceProvider.GetRequiredService<AppDb>();
    await db.Database.EnsureCreatedAsync();

    var sw = Stopwatch.StartNew();
    db.ItemTypes.AddRange(Enumerable.Range(1, TypeCount).Select(t => new ItemType
    {
        TypeId = t,
        Name = $"Type {t}",
        GroupId = t % 100,
        CategoryId = t % 3 == 0 ? 6 : 7, // ships + modules
        Volume = t % 3 == 0 ? 50000 : 5,
        PackagedVolume = t % 3 == 0 ? 10000 : 5,
    }));
    db.Prices.AddRange(Enumerable.Range(1, TypeCount).Select(t => new Price
    {
        TypeId = t,
        JitaSell = 1e6 * (1 + rng.NextDouble() * 500),
        JitaBuy = 1e6 * (1 + rng.NextDouble() * 400),
        PrevDayVolume = rng.Next(0, 500),
        PricesUpdatedAt = now,
        VolumeUpdatedAt = now,
    }));
    db.SolarSystems.AddRange(Enumerable.Range(0, SystemCount).Select(s => new SolarSystem
    {
        SolarSystemId = 30000000 + s,
        Name = $"System {s}",
        RegionId = 10000002,
        Security = s % 10 == 0 ? 0.4 : 0.9,
        JumpsToJita = s % 15,
    }));
    db.Stations.AddRange(Enumerable.Range(0, SystemCount).Select(s => new Station
    {
        StationId = 60000000 + s,
        Name = $"Station {s} - Moon {s % 12} - Some Corporation Factory",
        SolarSystemId = 30000000 + s,
    }));
    await db.SaveChangesAsync();
    Console.WriteLine($"seed static+prices: {sw.ElapsedMilliseconds} ms");
}

// ---------- Synthetic ESI scan payload ----------
List<EsiPublicContract> MakeScan() => Enumerable.Range(1, contractCount).Select(id => new EsiPublicContract
{
    ContractId = id,
    Type = "item_exchange",
    Title = $"Contract {id}",
    Price = 1e6 * (1 + (id * 37 % 900)),
    StartLocationId = 60000000 + id % SystemCount,
    DateIssued = now.AddDays(-1),
    DateExpired = now.AddDays(3),
    Volume = 5000,
}).ToList();

// ---------- 1. Contract upsert (real UpsertContractsAsync) ----------
var scan = MakeScan();
var upsertMs = new List<long>();
List<long> newIds = [];
for (var iter = 0; iter < iterations; iter++)
{
    var sw = Stopwatch.StartNew();
    var ids = await sync.UpsertContractsAsync(10000002, scan);
    sw.Stop();
    if (iter == 0) newIds = ids;
    upsertMs.Add(sw.ElapsedMilliseconds);
    Console.WriteLine($"upsert[{iter}]: {sw.ElapsedMilliseconds} ms ({ids.Count:N0} pending items)");

    if (iter == 0)
    {
        // Seed items exactly as the item-fetch path would save them.
        var swi = Stopwatch.StartNew();
        var itemsByContract = ids.ToDictionary(
            id => id,
            id => Enumerable.Range(0, 1 + (int)(id % 4)).Select(k => new ContractItem
            {
                ContractId = id,
                TypeId = (int)((id * 7 + k * 13) % TypeCount) + 1,
                Quantity = 1 + k,
                IsIncluded = true,
            }).ToList());
        foreach (var chunk in itemsByContract.Chunk(500))
            await sync.SaveItemsAsync(chunk.ToDictionary(kv => kv.Key, kv => kv.Value));
        Console.WriteLine($"save items ({itemsByContract.Values.Sum(v => v.Count):N0} rows, chunks of 500): {swi.ElapsedMilliseconds} ms");
    }
}

// ---------- 2. Evaluation (real EvaluateAllAsync) ----------
var evalMs = new List<long>();
for (var iter = 0; iter < iterations; iter++)
{
    var sw = Stopwatch.StartNew();
    await sync.EvaluateAllAsync();
    sw.Stop();
    evalMs.Add(sw.ElapsedMilliseconds);
    Console.WriteLine($"evaluate[{iter}]: {sw.ElapsedMilliseconds} ms");
}

// ---------- 3. Scanner UI query (filter + sort + take 500) ----------
var filter = new ScannerFilter("The Forge", 10, 20, 800e6, true);
var queryMs = new List<double>();
for (var iter = 0; iter < Math.Max(iterations, 10); iter++)
{
    var sw = Stopwatch.StartNew();
    var (rows, stats) = await query.GetScannerRowsAsync(filter);
    sw.Stop();
    queryMs.Add(sw.Elapsed.TotalMilliseconds);
    if (iter == 0) Console.WriteLine($"query[0]: {sw.Elapsed.TotalMilliseconds:0.0} ms ({rows.Count} rows, {stats.Passed} passed of {stats.Scanned:N0})");
}
Console.WriteLine($"query warm avg: {queryMs.Skip(1).Average():0.0} ms");

// ---------- 4. Detail query ----------
var detailMs = new List<double>();
for (var iter = 0; iter < 20; iter++)
{
    var sw = Stopwatch.StartNew();
    _ = await query.GetDetailAsync(iter * 37 + 1);
    sw.Stop();
    detailMs.Add(sw.Elapsed.TotalMilliseconds);
}
Console.WriteLine($"detail warm avg: {detailMs.Skip(2).Average():0.0} ms");

Console.WriteLine();
Console.WriteLine("=== SUMMARY (median) ===");
Console.WriteLine($"upsert:   {Median(upsertMs):0} ms");
Console.WriteLine($"evaluate: {Median(evalMs):0} ms");
Console.WriteLine($"query:    {queryMs.Skip(1).Average():0.0} ms");
Console.WriteLine($"detail:   {detailMs.Skip(2).Average():0.0} ms");

sp.Dispose();
Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
try { File.Delete(dbPath); } catch { }

static double Median(List<long> xs)
{
    var s = xs.OrderBy(x => x).ToList();
    return s[s.Count / 2];
}
