using System.Data.Common;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Serilog;

namespace NzbWebDAV.Database.Interceptors;

public class SqliteForeignKeyEnabler : DbConnectionInterceptor
{
    public override void ConnectionOpened(DbConnection connection, ConnectionEndEventData eventData)
    {
        try
        {
            using var command = connection.CreateCommand();

            command.CommandText = "PRAGMA foreign_keys = ON;";
            command.ExecuteNonQuery();

            var connStr = connection.ConnectionString ?? string.Empty;
            var isExplicitlyReadOnly = connStr.IndexOf("mode=readonly", StringComparison.OrdinalIgnoreCase) >= 0
                                     || connStr.IndexOf("mode=read-only", StringComparison.OrdinalIgnoreCase) >= 0
                                     || connStr.IndexOf("mode=ReadOnly", StringComparison.OrdinalIgnoreCase) >= 0
                                     || connStr.IndexOf("Mode=ReadOnly", StringComparison.OrdinalIgnoreCase) >= 0;

            if (!isExplicitlyReadOnly)
            {
                try
                {
                    command.CommandText = "PRAGMA journal_mode = WAL;";
                    _ = command.ExecuteScalar();

                    command.CommandText = "PRAGMA synchronous = NORMAL;";
                    command.ExecuteNonQuery();
                }
                catch (Exception ex)
                {
                    Log.Warning(ex, "Could not set WAL/synchronous PRAGMA on SQLite connection; database may be read-only or on a read-only filesystem.");
                }
            }
        }
        catch (Exception e)
        {
            Log.Warning(e, "SQLite connection opened but PRAGMA commands failed. Continuing without PRAGMA changes.");
        }
    }
}
