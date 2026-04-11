using System.Security.Cryptography;
using Amazon.S3;
using Amazon.S3.Model;
using MemoryPack;
using Microsoft.Data.Sqlite;
using NzbWebDAV.Database.Models;
using Serilog;
using ZstdSharp;

namespace NzbWebDAV.Database;

/// <summary>
/// S3-compatible blob store with content-addressed (SHA256) keys for
/// multi-tenant deduplication. Uses BlobKeyMappings table to map local
/// GUIDs to SHA256 object keys in S3.
/// </summary>
public class S3BlobStore : IBlobStore
{
    private static readonly int CompressionLevel = 1;
    private readonly AmazonS3Client _s3;
    private readonly string _bucket;

    public S3BlobStore(string endpoint, string bucket, string accessKey, string secretKey, string region)
    {
        _bucket = bucket;
        _s3 = new AmazonS3Client(accessKey, secretKey, new AmazonS3Config
        {
            ServiceURL = endpoint,
            ForcePathStyle = true,
            AuthenticationRegion = region
        });
    }

    public async Task WriteBlob(Guid id, Stream stream)
    {
        using var buffer = new MemoryStream();
        await stream.CopyToAsync(buffer);
        var bytes = buffer.ToArray();
        var sha256 = ComputeSha256(bytes);
        var key = $"nzb-blobs/{sha256}";

        await UploadIfAbsentAsync(key, bytes);
        await UpsertMappingAsync(id, sha256, "nzb");
        Log.Information("S3 WriteBlob (stream): {BlobId} → {Key}, {Bytes} bytes", id, key, bytes.Length);
    }

    public async Task WriteBlob<T>(Guid id, T blob)
    {
        using var buffer = new MemoryStream();
        await using (var compressionStream = new CompressionStream(buffer, CompressionLevel, leaveOpen: true))
        {
            await MemoryPackSerializer.SerializeAsync(compressionStream, blob);
        }
        var bytes = buffer.ToArray();
        var sha256 = ComputeSha256(bytes);
        var key = $"file-blobs/{sha256}";

        await UploadIfAbsentAsync(key, bytes);
        await UpsertMappingAsync(id, sha256, "file");
        Log.Information("S3 WriteBlob<T>: {BlobId} → {Key}, {Bytes} bytes", id, key, bytes.Length);
    }

    public Stream? ReadBlob(Guid id)
    {
        // Synchronous wrapper — used for NZB XML streams (QueueItem blobs).
        var mapping = GetMapping(id);
        if (mapping == null) return null;

        var key = $"{mapping.BlobType}-blobs/{mapping.Sha256Hash}";
        try
        {
            var response = _s3.GetObjectAsync(_bucket, key).GetAwaiter().GetResult();
            var ms = new MemoryStream();
            response.ResponseStream.CopyTo(ms);
            response.Dispose();
            ms.Position = 0;
            return ms;
        }
        catch (AmazonS3Exception ex) when (ex.StatusCode == System.Net.HttpStatusCode.NotFound)
        {
            Log.Warning("S3 ReadBlob: object {Key} not found in S3 for {BlobId}", key, id);
            return null;
        }
        // All other S3 errors (auth, connectivity, timeout) propagate — do NOT swallow
    }

    public async Task<T?> ReadBlob<T>(Guid id)
    {
        var mapping = GetMapping(id);
        if (mapping == null) return default;

        var key = $"{mapping.BlobType}-blobs/{mapping.Sha256Hash}";
        try
        {
            using var response = await _s3.GetObjectAsync(_bucket, key);
            var ms = new MemoryStream();
            await response.ResponseStream.CopyToAsync(ms);
            ms.Position = 0;
            await using var decompressionStream = new DecompressionStream(ms);
            return await MemoryPackSerializer.DeserializeAsync<T>(decompressionStream);
        }
        catch (AmazonS3Exception ex) when (ex.StatusCode == System.Net.HttpStatusCode.NotFound)
        {
            Log.Warning("S3 ReadBlob<T>: object {Key} not found in S3 for {BlobId}", key, id);
            return default;
        }
        // All other S3 errors propagate
    }

    public void Delete(Guid id)
    {
        // Only remove the local mapping. Do NOT delete from S3 —
        // other pods may reference the same content-addressed blob.
        try
        {
            using var conn = OpenMappingConnection();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "DELETE FROM BlobKeyMappings WHERE BlobId = @id";
            cmd.Parameters.AddWithValue("@id", id.ToString());
            cmd.ExecuteNonQuery();
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Failed to remove BlobKeyMapping for {BlobId}", id);
        }
    }

    private async Task UploadIfAbsentAsync(string key, byte[] bytes)
    {
        try
        {
            await _s3.GetObjectMetadataAsync(_bucket, key);
            return; // Already exists — dedup hit
        }
        catch (AmazonS3Exception ex) when (ex.StatusCode == System.Net.HttpStatusCode.NotFound)
        {
            // Expected — object doesn't exist yet
        }

        using var uploadStream = new MemoryStream(bytes);
        await _s3.PutObjectAsync(new PutObjectRequest
        {
            BucketName = _bucket,
            Key = key,
            InputStream = uploadStream,
            ContentType = "application/octet-stream",
            ServerSideEncryptionMethod = ServerSideEncryptionMethod.None
        });
    }

    private static async Task UpsertMappingAsync(Guid blobId, string sha256, string blobType)
    {
        await using var conn = new SqliteConnection($"Data Source={DavDatabaseContext.DatabaseFilePath}");
        await conn.OpenAsync();
        await using var pragmaCmd = conn.CreateCommand();
        pragmaCmd.CommandText = "PRAGMA busy_timeout = 30000";
        await pragmaCmd.ExecuteNonQueryAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = @"
            INSERT INTO BlobKeyMappings (BlobId, Sha256Hash, BlobType)
            VALUES (@id, @sha256, @blobType)
            ON CONFLICT(BlobId) DO UPDATE SET Sha256Hash = excluded.Sha256Hash, BlobType = excluded.BlobType";
        cmd.Parameters.AddWithValue("@id", blobId.ToString());
        cmd.Parameters.AddWithValue("@sha256", sha256);
        cmd.Parameters.AddWithValue("@blobType", blobType);
        await cmd.ExecuteNonQueryAsync();
    }

    private static BlobKeyMapping? GetMapping(Guid blobId)
    {
        try
        {
            using var conn = OpenMappingConnection();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT Sha256Hash, BlobType FROM BlobKeyMappings WHERE BlobId = @id";
            cmd.Parameters.AddWithValue("@id", blobId.ToString());
            using var reader = cmd.ExecuteReader();
            if (!reader.Read()) return null;
            return new BlobKeyMapping
            {
                BlobId = blobId,
                Sha256Hash = reader.GetString(0),
                BlobType = reader.GetString(1)
            };
        }
        catch (Exception ex)
        {
            Log.Error(ex, "S3 GetMapping: query failed for {BlobId} — this will cause blob read failure", blobId);
            throw;
        }
    }

    private static SqliteConnection OpenMappingConnection()
    {
        var conn = new SqliteConnection($"Data Source={DavDatabaseContext.DatabaseFilePath}");
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "PRAGMA busy_timeout = 30000";
        cmd.ExecuteNonQuery();
        return conn;
    }

    /// <summary>
    /// One-time repair: finds DavItems whose FileBlobId has no BlobKeyMapping,
    /// checks if the blob exists on the local filesystem (written by upstream
    /// code before S3 patches were applied), and migrates it to S3.
    /// </summary>
    public async Task RepairOrphanedBlobsAsync()
    {
        try
        {
            await using var conn = new SqliteConnection($"Data Source={DavDatabaseContext.DatabaseFilePath}");
            await conn.OpenAsync();

            // Find FileBlobIds with no mapping
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = @"
                SELECT DISTINCT d.FileBlobId FROM DavItems d
                LEFT JOIN BlobKeyMappings b ON UPPER(d.FileBlobId) = UPPER(b.BlobId)
                WHERE d.FileBlobId IS NOT NULL AND b.BlobId IS NULL";
            var orphanIds = new List<Guid>();
            await using (var reader = await cmd.ExecuteReaderAsync())
            {
                while (await reader.ReadAsync())
                    orphanIds.Add(Guid.Parse(reader.GetString(0)));
            }

            if (orphanIds.Count == 0)
            {
                Log.Information("S3 repair: no orphaned blobs to recover");
                return;
            }

            Log.Warning("S3 repair: scanning local filesystem for {Count} orphaned blobs...", orphanIds.Count);

            // Fast pass: check which blobs exist on local filesystem
            var localBlobs = new List<Guid>();
            var notFound = 0;
            foreach (var blobId in orphanIds)
            {
                var stream = BlobStore.ReadBlob(blobId);
                if (stream != null)
                {
                    stream.Dispose();
                    localBlobs.Add(blobId);
                }
                else
                {
                    notFound++;
                }
            }

            Log.Warning("S3 repair: {Local} found on local filesystem, {NotFound} unrecoverable, out of {Total} orphaned",
                localBlobs.Count, notFound, orphanIds.Count);

            // Upload recoverable blobs to S3
            var recovered = 0;
            foreach (var blobId in localBlobs)
            {
                using var localStream = BlobStore.ReadBlob(blobId)!;
                using var ms = new MemoryStream();
                await localStream.CopyToAsync(ms);
                var bytes = ms.ToArray();
                var sha256 = ComputeSha256(bytes);
                var key = $"file-blobs/{sha256}";

                await UploadIfAbsentAsync(key, bytes);
                await UpsertMappingAsync(blobId, sha256, "file");
                recovered++;
                if (recovered % 100 == 0)
                    Log.Information("S3 repair: uploaded {Recovered}/{Total} blobs to S3", recovered, localBlobs.Count);
            }

            // Remove unrecoverable DavItems so users can re-download
            if (notFound > 0)
            {
                await using var deleteConn = new SqliteConnection($"Data Source={DavDatabaseContext.DatabaseFilePath}");
                await deleteConn.OpenAsync();
                await using var pragmaCmd = deleteConn.CreateCommand();
                pragmaCmd.CommandText = "PRAGMA busy_timeout = 30000";
                await pragmaCmd.ExecuteNonQueryAsync();
                await using var deleteCmd = deleteConn.CreateCommand();
                deleteCmd.CommandText = @"
                    DELETE FROM DavItems WHERE FileBlobId IN (
                        SELECT d.FileBlobId FROM DavItems d
                        LEFT JOIN BlobKeyMappings b ON UPPER(d.FileBlobId) = UPPER(b.BlobId)
                        WHERE d.FileBlobId IS NOT NULL AND b.BlobId IS NULL
                    )";
                var deleted = await deleteCmd.ExecuteNonQueryAsync();
                Log.Warning("S3 repair: removed {Deleted} unrecoverable DavItems (users can re-download these)", deleted);
            }

            Log.Warning("S3 repair complete: {Recovered} recovered, {NotFound} removed", recovered, notFound);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "S3 repair failed — non-fatal, will retry on next startup");
        }
    }

    private static string ComputeSha256(byte[] bytes)
    {
        var hash = SHA256.HashData(bytes);
        return Convert.ToHexStringLower(hash);
    }
}
