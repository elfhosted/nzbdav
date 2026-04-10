using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using NzbWebDAV.Database.Interceptors;
using NzbWebDAV.Database.Models;
using Serilog;

namespace NzbWebDAV.Database;

/// <summary>
/// Minimal DbContext for the BlobKeyMappings table.
/// Uses the same SQLite database but creates the table via raw SQL
/// (CREATE TABLE IF NOT EXISTS) to avoid polluting upstream EF Core migrations.
/// </summary>
public class BlobKeyMappingDbContext : DbContext
{
    private static readonly string DatabaseFilePath = DavDatabaseContext.DatabaseFilePath;

    public DbSet<BlobKeyMapping> BlobKeyMappings => Set<BlobKeyMapping>();

    protected override void OnConfiguring(DbContextOptionsBuilder options)
    {
        options.UseSqlite($"Data Source={DatabaseFilePath}")
               .AddInterceptors(new SqliteForeignKeyEnabler());
    }

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<BlobKeyMapping>(e =>
        {
            e.ToTable("BlobKeyMappings");
            e.HasKey(m => m.BlobId);
            e.Property(m => m.BlobId).HasColumnType("TEXT");
            e.Property(m => m.Sha256Hash).IsRequired();
            e.Property(m => m.BlobType).IsRequired();
            e.HasIndex(m => m.Sha256Hash);
        });
    }

    /// <summary>
    /// Creates the BlobKeyMappings table if it doesn't exist.
    /// Called once at startup when S3 is configured.
    /// </summary>
    public static async Task EnsureTableCreatedAsync()
    {
        await using var ctx = new BlobKeyMappingDbContext();
        await ctx.Database.ExecuteSqlRawAsync("PRAGMA journal_mode=WAL");
        await ctx.Database.ExecuteSqlRawAsync(@"
            CREATE TABLE IF NOT EXISTS BlobKeyMappings (
                BlobId TEXT NOT NULL PRIMARY KEY COLLATE NOCASE,
                Sha256Hash TEXT NOT NULL,
                BlobType TEXT NOT NULL
            );
            CREATE INDEX IF NOT EXISTS IX_BlobKeyMappings_Sha256Hash
                ON BlobKeyMappings (Sha256Hash);
        ");

        await MigrateBlobFormatKeysAsync();

        var count = ctx.BlobKeyMappings.Count();
        Log.Warning("S3 blob store: {Count} BlobKeyMappings entries found at startup", count);
    }

    /// <summary>
    /// Converts any BLOB-format BlobId rows to TEXT strings.
    /// </summary>
    private static async Task MigrateBlobFormatKeysAsync()
    {
        await using var conn = new SqliteConnection($"Data Source={DavDatabaseContext.DatabaseFilePath}");
        await conn.OpenAsync();

        await using var selectCmd = conn.CreateCommand();
        selectCmd.CommandText = "SELECT BlobId, Sha256Hash, BlobType FROM BlobKeyMappings WHERE TYPEOF(BlobId) = 'blob'";

        var toMigrate = new List<(byte[] blobBytes, string sha256, string blobType)>();
        await using (var reader = await selectCmd.ExecuteReaderAsync())
        {
            while (await reader.ReadAsync())
            {
                var blobBytes = (byte[])reader[0];
                toMigrate.Add((blobBytes, reader.GetString(1), reader.GetString(2)));
            }
        }

        if (toMigrate.Count == 0) return;

        Log.Warning("Migrating {Count} BLOB-format BlobKeyMappings to TEXT", toMigrate.Count);
        foreach (var (blobBytes, sha256, blobType) in toMigrate)
        {
            if (blobBytes.Length != 16) continue;
            var guid = new Guid(blobBytes);

            await using var updateCmd = conn.CreateCommand();
            updateCmd.CommandText = "UPDATE BlobKeyMappings SET BlobId = @textId WHERE BlobId = @blobId";
            updateCmd.Parameters.AddWithValue("@textId", guid.ToString());
            updateCmd.Parameters.Add(new SqliteParameter("@blobId", SqliteType.Blob) { Value = blobBytes });
            await updateCmd.ExecuteNonQueryAsync();
        }
        Log.Warning("Migrated {Count} BlobKeyMappings from BLOB to TEXT format", toMigrate.Count);
    }
}
