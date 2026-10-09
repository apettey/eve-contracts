using EveContracts.Core.Data;
using EveContracts.Core.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace EveContracts.Core.Services;

public record ScannerFilter(string Region, double MinMarginPct, double MinVolume, double MaxPrice, bool HighsecOnly);

public record ScannerRow(
    long ContractId, string Title, string ItemsSummary, string System, string Station,
    double Sec, int Jumps, double Price, double SellVal, double Profit, double Margin,
    string Verdict, DateTime Expires, IReadOnlyList<string> Flags);

public record ScannerStats(int Scanned, int Passed, double BestProfit, double TotalOpportunity);

public record ItemDetail(string Name, long Qty, double M3, double Sell, double Buy, double VolPerDay, bool BelowFloor, bool Included);

public record ContractDetail(ScannerRow Row, IReadOnlyList<ItemDetail> Items, double Fees, double Hauling, double TotalM3, string Liquidate,
    BrowseRow Terms);

/// <summary>Filter for the All Contracts browser. Type: All | Item Exchange | Auction | Courier.</summary>
public record BrowseFilter(string Region, string Search, string Type, bool HighsecOnly, string Sort);

public record BrowseRow(
    long ContractId, string Type, string Title, string ItemsSummary, string System, string Station,
    string Destination, double Sec, int Jumps, double Price, double Reward, double Collateral, double Buyout,
    double VolumeM3, DateTime Issued, DateTime Expires, string Verdict, double Profit, bool ItemsFetched);

public record BrowseStats(int Live, int Matching, double MatchingValue, double CheapestMatch);

public record OwnRow(long Id, string Dir, string Title, string Type, string Route, string Character,
    string Party, double Value, double Collateral, DateTime Expires, DateTime? Completed, string Status);

public record CharacterRow(int CharacterId, string Name, bool Authed, int Count);

public record OwnItemDetail(string Name, long Qty, bool Included, double JitaSell);

public record OwnContractDetail(
    OwnRow Row, DateTime DateIssued, double VolumeM3, int DaysToComplete,
    double Price, double Reward, double Collateral, double Buyout,
    IReadOnlyList<OwnItemDetail> Items, bool ItemsAvailable);

/// <summary>
/// Read-side queries for the UI. Everything hits the local cache — never ESI.
/// The scanner keeps an in-memory snapshot of all evaluated rows for the active
/// region; filter changes are answered from memory (sub-ms) and the snapshot is
/// rebuilt only when the sync pipeline reports new data.
/// </summary>
public class ContractQueryService
{
    private readonly IServiceScopeFactory _scopes;
    private readonly SettingsService _settings;
    private readonly StaticDataCache _static;

    private sealed record Snapshot(List<ScannerRow> Rows, int ScannedCount);
    private sealed record BrowseEntry(BrowseRow Row, string SearchText);
    private readonly SnapshotCache<Snapshot> _scan;
    private readonly SnapshotCache<List<BrowseEntry>> _browse;

    public ContractQueryService(IServiceScopeFactory scopes, SettingsService settings,
        StaticDataCache staticData, PublicContractSync publicSync)
    {
        _scopes = scopes;
        _settings = settings;
        _static = staticData;
        _scan = new SnapshotCache<Snapshot>(BuildSnapshotAsync);
        _browse = new SnapshotCache<List<BrowseEntry>>(BuildBrowseAsync);
        publicSync.Updated += Invalidate;
    }

    public void Invalidate()
    {
        _scan.Invalidate();
        _browse.Invalidate();
    }

    /// <summary>
    /// Per-region in-memory snapshot. During heavy sync every chunk marks it dirty;
    /// rebuilding at most every 2 s keeps the UI fresh without paying a full rebuild
    /// per chunk. A region switch always rebuilds.
    /// </summary>
    private sealed class SnapshotCache<T>(Func<int, CancellationToken, Task<T>> build) where T : class
    {
        private static readonly TimeSpan RebuildThrottle = TimeSpan.FromSeconds(2);
        private readonly SemaphoreSlim _lock = new(1, 1);
        private (int RegionId, T Value)? _snap;
        private int _dirty = 1;
        private DateTime _lastBuild = DateTime.MinValue;

        public void Invalidate() => Interlocked.Exchange(ref _dirty, 1);

        public async Task<T> GetAsync(int regionId, CancellationToken ct)
        {
            var snap = _snap;
            var mustBuild = snap is null || snap.Value.RegionId != regionId;
            var mayBuild = Volatile.Read(ref _dirty) == 1 && DateTime.UtcNow - _lastBuild > RebuildThrottle;
            if (!mustBuild && !mayBuild) return snap!.Value.Value;

            await _lock.WaitAsync(ct);
            try
            {
                snap = _snap;
                if (snap is null || snap.Value.RegionId != regionId ||
                    (DateTime.UtcNow - _lastBuild > RebuildThrottle && Interlocked.CompareExchange(ref _dirty, 0, 1) == 1))
                {
                    // Off the UI thread: a Forge-sized build is ~1 s of CPU after the DB reads.
                    _snap = snap = (regionId, await Task.Run(() => build(regionId, ct), ct));
                    _lastBuild = DateTime.UtcNow;
                }
                return snap.Value.Value;
            }
            finally { _lock.Release(); }
        }
    }

    private string TypeName(int typeId) => _static.Types.TryGetValue(typeId, out var t) ? t.Name : $"Type {typeId}";

    public async Task<(List<ScannerRow> rows, ScannerStats stats)> GetScannerRowsAsync(ScannerFilter f, CancellationToken ct = default)
    {
        var regionId = Sde.SdeService.Regions.GetValueOrDefault(f.Region, Esi.EsiClient.TheForgeRegionId);
        var snap = await _scan.GetAsync(regionId, ct);

        var now = DateTime.UtcNow;
        var minMargin = f.MinMarginPct / 100.0;
        var rows = new List<ScannerRow>(512);
        foreach (var r in snap.Rows) // pre-sorted by profit desc
        {
            if (r.Expires <= now || r.Price > f.MaxPrice) continue;
            if (f.HighsecOnly && r.Sec < 0.45) continue; // EVE rounds 0.45+ to 0.5
            var verdict = r.Verdict == "THIN" || r.Verdict == "BUY"
                ? (r.Margin < minMargin ? "THIN" : "BUY")
                : r.Verdict;
            rows.Add(verdict == r.Verdict ? r : r with { Verdict = verdict });
            if (rows.Count >= 500) break;
        }

        int passed = 0;
        double best = 0, total = 0;
        foreach (var r in rows)
        {
            if (r.Verdict != "BUY") continue;
            passed++;
            total += r.Profit;
            if (r.Profit > best) best = r.Profit;
        }
        return (rows, new ScannerStats(snap.ScannedCount, passed, best, total));
    }

    private async Task<Snapshot> BuildSnapshotAsync(int regionId, CancellationToken ct)
    {
        using var scope = _scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDb>();
        var now = DateTime.UtcNow;

        var scanned = await db.PublicContracts.CountAsync(c => c.RegionId == regionId && c.DateExpired > now, ct);

        var live = db.PublicContracts.AsNoTracking()
            .Where(c => c.RegionId == regionId && c.DateExpired > now && c.ItemsFetched)
            .Where(c => c.Verdict != "EXCLUDED" && c.Verdict != "PENDING");

        // SCAM rows sink below everything else; the rest keep profit-desc order.
        var list = await live.OrderByDescending(c => c.NetProfit).ToListAsync(ct);
        list = list.OrderBy(c => c.Verdict == "SCAM" ? 1 : 0).ThenByDescending(c => c.NetProfit).ToList();
        var items = await live
            .Join(db.ContractItems.AsNoTracking(), C => C.ContractId, I => I.ContractId, (C, I) => I)
            .ToListAsync(ct);
        var itemsByContract = items.ToLookup(i => i.ContractId);

        var rows = list.Select(c =>
        {
            var cItems = itemsByContract[c.ContractId];
            return new ScannerRow(
                c.ContractId,
                string.IsNullOrWhiteSpace(c.Title) ? SummarizeItems(cItems, TypeName, 1) : c.Title,
                SummarizeItems(cItems, TypeName, 4),
                c.SystemName, ShortStation(c.StationName), c.SecurityStatus, c.JumpsToJita,
                c.Price, c.JitaSellValue, c.NetProfit, c.Margin, c.Verdict, c.DateExpired,
                ParseFlags(c.FlagsJson));
        }).ToList();

        return new Snapshot(rows, scanned);
    }

    /// <summary>
    /// Every live public contract in the region — like EVE's own contract search,
    /// nothing is hidden by scope or verdict. Filtering is in-memory over a
    /// precomputed lowercase search string (title, every item, locations); all
    /// space-separated terms must match.
    /// </summary>
    public async Task<(List<BrowseRow> rows, BrowseStats stats)> GetBrowseRowsAsync(BrowseFilter f, CancellationToken ct = default)
    {
        var regionId = Sde.SdeService.Regions.GetValueOrDefault(f.Region, Esi.EsiClient.TheForgeRegionId);
        var snap = await _browse.GetAsync(regionId, ct);

        var now = DateTime.UtcNow;
        var terms = f.Search.ToLowerInvariant().Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var typeKey = f.Type switch
        {
            "Item Exchange" => "item_exchange",
            "Auction" => "auction",
            "Courier" => "courier",
            _ => null,
        };

        var rows = new List<BrowseRow>();
        int live = 0;
        double value = 0, cheapest = double.MaxValue;
        foreach (var e in snap)
        {
            var r = e.Row;
            if (r.Expires <= now) continue;
            live++;
            if (typeKey is not null && r.Type != typeKey) continue;
            if (f.HighsecOnly && r.Sec < 0.45) continue; // EVE rounds 0.45+ to 0.5
            var match = true;
            foreach (var t in terms)
                if (!e.SearchText.Contains(t, StringComparison.Ordinal)) { match = false; break; }
            if (!match) continue;
            rows.Add(r);
            var v = BrowseValue(r);
            value += v;
            if (v > 0 && v < cheapest) cheapest = v;
        }

        Comparison<BrowseRow> cmp = f.Sort switch
        {
            // 0-ISK rows (want-to-buy, swaps, unpopulated courier rewards) would otherwise flood the top.
            "Price ↑" => (a, b) => (BrowseValue(a) <= 0).CompareTo(BrowseValue(b) <= 0) is var z and not 0
                ? z : BrowseValue(a).CompareTo(BrowseValue(b)),
            "Price ↓" => (a, b) => BrowseValue(b).CompareTo(BrowseValue(a)),
            "Expiring soonest" => (a, b) => a.Expires.CompareTo(b.Expires),
            "Net profit" => (a, b) => b.Profit.CompareTo(a.Profit),
            _ => (a, b) => b.Issued.CompareTo(a.Issued), // Newest
        };
        rows.Sort(cmp);

        return (rows, new BrowseStats(live, rows.Count, value, cheapest == double.MaxValue ? 0 : cheapest));
    }

    /// <summary>What the contract is "worth" for sorting/stats: courier reward, otherwise price.</summary>
    private static double BrowseValue(BrowseRow r) => r.Type == "courier" ? r.Reward : r.Price;

    private async Task<List<BrowseEntry>> BuildBrowseAsync(int regionId, CancellationToken ct)
    {
        if (!_static.Ready) await _static.LoadAsync(ct);
        using var scope = _scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDb>();
        var now = DateTime.UtcNow;

        var live = db.PublicContracts.AsNoTracking().Where(c => c.RegionId == regionId && c.DateExpired > now);
        var list = await live.ToListAsync(ct);
        var items = await live
            .Join(db.ContractItems.AsNoTracking(), C => C.ContractId, I => I.ContractId, (C, I) => I)
            .ToListAsync(ct);
        var itemsByContract = items.ToLookup(i => i.ContractId);

        var result = new List<BrowseEntry>(list.Count);
        var sb = new System.Text.StringBuilder();
        foreach (var c in list)
        {
            var cItems = itemsByContract[c.ContractId];
            sb.Clear();
            sb.Append(c.Title).Append('\n').Append(c.SystemName).Append('\n')
              .Append(c.StationName).Append('\n').Append(c.DestinationName);
            foreach (var i in cItems) sb.Append('\n').Append(TypeName(i.TypeId));
            result.Add(new BrowseEntry(ToBrowseRow(c, cItems), sb.ToString().ToLowerInvariant()));
        }
        return result;
    }

    private BrowseRow ToBrowseRow(PublicContract c, IEnumerable<ContractItem> items)
    {
        var summary = c.Type == "courier" ? $"{c.VolumeM3.ToString("N0", System.Globalization.CultureInfo.InvariantCulture)} m³ to haul"
            : !c.ItemsFetched ? "(items loading…)"
            : items.Any(i => i.IsIncluded) ? SummarizeItems(items, TypeName, 4)
            : "wants: " + SummarizeItems(items.Select(i => new ContractItem { TypeId = i.TypeId, Quantity = i.Quantity, IsIncluded = true }), TypeName, 4);
        var title = !string.IsNullOrWhiteSpace(c.Title) ? c.Title
            : c.Type == "courier" ? $"Courier → {ShortStation(c.DestinationName)}"
            : c.ItemsFetched ? SummarizeItems(items, TypeName, 1)
            : $"Contract {c.ContractId}";
        // Only scanner-scope verdicts mean anything; EXCLUDED/PENDING rows carry no profit claim.
        var evaluated = c.Verdict is not ("EXCLUDED" or "PENDING");
        return new BrowseRow(c.ContractId, c.Type, title, summary,
            c.SystemName, ShortStation(c.StationName), ShortStation(c.DestinationName),
            c.SecurityStatus, c.JumpsToJita, c.Price, c.Reward, c.Collateral, c.Buyout, c.VolumeM3,
            c.DateIssued, c.DateExpired, evaluated ? c.Verdict : "", evaluated ? c.NetProfit : 0, c.ItemsFetched);
    }

    public async Task<ContractDetail?> GetDetailAsync(long contractId, CancellationToken ct = default)
    {
        using var scope = _scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDb>();
        var c = await db.PublicContracts.AsNoTracking().Include(x => x.Items)
            .FirstOrDefaultAsync(x => x.ContractId == contractId, ct);
        if (c is null) return null;

        var typeIds = c.Items.Select(i => i.TypeId).Distinct().ToList();
        var prices = await db.Prices.Where(p => typeIds.Contains(p.TypeId)).ToDictionaryAsync(p => p.TypeId, ct);
        var minVol = _settings.MinDailyVolume;

        var items = c.Items.Select(i =>
        {
            _static.Types.TryGetValue(i.TypeId, out var t);
            prices.TryGetValue(i.TypeId, out var p);
            return new ItemDetail(
                t?.Name ?? $"Type {i.TypeId}", i.Quantity,
                (t?.PackagedVolume ?? 0) * i.Quantity,
                p?.JitaSell ?? 0, p?.JitaBuy ?? 0, p?.PrevDayVolume ?? 0,
                (p?.PrevDayVolume ?? 0) <= minVol, i.IsIncluded);
        }).ToList();

        var worstVol = items.Where(i => i.Included).Select(i => i.VolPerDay).DefaultIfEmpty(0).Min();
        var liquidate = worstVol > 100 ? "< 1 day" : worstVol > 30 ? "1–3 days" : "3–7 days";

        var row = new ScannerRow(c.ContractId,
            string.IsNullOrWhiteSpace(c.Title) ? SummarizeItems(c.Items, TypeName, 1) : c.Title,
            SummarizeItems(c.Items, TypeName, 4),
            c.SystemName, ShortStation(c.StationName), c.SecurityStatus, c.JumpsToJita,
            c.Price, c.JitaSellValue, c.NetProfit, c.Margin, c.Verdict, c.DateExpired, ParseFlags(c.FlagsJson));

        return new ContractDetail(row, items, c.Fees, c.Hauling,
            items.Where(i => i.Included).Sum(i => i.M3), liquidate, ToBrowseRow(c, c.Items));
    }

    public async Task<List<CharacterRow>> GetCharactersAsync(CancellationToken ct = default)
    {
        using var scope = _scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDb>();
        var counts = await db.OwnContracts
            .Where(o => o.Status == "outstanding" || o.Status == "in_progress")
            .GroupBy(o => o.CharacterId)
            .Select(g => new { g.Key, Count = g.Count() })
            .ToDictionaryAsync(x => x.Key, x => x.Count, ct);
        return (await db.Characters.AsNoTracking().OrderBy(c => c.Name).ToListAsync(ct))
            .Select(c => new CharacterRow(c.CharacterId, c.Name, c.AuthStatus == "ok", counts.GetValueOrDefault(c.CharacterId)))
            .ToList();
    }

    public async Task<List<OwnRow>> GetOwnRowsAsync(int? characterId, string direction, string status, CancellationToken ct = default)
    {
        using var scope = _scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDb>();
        var q = db.OwnContracts.AsNoTracking().AsQueryable();
        if (characterId is int cid) q = q.Where(o => o.CharacterId == cid);
        if (direction == "Outbound") q = q.Where(o => o.Direction == "OUT");
        if (direction == "Inbound") q = q.Where(o => o.Direction == "IN");
        var statusKey = status switch
        {
            "Outstanding" => "outstanding",
            "In progress" => "in_progress",
            "Finished" => "finished",
            "Expired" => "expired",
            _ => null,
        };
        if (statusKey is not null) q = q.Where(o => o.Status == statusKey);

        return (await q.OrderByDescending(o => o.DateIssued).ToListAsync(ct))
            .Select(o => new OwnRow(o.Id, o.Direction, o.Title, PrettyType(o.Type), o.Route,
                o.CharacterName, o.OtherParty,
                o.Type == "courier" ? o.Reward : o.Price,
                o.Collateral, o.DateExpired, o.DateCompleted, PrettyStatus(o.Status)))
            .ToList();
    }

    public async Task<OwnContractDetail?> GetOwnDetailAsync(long ownRowId, CancellationToken ct = default)
    {
        if (!_static.Ready) await _static.LoadAsync(ct);
        using var scope = _scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDb>();
        var o = await db.OwnContracts.AsNoTracking().FirstOrDefaultAsync(x => x.Id == ownRowId, ct);
        if (o is null) return null;

        // ESI returns one record per stack; collapse identical types for display.
        var items = (await db.OwnContractItems.AsNoTracking()
                .Where(i => i.OwnContractId == ownRowId).ToListAsync(ct))
            .GroupBy(i => (i.TypeId, i.IsIncluded))
            .Select(g => new OwnContractItem
            {
                TypeId = g.Key.TypeId,
                IsIncluded = g.Key.IsIncluded,
                Quantity = g.Sum(x => x.Quantity),
            })
            .ToList();
        var typeIds = items.Select(i => i.TypeId).Distinct().ToList();
        var prices = typeIds.Count == 0
            ? new Dictionary<int, Price>()
            : await db.Prices.Where(p => typeIds.Contains(p.TypeId)).ToDictionaryAsync(p => p.TypeId, ct);

        var row = new OwnRow(o.Id, o.Direction, o.Title, PrettyType(o.Type), o.Route,
            o.CharacterName, o.OtherParty,
            o.Type == "courier" ? o.Reward : o.Price,
            o.Collateral, o.DateExpired, o.DateCompleted, PrettyStatus(o.Status));

        var itemDetails = items
            .OrderByDescending(i => i.IsIncluded)
            .ThenByDescending(i => (prices.TryGetValue(i.TypeId, out var pv) ? pv.JitaSell : 0) * i.Quantity)
            .Select(i => new OwnItemDetail(TypeName(i.TypeId), i.Quantity, i.IsIncluded,
                prices.TryGetValue(i.TypeId, out var p) ? p.JitaSell : 0))
            .ToList();

        return new OwnContractDetail(row, o.DateIssued, o.VolumeM3, o.DaysToComplete,
            o.Price, o.Reward, o.Collateral, o.Buyout, itemDetails, o.ItemsFetched);
    }

    public async Task<(double outstanding, double collateral, int completed30d, int expiring24h)> GetOwnStatsAsync(CancellationToken ct = default)
    {
        using var scope = _scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDb>();
        var now = DateTime.UtcNow;
        var active = await db.OwnContracts.AsNoTracking()
            .Where(o => o.Status == "outstanding" || o.Status == "in_progress").ToListAsync(ct);
        var completed = await db.OwnContracts.CountAsync(
            o => o.Status == "finished" && o.DateCompleted > now.AddDays(-30), ct);
        var expiring = active.Count(o => o.DateExpired > now && o.DateExpired < now.AddHours(24));
        return (active.Sum(o => o.Type == "courier" ? o.Reward : o.Price),
                active.Where(o => o.Type == "courier").Sum(o => o.Collateral),
                completed, expiring);
    }

    private static string PrettyType(string t) => t switch
    {
        "item_exchange" => "Item Exchange",
        "courier" => "Courier",
        "auction" => "Auction",
        "loan" => "Loan",
        _ => t,
    };

    private static string PrettyStatus(string s) => s switch
    {
        "outstanding" => "Outstanding",
        "in_progress" => "In progress",
        "finished" or "finished_issuer" or "finished_contractor" => "Finished",
        "expired" => "Expired",
        "rejected" => "Rejected",
        "cancelled" => "Cancelled",
        "failed" => "Failed",
        "deleted" => "Deleted",
        _ => s,
    };

    private static string ShortStation(string name)
    {
        if (string.IsNullOrEmpty(name)) return "";
        if (name.Length <= 28) return name;
        var parts = name.Split(" - ");
        return parts.Length >= 2 ? parts[0] + " " + parts[^1] : name[..28];
    }

    private static string SummarizeItems(IEnumerable<ContractItem> items, Func<int, string> name, int max)
    {
        var parts = items.Where(i => i.IsIncluded)
            .Select(i => (i.Quantity > 1 ? i.Quantity + "× " : "") + name(i.TypeId))
            .ToList();
        if (parts.Count == 0) return "(no items)";
        var head = string.Join(", ", parts.Take(max));
        return parts.Count > max ? head + $" +{parts.Count - max} more" : head;
    }

    private static IReadOnlyList<string> ParseFlags(string json)
    {
        try { return System.Text.Json.JsonSerializer.Deserialize<List<string>>(json) ?? []; }
        catch { return []; }
    }
}
