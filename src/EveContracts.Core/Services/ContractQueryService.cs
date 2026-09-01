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

public record ContractDetail(ScannerRow Row, IReadOnlyList<ItemDetail> Items, double Fees, double Hauling, double TotalM3, string Liquidate);

public record OwnRow(long Id, string Dir, string Title, string Type, string Route, string Character,
    string Party, double Value, double Collateral, DateTime Expires, DateTime? Completed, string Status);

public record CharacterRow(int CharacterId, string Name, bool Authed, int Count);

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

    private sealed record Snapshot(int RegionId, List<ScannerRow> Rows, int ScannedCount);
    private Snapshot? _snap;
    private int _dirty = 1;
    private readonly SemaphoreSlim _snapLock = new(1, 1);

    public ContractQueryService(IServiceScopeFactory scopes, SettingsService settings,
        StaticDataCache staticData, PublicContractSync publicSync)
    {
        _scopes = scopes;
        _settings = settings;
        _static = staticData;
        publicSync.Updated += Invalidate;
    }

    public void Invalidate() => Interlocked.Exchange(ref _dirty, 1);

    private string TypeName(int typeId) => _static.Types.TryGetValue(typeId, out var t) ? t.Name : $"Type {typeId}";

    public async Task<(List<ScannerRow> rows, ScannerStats stats)> GetScannerRowsAsync(ScannerFilter f, CancellationToken ct = default)
    {
        var regionId = Sde.SdeService.Regions.GetValueOrDefault(f.Region, Esi.EsiClient.TheForgeRegionId);
        var snap = _snap;
        if (snap is null || snap.RegionId != regionId || Volatile.Read(ref _dirty) == 1)
        {
            await _snapLock.WaitAsync(ct);
            try
            {
                snap = _snap;
                if (snap is null || snap.RegionId != regionId || Interlocked.CompareExchange(ref _dirty, 0, 1) == 1)
                    _snap = snap = await BuildSnapshotAsync(regionId, ct);
            }
            finally { _snapLock.Release(); }
        }

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

        var list = await live.OrderByDescending(c => c.NetProfit).ToListAsync(ct);
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

        return new Snapshot(regionId, rows, scanned);
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
            items.Where(i => i.Included).Sum(i => i.M3), liquidate);
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
