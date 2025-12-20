using System.IO;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using NzbWebDAV.Config;
using NzbWebDAV.Database.Models;
using Serilog;

namespace NzbWebDAV.Services;

public class NzbStorageService(ConfigManager configManager)
{
    private const string DefaultCompression = "gzip";

    public readonly record struct StoredNzbMetadata(
        string RelativePath,
        string Compression,
        long LengthBytes,
        string Sha256
    );

    public async Task<StoredNzbMetadata> WriteAsync(Guid queueItemId, string nzbContents, CancellationToken ct)
    {
        var storageRoot = configManager.GetNzbStoragePath();
        Directory.CreateDirectory(storageRoot);
        var fileName = $"{queueItemId:N}.nzb.gz";
        var fullPath = Path.Combine(storageRoot, fileName);
        var tempPath = fullPath + ".tmp";
        var sourceBytes = Encoding.UTF8.GetBytes(nzbContents);
        var sha256 = Convert.ToHexString(SHA256.HashData(sourceBytes));
        try
        {
            await using (var fileStream = new FileStream(tempPath, FileMode.Create, FileAccess.Write, FileShare.None))
            await using (var gzip = new GZipStream(fileStream, CompressionLevel.SmallestSize))
            {
                await gzip.WriteAsync(sourceBytes, ct).ConfigureAwait(false);
            }

            if (File.Exists(fullPath)) File.Delete(fullPath);
            File.Move(tempPath, fullPath);
            var length = new FileInfo(fullPath).Length;
            return new StoredNzbMetadata(fileName, DefaultCompression, length, sha256);
        }
        catch
        {
            if (File.Exists(tempPath)) File.Delete(tempPath);
            throw;
        }
    }

    public async Task<string> ReadAsStringAsync(QueueNzbContents queueNzbContents, CancellationToken ct)
    {
        if (!queueNzbContents.HasExternalPayload)
        {
            return queueNzbContents.NzbContents;
        }

        var fullPath = ResolveFullPath(queueNzbContents.ExternalPath!);
        if (!File.Exists(fullPath))
        {
            Log.Warning(
                "NZB payload missing on disk for queue item {QueueItemId}. Expected path: {Path}",
                queueNzbContents.Id,
                fullPath);
            throw new FileNotFoundException($"NZB payload is missing on disk: {fullPath}");
        }

        await using var fileStream = new FileStream(fullPath, FileMode.Open, FileAccess.Read, FileShare.Read);
        await using var decompressed = new GZipStream(fileStream, CompressionMode.Decompress);
        using var reader = new StreamReader(decompressed, Encoding.UTF8);
#if NET9_0_OR_GREATER
        return await reader.ReadToEndAsync(ct).ConfigureAwait(false);
#else
        return await reader.ReadToEndAsync().ConfigureAwait(false);
#endif
    }

    public async Task<Stream> OpenReadableStreamAsync(QueueNzbContents queueNzbContents, CancellationToken ct)
    {
        var nzbText = await ReadAsStringAsync(queueNzbContents, ct).ConfigureAwait(false);
        return new MemoryStream(Encoding.UTF8.GetBytes(nzbText));
    }

    public Task DeleteAsync(QueueNzbContents queueNzbContents, CancellationToken ct)
    {
        if (!queueNzbContents.HasExternalPayload)
        {
            return Task.CompletedTask;
        }

        try
        {
            var fullPath = ResolveFullPath(queueNzbContents.ExternalPath!);
            if (File.Exists(fullPath))
            {
                File.Delete(fullPath);
            }
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Failed to delete NZB payload for queue item {QueueItemId}", queueNzbContents.Id);
        }

        return Task.CompletedTask;
    }

    private string ResolveFullPath(string relativePath)
    {
        var storageRoot = configManager.GetNzbStoragePath();
        return Path.Combine(storageRoot, relativePath);
    }
}
