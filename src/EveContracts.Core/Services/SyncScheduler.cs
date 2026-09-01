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
///  - Public contracts: every 30 min for the enabled region.
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
            if (!_static.Ready) await _static.LoadAsync(ct);
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
                    SetStatus("scanning public contracts");
                    var regionId = SdeService.Regions.GetValueOrDefault(_settings.Region, Esi.EsiClient.TheForgeRegionId);
                    await _publicSync.SyncRegionAsync(regionId, ct);
                    SetStatus("ready");
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
    public async Task RescanNowAsync(CancellationToken ct = default)
    {
        var regionId = SdeService.Regions.GetValueOrDefault(_settings.Region, Esi.EsiClient.TheForgeRegionId);
        SetStatus("scanning public contracts");
        await _publicSync.SyncRegionAsync(regionId, ct);
        SetStatus("ready");
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
            "ALTER TABLE OwnContracts ADD COLUMN VolumeM3 REAL NOT NULL DEFAULT 0",
            "ALTER TABLE OwnContracts ADD COLUMN DaysToComplete INTEGER NOT NULL DEFAULT 0",
            "ALTER TABLE OwnContracts ADD COLUMN Buyout REAL NOT NULL DEFAULT 0",
            "ALTER TABLE OwnContracts ADD COLUMN ItemsFetched INTEGER NOT NULL DEFAULT 0",
        ];
        var conn = await Data.BulkOps.OpenAsync(db, ct);
        foreach (var sql in statements)
        {
            try
            {
                await using var cmd = conn.CreateCommand();
                cmd.CommandText = sql;
                await cmd.ExecuteNonQueryAsync(ct);
            }
            catch (Microsoft.Data.Sqlite.SqliteException ex) when (ex.Message.Contains("duplicate column"))
            {
                // Column already present — fine.
            }
        }
    }

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
