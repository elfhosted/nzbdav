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
        try
        {
            var mapping = GetMapping(id);
            if (mapping == null)
            {
                Log.Warning("S3 ReadBlob: no mapping found for {BlobId} — blob was never written or mapping was lost", id);
                return null;
            }

            var key = $"{mapping.BlobType}-blobs/{mapping.Sha256Hash}";
            using var response = _s3.GetObjectAsync(_bucket, key).GetAwaiter().GetResult();
            var ms = new MemoryStream();
            response.ResponseStream.CopyTo(ms);
            ms.Position = 0;
            return ms;
        }
        catch (AmazonS3Exception ex) when (ex.StatusCode == System.Net.HttpStatusCode.NotFound)
        {
            Log.Warning("S3 ReadBlob: object not found in S3 for {BlobId}", id);
            return null;
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "S3 ReadBlob failed for {BlobId}", id);
            return null;
        }
    }

    public async Task<T?> ReadBlob<T>(Guid id)
    {
        var stream = ReadBlob(id);
        if (stream == null) return default;
        await using var s = stream;
        await using var decompressionStream = new DecompressionStream(s);
        return await MemoryPackSerializer.DeserializeAsync<T>(decompressionStream);
    }

    public void Delete(Guid id)
    {
        // Only remove the local mapping. Do NOT delete from S3 —
        // other pods may reference the same content-addressed blob.
        try
        {
            using var conn = OpenMappingConnection();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "DELETE FROM BlobKeyMappings WHERE BlobId = @id COLLATE NOCASE";
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
            cmd.CommandText = "SELECT Sha256Hash, BlobType FROM BlobKeyMappings WHERE BlobId = @id COLLATE NOCASE";
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
            Log.Warning(ex, "S3 GetMapping: query failed for {BlobId}", blobId);
            return null;
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

    private static string ComputeSha256(byte[] bytes)
    {
        var hash = SHA256.HashData(bytes);
        return Convert.ToHexStringLower(hash);
    }
}
