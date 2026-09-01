// Profit Scanner benchmark harness.
// Seeds a synthetic Forge-scale dataset into a throwaway SQLite DB, then times the
// hot paths of the scanner pipeline: contract upsert, evaluation, and UI queries.
// Run: dotnet run --project tests/EveContracts.Bench -c Release [-- <contracts> <iterations>]

using System.Diagnostics;
using EveContracts.Core.Data;
using EveContracts.Core.Models;
using EveContracts.Core.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

var contractCount = args.Length > 0 && int.TryParse(args[0], out var c) ? c : 20_000;
var iterations = args.Length > 1 && int.TryParse(args[1], out var i) ? i : 3;

var dbPath = Path.Combine(Path.GetTempPath(), $"evebench_{Guid.NewGuid():N}.db");
var services = new ServiceCollection();
services.AddDbContext<AppDb>(o => o.UseSqlite($"Data Source={dbPath}"), ServiceLifetime.Transient);
services.AddSingleton<SettingsService>();
services.AddLogging();
var sp = services.BuildServiceProvider();
var scopes = sp.GetRequiredService<IServiceScopeFactory>();

Console.WriteLine($"Bench: {contractCount:N0} contracts, {iterations} iterations, db={dbPath}");

// ---------- Seed ----------
var rng = new Random(42);
const int TypeCount = 1500;
const int SystemCount = 400;

using (var scope = scopes.CreateScope())
{
    var db = scope.ServiceProvider.GetRequiredService<AppDb>();
    await db.Database.EnsureCreatedAsync();

    var sw = Stopwatch.StartNew();
    var types = Enumerable.Range(1, TypeCount).Select(t => new ItemType
    {
        TypeId = t,
        Name = $"Type {t}",
        GroupId = t % 100,
        CategoryId = t % 3 == 0 ? 6 : 7, // ships + modules
        Volume = t % 3 == 0 ? 50000 : 5,
        PackagedVolume = t % 3 == 0 ? 10000 : 5,
    }).ToList();
    db.ItemTypes.AddRange(types);

    db.Prices.AddRange(Enumerable.Range(1, TypeCount).Select(t => new Price
    {
        TypeId = t,
        JitaSell = 1e6 * (1 + rng.NextDouble() * 500),
        JitaBuy = 1e6 * (1 + rng.NextDouble() * 400),
        PrevDayVolume = rng.Next(0, 500),
        PricesUpdatedAt = DateTime.UtcNow,
        VolumeUpdatedAt = DateTime.UtcNow,
    }));
    await db.SaveChangesAsync();
    Console.WriteLine($"seed types+prices: {sw.ElapsedMilliseconds} ms");
}

// ---------- 1. Contract upsert (simulates one scan cycle write) ----------
var upsertMs = new List<long>();
for (var iter = 0; iter < iterations; iter++)
{
    using var scope = scopes.CreateScope();
    var db = scope.ServiceProvider.GetRequiredService<AppDb>();
    var now = DateTime.UtcNow;
    var sw = Stopwatch.StartNew();

    var known = await db.PublicContracts.Where(x => x.RegionId == 10000002)
        .ToDictionaryAsync(x => x.ContractId);

    for (long id = 1; id <= contractCount; id++)
    {
        if (!known.TryGetValue(id, out var row))
        {
            row = new PublicContract { ContractId = id, RegionId = 10000002, FirstSeen = now };
            db.PublicContracts.Add(row);
            var itemCount = 1 + (int)(id % 4);
            for (var k = 0; k < itemCount; k++)
                db.ContractItems.Add(new ContractItem
                {
                    ContractId = id,
                    TypeId = (int)((id * 7 + k * 13) % TypeCount) + 1,
                    Quantity = 1 + k,
                    IsIncluded = true,
                });
            row.ItemsFetched = true;
        }
        row.Type = "item_exchange";
        row.Title = $"Contract {id}";
        row.Price = 1e6 * (1 + (id * 37 % 900));
        row.SolarSystemId = (int)(id % SystemCount);
        row.SystemName = $"System {id % SystemCount}";
        row.StationName = "Station";
        row.SecurityStatus = id % 10 == 0 ? 0.4 : 0.9;
        row.JumpsToJita = (int)(id % 15);
        row.DateIssued = now.AddDays(-1);
        row.DateExpired = now.AddDays(3);
        row.LastSeen = now;
    }
    await db.SaveChangesAsync();
    sw.Stop();
    upsertMs.Add(sw.ElapsedMilliseconds);
    Console.WriteLine($"upsert[{iter}]: {sw.ElapsedMilliseconds} ms");
}

// ---------- 2. Evaluation over all live contracts ----------
var settings = sp.GetRequiredService<SettingsService>();
var thresholds = new EvalThresholds(10, 20, 800e6, 4.5, 800, true, false, false);
var evalMs = new List<long>();
for (var iter = 0; iter < iterations; iter++)
{
    using var scope = scopes.CreateScope();
    var db = scope.ServiceProvider.GetRequiredService<AppDb>();
    var now = DateTime.UtcNow;
    var sw = Stopwatch.StartNew();

    var contracts = await db.PublicContracts
        .Where(x => x.DateExpired > now && x.ItemsFetched)
        .Include(x => x.Items)
        .ToListAsync();
    var typeIds = contracts.SelectMany(x => x.Items.Select(it => it.TypeId)).Distinct().ToList();
    var prices = await db.Prices.Where(p => typeIds.Contains(p.TypeId)).ToDictionaryAsync(p => p.TypeId);
    var types = await db.ItemTypes.Where(t => typeIds.Contains(t.TypeId)).ToDictionaryAsync(t => t.TypeId);

    foreach (var con in contracts)
    {
        var items = con.Items.Select(it =>
        {
            types.TryGetValue(it.TypeId, out var t);
            prices.TryGetValue(it.TypeId, out var p);
            return new EvalItem(it.TypeId, t?.Name ?? "?", it.Quantity, it.IsIncluded,
                t?.CategoryId ?? 0, t?.PackagedVolume ?? 0,
                p?.JitaSell ?? 0, p?.JitaBuy ?? 0, p?.PrevDayVolume ?? 0, null, false);
        }).ToList();
        var r = EvaluationService.Evaluate(
            new EvalInput(con.Type, con.Price, con.SecurityStatus, con.JumpsToJita, con.DateExpired, con.Title, items),
            thresholds);
        con.JitaSellValue = r.JitaSellValue;
        con.Fees = r.Fees;
        con.Hauling = r.Hauling;
        con.NetProfit = r.NetProfit;
        con.Margin = r.Margin;
        con.Verdict = r.Verdict;
        con.FlagsJson = r.FlagsJson;
    }
    await db.SaveChangesAsync();
    sw.Stop();
    evalMs.Add(sw.ElapsedMilliseconds);
    Console.WriteLine($"evaluate[{iter}]: {sw.ElapsedMilliseconds} ms ({contracts.Count:N0} contracts)");
}

// ---------- 3. Scanner UI query (filter + sort + take 500) ----------
var query = new ContractQueryService(scopes, settings);
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
SqliteCleanup(dbPath);

static double Median(List<long> xs)
{
    var s = xs.OrderBy(x => x).ToList();
    return s[s.Count / 2];
}

static void SqliteCleanup(string path)
{
    Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
    try { File.Delete(path); } catch { }
}
