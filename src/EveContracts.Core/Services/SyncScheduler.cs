using EveContracts.Core.Data;
using EveContracts.Core.Sde;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace EveContracts.Core.Services;

/// <summary>
/// Orchestrates the update cycle from the spec:
///  - SDE: ensure loaded on startup, refresh when stale (game patches).
///  - Public contracts: every 30 min for the selected region (or every known-space region).
///  - Prices: hourly; volumes: ~daily (handled inside PriceService staleness).
///  - Own contracts: every 5 min across authed characters.
///  - Purge: hourly; rows gone 3 days after completed/expired.
/// </summary>
public class SyncScheduler : BackgroundService
{
    private readonly IServiceScopeFactory _scopes;
    private readonly SdeService _sde;
    private readonly SettingsService _settings;
    private readonly PublicContractSync _publicSync;
    private readonly OwnContractSync _ownSync;
    private readonly PriceService _prices;
    private readonly StaticDataCache _static;
    private readonly ILogger<SyncScheduler> _log;

    public string Status { get; private set; } = "starting";
    public bool SdeReady { get; private set; }
    public event Action? StatusChanged;

    public SyncScheduler(IServiceScopeFactory scopes, SdeService sde, SettingsService settings,
        PublicContractSync publicSync, OwnContractSync ownSync, PriceService prices,
        StaticDataCache staticData, ILogger<SyncScheduler> log)
    {
        _scopes = scopes;
        _sde = sde;
        _settings = settings;
        _publicSync = publicSync;
        _ownSync = ownSync;
        _prices = prices;
        _static = staticData;
        _log = log;
    }

    private void SetStatus(string s) { Status = s; StatusChanged?.Invoke(); }

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        try
        {
            using (var scope = _scopes.CreateScope())
            {
                var db = scope.ServiceProvider.GetRequiredService<AppDb>();
                await db.Database.EnsureCreatedAsync(ct);
                await UpgradeSchemaAsync(db, ct);
            }
            await _settings.LoadAsync(ct);

            SetStatus("loading static data");
            _sde.Progress += SetStatus;
            await _sde.EnsureLoadedAsync(ct);

            // A schema upgrade can leave derived columns (e.g. IsRig) unpopulated in an
            // otherwise-fresh SDE; rigs always exist, so zero rig rows means re-parse.
            using (var scope = _scopes.CreateScope())
            {
                var db = scope.ServiceProvider.GetRequiredService<AppDb>();
                if (await db.ItemTypes.AnyAsync(ct) && !await db.ItemTypes.AnyAsync(t => t.IsRig, ct))
                {
                    _log.LogInformation("Re-parsing SDE to populate rig data");
                    try { await _sde.LoadFromDiskAsync(ct); }
                    catch (FileNotFoundException) { await _sde.DownloadAndLoadAsync(ct); }
                }
            }

            if (!_static.Ready) await _static.LoadAsync(ct);
            try { await _sde.EnsureRegionsAsync(ct); }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // Region names only feed the picker; the trade hubs still resolve without them.
                _log.LogWarning("Region list unavailable: {Error}", ex.Message);
            }
            SdeReady = true;
            SetStatus("ready");
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _log.LogError(ex, "Startup failed");
            SetStatus("startup failed: " + ex.Message);
            return;
        }

        var lastPublic = DateTime.MinValue;
        var lastOwn = DateTime.MinValue;
        var lastPrices = DateTime.MinValue;
        var lastPurge = DateTime.MinValue;

        while (!ct.IsCancellationRequested)
        {
            try
            {
                var now = DateTime.UtcNow;

                if (now - lastPublic >= TimeSpan.FromMinutes(30))
                {
                    lastPublic = now;
                    await ScanPublicAsync(ct);
                }

                if (now - lastPrices >= TimeSpan.FromHours(1))
                {
                    lastPrices = now;
                    SetStatus("refreshing prices");
                    var needed = await _prices.GetNeededTypeIdsAsync(ct);
                    await _prices.RefreshPricesAsync(needed, ct);
                    await _prices.RefreshVolumesAsync(await _prices.GetVolumeNeededTypeIdsAsync(ct), ct: ct);
                    await _publicSync.EvaluateAllAsync(ct);
                    SetStatus("ready");
                }

                if (now - lastOwn >= TimeSpan.FromMinutes(5))
                {
                    lastOwn = now;
                    await _ownSync.SyncAllAsync(ct);
                }

                if (now - lastPurge >= TimeSpan.FromHours(1))
                {
                    lastPurge = now;
                    await PurgeAsync(ct);
                }
            }
            catch (OperationCanceledException) { break; }
            catch (Exception ex)
            {
                _log.LogError(ex, "Sync cycle error");
                SetStatus("sync error (will retry)");
                // Retry the public scan in ~2 min instead of waiting out the full 30.
                lastPublic = DateTime.UtcNow - TimeSpan.FromMinutes(28);
            }
            await Task.Delay(TimeSpan.FromSeconds(30), ct).ContinueWith(_ => { });
        }
    }

    /// <summary>Force an immediate public-contract rescan (e.g. after the user changes region).</summary>
    public Task RescanNowAsync(CancellationToken ct = default) => ScanPublicAsync(ct);

    private readonly SemaphoreSlim _scanLock = new(1, 1);

    /// <summary>
    /// Scan the selected region, or every known-space region for "All regions" (trade
    /// hubs first so the busiest markets are fresh soonest), then one evaluation pass.
    /// Serialized: a region switch mid-scan waits rather than running two scans at once.
    /// </summary>
    private async Task ScanPublicAsync(CancellationToken ct)
    {
        await _scanLock.WaitAsync(ct);
        try
        {
            var regionId = _static.ResolveRegion(_settings.Region);
            if (regionId != 0)
            {
                SetStatus("scanning public contracts");
                await _publicSync.FetchRegionAsync(regionId, ct);
            }
            else
            {
                var hubs = SdeService.TradeHubRegions.Select(_static.ResolveRegion).ToList();
                var all = hubs.Concat(_static.Regions.Keys.Except(hubs).Order()).ToList();
                for (var i = 0; i < all.Count; i++)
                {
                    // The user may switch back to a single region mid-sweep; stop at the next region.
                    if (_static.ResolveRegion(_settings.Region) != 0) break;
                    SetStatus($"scanning {_static.RegionName(all[i])} ({i + 1}/{all.Count})");
                    try { await _publicSync.FetchRegionAsync(all[i], ct); }
                    catch (Exception ex) when (ex is not OperationCanceledException)
                    {
                        // One bad region (ESI 5xx) must not starve the rest of the sweep.
                        _log.LogWarning("Region {Region} scan failed: {Error}", all[i], ex.Message);
                    }
                }
            }
            SetStatus("evaluating contracts");
            await _publicSync.FinalizeScanAsync(ct);
            SetStatus("ready");
        }
        finally { _scanLock.Release(); }
    }

    /// <summary>
    /// Additive schema upgrades for databases created by an older build. EnsureCreated
    /// only builds schema for brand-new files, so new tables/columns are applied here;
    /// each statement is safe to re-run (IF NOT EXISTS / duplicate-column swallowed).
    /// </summary>
    private static async Task UpgradeSchemaAsync(AppDb db, CancellationToken ct)
    {
        string[] statements =
        [
            """
            CREATE TABLE IF NOT EXISTS OwnContractItems (
                Id INTEGER PRIMARY KEY AUTOINCREMENT,
                OwnContractId INTEGER NOT NULL,
                TypeId INTEGER NOT NULL,
                Quantity INTEGER NOT NULL,
                IsIncluded INTEGER NOT NULL)
            """,
            "CREATE INDEX IF NOT EXISTS IX_OwnContractItems_OwnContractId ON OwnContractItems (OwnContractId)",
            "CREATE TABLE IF NOT EXISTS Regions (RegionId INTEGER NOT NULL PRIMARY KEY, Name TEXT NOT NULL)",
            "ALTER TABLE OwnContracts ADD COLUMN VolumeM3 REAL NOT NULL DEFAULT 0",
            "ALTER TABLE OwnContracts ADD COLUMN DaysToComplete INTEGER NOT NULL DEFAULT 0",
            "ALTER TABLE OwnContracts ADD COLUMN Buyout REAL NOT NULL DEFAULT 0",
            "ALTER TABLE OwnContracts ADD COLUMN ItemsFetched INTEGER NOT NULL DEFAULT 0",
            "ALTER TABLE ItemTypes ADD COLUMN IsRig INTEGER NOT NULL DEFAULT 0",
            "ALTER TABLE PublicContracts ADD COLUMN Collateral REAL NOT NULL DEFAULT 0",
            "ALTER TABLE PublicContracts ADD COLUMN Buyout REAL NOT NULL DEFAULT 0",
            "ALTER TABLE PublicContracts ADD COLUMN DestinationName TEXT NOT NULL DEFAULT ''",
            RewardColumnSql,
        ];
        var conn = await Data.BulkOps.OpenAsync(db, ct);
        foreach (var sql in statements)
        {
            try
            {
                await using var cmd = conn.CreateCommand();
                cmd.CommandText = sql;
                await cmd.ExecuteNonQueryAsync(ct);

                // Courier/auction terms were added after contracts were already cached; drop the
                // public-contract ETags once so the next scan re-upserts every row with them.
                if (sql == RewardColumnSql)
                {
                    await using var reset = conn.CreateCommand();
                    reset.CommandText = "DELETE FROM EsiEtags WHERE Url LIKE '/contracts/public/%'";
                    await reset.ExecuteNonQueryAsync(ct);
                }
            }
            catch (Microsoft.Data.Sqlite.SqliteException ex) when (ex.Message.Contains("duplicate column"))
            {
                // Column already present — fine.
            }
        }
    }

    private const string RewardColumnSql = "ALTER TABLE PublicContracts ADD COLUMN Reward REAL NOT NULL DEFAULT 0";

    /// <summary>Retention rule: purge contracts 3 days after they finished or should have finished.</summary>
    public async Task PurgeAsync(CancellationToken ct = default)
    {
        using var scope = _scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDb>();
        var cutoff = DateTime.UtcNow.AddDays(-3);
        var pub = await db.PublicContracts.Where(c => c.DateExpired < cutoff).ExecuteDeleteAsync(ct);
        var own = await db.OwnContracts.Where(o =>
            (o.DateCompleted ?? o.DateExpired) < cutoff && o.DateExpired < cutoff).ExecuteDeleteAsync(ct);
        // Orphaned items go with their contracts via cascade; also drop prices for types no longer referenced.
        if (pub > 0 || own > 0)
            _log.LogInformation("Purged {Pub} public and {Own} own contracts", pub, own);
    }
}
