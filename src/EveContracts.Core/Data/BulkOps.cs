using EveContracts.Core.Models;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace EveContracts.Core.Data;

/// <summary>
/// Raw SQLite write paths for the hot loops. EF change tracking costs ~100 bytes
/// and several dictionary hops per tracked property; at 34k contracts per scan
/// cycle that dominates the local pipeline, so scans write through prepared
/// statements in a single transaction instead.
/// </summary>
public static class BulkOps
{
    public static async Task<SqliteConnection> OpenAsync(AppDb db, CancellationToken ct)
    {
        var conn = (SqliteConnection)db.Database.GetDbConnection();
        if (conn.State != System.Data.ConnectionState.Open) await conn.OpenAsync(ct);
        return conn;
    }

    /// <summary>Upsert scan results. Preserves FirstSeen/ItemsFetched/evaluation columns on conflict.</summary>
    public static async Task UpsertPublicContractsAsync(AppDb db, IReadOnlyList<PublicContract> rows, CancellationToken ct)
    {
        var conn = await OpenAsync(db, ct);
        await using var tx = (SqliteTransaction)await conn.BeginTransactionAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = """
            INSERT INTO PublicContracts
                (ContractId, RegionId, Type, Title, Price, StartLocationId, SolarSystemId, SystemName,
                 StationName, SecurityStatus, JumpsToJita, DateIssued, DateExpired, VolumeM3,
                 FirstSeen, LastSeen, ItemsFetched,
                 JitaSellValue, Fees, Hauling, NetProfit, Margin, Verdict, FlagsJson)
            VALUES (@id, @region, @type, @title, @price, @startLoc, @sysId, @sysName,
                    @station, @sec, @jumps, @issued, @expired, @vol,
                    @firstSeen, @lastSeen, 0,
                    0, 0, 0, 0, 0, 'PENDING', '[]')
            ON CONFLICT(ContractId) DO UPDATE SET
                Type = excluded.Type, Title = excluded.Title, Price = excluded.Price,
                StartLocationId = excluded.StartLocationId, SolarSystemId = excluded.SolarSystemId,
                SystemName = excluded.SystemName, StationName = excluded.StationName,
                SecurityStatus = excluded.SecurityStatus, JumpsToJita = excluded.JumpsToJita,
                DateIssued = excluded.DateIssued, DateExpired = excluded.DateExpired,
                VolumeM3 = excluded.VolumeM3, LastSeen = excluded.LastSeen
            """;
        var pId = cmd.Parameters.Add("@id", SqliteType.Integer);
        var pRegion = cmd.Parameters.Add("@region", SqliteType.Integer);
        var pType = cmd.Parameters.Add("@type", SqliteType.Text);
        var pTitle = cmd.Parameters.Add("@title", SqliteType.Text);
        var pPrice = cmd.Parameters.Add("@price", SqliteType.Real);
        var pStartLoc = cmd.Parameters.Add("@startLoc", SqliteType.Integer);
        var pSysId = cmd.Parameters.Add("@sysId", SqliteType.Integer);
        var pSysName = cmd.Parameters.Add("@sysName", SqliteType.Text);
        var pStation = cmd.Parameters.Add("@station", SqliteType.Text);
        var pSec = cmd.Parameters.Add("@sec", SqliteType.Real);
        var pJumps = cmd.Parameters.Add("@jumps", SqliteType.Integer);
        var pIssued = cmd.Parameters.Add("@issued", SqliteType.Text);
        var pExpired = cmd.Parameters.Add("@expired", SqliteType.Text);
        var pVol = cmd.Parameters.Add("@vol", SqliteType.Real);
        var pFirstSeen = cmd.Parameters.Add("@firstSeen", SqliteType.Text);
        var pLastSeen = cmd.Parameters.Add("@lastSeen", SqliteType.Text);
        cmd.Prepare();

        foreach (var r in rows)
        {
            pId.Value = r.ContractId;
            pRegion.Value = r.RegionId;
            pType.Value = r.Type;
            pTitle.Value = r.Title;
            pPrice.Value = r.Price;
            pStartLoc.Value = r.StartLocationId;
            pSysId.Value = r.SolarSystemId;
            pSysName.Value = r.SystemName;
            pStation.Value = r.StationName;
            pSec.Value = r.SecurityStatus;
            pJumps.Value = r.JumpsToJita;
            pIssued.Value = Dt(r.DateIssued);
            pExpired.Value = Dt(r.DateExpired);
            pVol.Value = r.VolumeM3;
            pFirstSeen.Value = Dt(r.FirstSeen);
            pLastSeen.Value = Dt(r.LastSeen);
            await cmd.ExecuteNonQueryAsync(ct);
        }
        await tx.CommitAsync(ct);
    }

    /// <summary>Insert fetched items and mark their contracts ItemsFetched, one transaction.</summary>
    public static async Task InsertContractItemsAsync(AppDb db,
        IReadOnlyDictionary<long, List<ContractItem>> itemsByContract, CancellationToken ct)
    {
        var conn = await OpenAsync(db, ct);
        await using var tx = (SqliteTransaction)await conn.BeginTransactionAsync(ct);

        await using (var ins = conn.CreateCommand())
        {
            ins.Transaction = tx;
            ins.CommandText = "INSERT INTO ContractItems (ContractId, TypeId, Quantity, IsIncluded) VALUES (@c, @t, @q, @i)";
            var pc = ins.Parameters.Add("@c", SqliteType.Integer);
            var pt = ins.Parameters.Add("@t", SqliteType.Integer);
            var pq = ins.Parameters.Add("@q", SqliteType.Integer);
            var pi = ins.Parameters.Add("@i", SqliteType.Integer);
            ins.Prepare();
            foreach (var (contractId, items) in itemsByContract)
            {
                foreach (var it in items)
                {
                    pc.Value = contractId;
                    pt.Value = it.TypeId;
                    pq.Value = it.Quantity;
                    pi.Value = it.IsIncluded ? 1 : 0;
                    await ins.ExecuteNonQueryAsync(ct);
                }
            }
        }

        await using (var mark = conn.CreateCommand())
        {
            mark.Transaction = tx;
            mark.CommandText = "UPDATE PublicContracts SET ItemsFetched = 1 WHERE ContractId = @c";
            var pc = mark.Parameters.Add("@c", SqliteType.Integer);
            mark.Prepare();
            foreach (var contractId in itemsByContract.Keys)
            {
                pc.Value = contractId;
                await mark.ExecuteNonQueryAsync(ct);
            }
        }
        await tx.CommitAsync(ct);
    }

    /// <summary>Write evaluation results back — only the eval columns, prepared, one transaction.</summary>
    public static async Task UpdateEvaluationsAsync(AppDb db, IReadOnlyList<(long ContractId, Services.EvalResult R)> results, CancellationToken ct)
    {
        if (results.Count == 0) return;
        var conn = await OpenAsync(db, ct);
        await using var tx = (SqliteTransaction)await conn.BeginTransactionAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = """
            UPDATE PublicContracts SET
                JitaSellValue = @sell, Fees = @fees, Hauling = @haul,
                NetProfit = @profit, Margin = @margin, Verdict = @verdict, FlagsJson = @flags
            WHERE ContractId = @id
            """;
        var pSell = cmd.Parameters.Add("@sell", SqliteType.Real);
        var pFees = cmd.Parameters.Add("@fees", SqliteType.Real);
        var pHaul = cmd.Parameters.Add("@haul", SqliteType.Real);
        var pProfit = cmd.Parameters.Add("@profit", SqliteType.Real);
        var pMargin = cmd.Parameters.Add("@margin", SqliteType.Real);
        var pVerdict = cmd.Parameters.Add("@verdict", SqliteType.Text);
        var pFlags = cmd.Parameters.Add("@flags", SqliteType.Text);
        var pId = cmd.Parameters.Add("@id", SqliteType.Integer);
        cmd.Prepare();

        foreach (var (id, r) in results)
        {
            pSell.Value = r.JitaSellValue;
            pFees.Value = r.Fees;
            pHaul.Value = r.Hauling;
            pProfit.Value = r.NetProfit;
            pMargin.Value = r.Margin;
            pVerdict.Value = r.Verdict;
            pFlags.Value = r.FlagsJson;
            pId.Value = id;
            await cmd.ExecuteNonQueryAsync(ct);
        }
        await tx.CommitAsync(ct);
    }

    /// <summary>Upsert Jita quotes; volume columns untouched for existing rows.</summary>
    public static async Task UpsertPriceQuotesAsync(AppDb db,
        IReadOnlyList<(int TypeId, double Sell, double Buy)> quotes, DateTime updatedAt, CancellationToken ct)
    {
        if (quotes.Count == 0) return;
        var conn = await OpenAsync(db, ct);
        await using var tx = (SqliteTransaction)await conn.BeginTransactionAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = """
            INSERT INTO Prices (TypeId, JitaSell, JitaBuy, PrevDayVolume, PricesUpdatedAt, VolumeUpdatedAt)
            VALUES (@t, @s, @b, 0, @at, '0001-01-01 00:00:00')
            ON CONFLICT(TypeId) DO UPDATE SET JitaSell = @s, JitaBuy = @b, PricesUpdatedAt = @at
            """;
        var pt = cmd.Parameters.Add("@t", SqliteType.Integer);
        var ps = cmd.Parameters.Add("@s", SqliteType.Real);
        var pb = cmd.Parameters.Add("@b", SqliteType.Real);
        cmd.Parameters.AddWithValue("@at", Dt(updatedAt));
        cmd.Prepare();
        foreach (var (t, s, b) in quotes)
        {
            pt.Value = t; ps.Value = s; pb.Value = b;
            await cmd.ExecuteNonQueryAsync(ct);
        }
        await tx.CommitAsync(ct);
    }

    /// <summary>Upsert previous-day volumes; quote columns untouched for existing rows.</summary>
    public static async Task UpsertPriceVolumesAsync(AppDb db,
        IReadOnlyList<(int TypeId, double Volume)> volumes, DateTime updatedAt, CancellationToken ct)
    {
        if (volumes.Count == 0) return;
        var conn = await OpenAsync(db, ct);
        await using var tx = (SqliteTransaction)await conn.BeginTransactionAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = """
            INSERT INTO Prices (TypeId, JitaSell, JitaBuy, PrevDayVolume, PricesUpdatedAt, VolumeUpdatedAt)
            VALUES (@t, 0, 0, @v, '0001-01-01 00:00:00', @at)
            ON CONFLICT(TypeId) DO UPDATE SET PrevDayVolume = @v, VolumeUpdatedAt = @at
            """;
        var pt = cmd.Parameters.Add("@t", SqliteType.Integer);
        var pv = cmd.Parameters.Add("@v", SqliteType.Real);
        cmd.Parameters.AddWithValue("@at", Dt(updatedAt));
        cmd.Prepare();
        foreach (var (t, v) in volumes)
        {
            pt.Value = t; pv.Value = v;
            await cmd.ExecuteNonQueryAsync(ct);
        }
        await tx.CommitAsync(ct);
    }

    /// <summary>Replace the SDE tables in one transaction with prepared inserts.</summary>
    public static async Task ReplaceStaticDataAsync(AppDb db,
        IReadOnlyList<ItemType> types, IReadOnlyList<SolarSystem> systems, IReadOnlyList<Station> stations,
        CancellationToken ct)
    {
        var conn = await OpenAsync(db, ct);
        await using var tx = (SqliteTransaction)await conn.BeginTransactionAsync(ct);

        async Task Exec(string sql)
        {
            await using var c = conn.CreateCommand();
            c.Transaction = tx;
            c.CommandText = sql;
            await c.ExecuteNonQueryAsync(ct);
        }
        await Exec("DELETE FROM ItemTypes");
        await Exec("DELETE FROM SolarSystems");
        await Exec("DELETE FROM Stations");

        await using (var cmd = conn.CreateCommand())
        {
            cmd.Transaction = tx;
            cmd.CommandText = "INSERT INTO ItemTypes (TypeId, Name, GroupId, CategoryId, Volume, PackagedVolume) VALUES (@a,@b,@c,@d,@e,@f)";
            var pa = cmd.Parameters.Add("@a", SqliteType.Integer);
            var pb = cmd.Parameters.Add("@b", SqliteType.Text);
            var pc = cmd.Parameters.Add("@c", SqliteType.Integer);
            var pd = cmd.Parameters.Add("@d", SqliteType.Integer);
            var pe = cmd.Parameters.Add("@e", SqliteType.Real);
            var pf = cmd.Parameters.Add("@f", SqliteType.Real);
            cmd.Prepare();
            foreach (var t in types)
            {
                pa.Value = t.TypeId; pb.Value = t.Name; pc.Value = t.GroupId;
                pd.Value = t.CategoryId; pe.Value = t.Volume; pf.Value = t.PackagedVolume;
                await cmd.ExecuteNonQueryAsync(ct);
            }
        }
        await using (var cmd = conn.CreateCommand())
        {
            cmd.Transaction = tx;
            cmd.CommandText = "INSERT INTO SolarSystems (SolarSystemId, Name, RegionId, Security, JumpsToJita) VALUES (@a,@b,@c,@d,@e)";
            var pa = cmd.Parameters.Add("@a", SqliteType.Integer);
            var pb = cmd.Parameters.Add("@b", SqliteType.Text);
            var pc = cmd.Parameters.Add("@c", SqliteType.Integer);
            var pd = cmd.Parameters.Add("@d", SqliteType.Real);
            var pe = cmd.Parameters.Add("@e", SqliteType.Integer);
            cmd.Prepare();
            foreach (var s in systems)
            {
                pa.Value = s.SolarSystemId; pb.Value = s.Name; pc.Value = s.RegionId;
                pd.Value = s.Security; pe.Value = s.JumpsToJita;
                await cmd.ExecuteNonQueryAsync(ct);
            }
        }
        await using (var cmd = conn.CreateCommand())
        {
            cmd.Transaction = tx;
            cmd.CommandText = "INSERT INTO Stations (StationId, Name, SolarSystemId) VALUES (@a,@b,@c)";
            var pa = cmd.Parameters.Add("@a", SqliteType.Integer);
            var pb = cmd.Parameters.Add("@b", SqliteType.Text);
            var pc = cmd.Parameters.Add("@c", SqliteType.Integer);
            cmd.Prepare();
            foreach (var s in stations)
            {
                pa.Value = s.StationId; pb.Value = s.Name; pc.Value = s.SolarSystemId;
                await cmd.ExecuteNonQueryAsync(ct);
            }
        }
        await tx.CommitAsync(ct);
    }

    // SQLite stores DateTime as ISO-8601 text when written by EF; match that format
    // so EF reads back what we write.
    private static string Dt(DateTime dt) => dt.ToString("yyyy-MM-dd HH:mm:ss.fffffff", System.Globalization.CultureInfo.InvariantCulture);
}
