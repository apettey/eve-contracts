using System.Data.Common;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace EveContracts.Core.Data;

/// <summary>
/// WAL journaling lets the UI read while sync writes; synchronous=NORMAL is the
/// recommended pairing (fsync per checkpoint, not per commit). journal_mode is
/// persisted in the db file but harmless to re-issue; synchronous is per-connection.
/// </summary>
public class SqlitePragmaInterceptor : DbConnectionInterceptor
{
    public override void ConnectionOpened(DbConnection connection, ConnectionEndEventData eventData)
    {
        using var cmd = connection.CreateCommand();
        cmd.CommandText = "PRAGMA journal_mode=WAL; PRAGMA synchronous=NORMAL; PRAGMA temp_store=MEMORY;";
        cmd.ExecuteNonQuery();
    }

    public override async Task ConnectionOpenedAsync(DbConnection connection, ConnectionEndEventData eventData, CancellationToken cancellationToken = default)
    {
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = "PRAGMA journal_mode=WAL; PRAGMA synchronous=NORMAL; PRAGMA temp_store=MEMORY;";
        await cmd.ExecuteNonQueryAsync(cancellationToken);
    }
}
