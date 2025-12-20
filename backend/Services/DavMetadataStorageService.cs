using System.Security.Cryptography;
using System.IO;
using NzbWebDAV.Config;
using NzbWebDAV.Utils;
using Serilog;

namespace NzbWebDAV.Services;

public class DavMetadataStorageService
{
    private readonly ConfigManager _configManager;
    private readonly object _rootLock = new();
    private string? _storageRoot;

    public DavMetadataStorageService(ConfigManager configManager)
    {
        _configManager = configManager;
    }

    public bool IsEnabled => _configManager.IsDavMetadataOffloadEnabled();

    public string StorePayload<T>(T payload)
    {
        if (payload == null) throw new ArgumentNullException(nameof(payload));
        var bytes = CompressionUtil.SerializeToCompressedJson(payload);
        var hashBytes = SHA256.HashData(bytes);
        var hash = Convert.ToHexString(hashBytes).ToLowerInvariant();
        var path = GetPayloadPath(hash);
        if (File.Exists(path)) return hash;

        var directory = Path.GetDirectoryName(path)!;
        Directory.CreateDirectory(directory);
        var tempPath = Path.Combine(directory, $"{Path.GetFileName(path)}.{Guid.NewGuid():N}.tmp");
        try
        {
            File.WriteAllBytes(tempPath, bytes);
            try
            {
                File.Move(tempPath, path, overwrite: false);
            }
            catch (IOException) when (File.Exists(path))
            {
                // Another writer stored this hash in parallel; fall through and delete temp file.
            }
        }
        finally
        {
            if (File.Exists(tempPath))
            {
                try
                {
                    File.Delete(tempPath);
                }
                catch (IOException)
                {
                    // Ignored; best-effort cleanup.
                }
            }
        }

        return hash;
    }

    public T ResolvePayload<T>(string? storageHash, T? inlineValue, Func<T> fallbackFactory) where T : class
    {
        if (!string.IsNullOrWhiteSpace(storageHash))
            return LoadPayload(storageHash!, fallbackFactory);
        if (inlineValue is not null)
            return inlineValue;
        return fallbackFactory();
    }

    public bool TryDelete(string? storageHash)
    {
        if (string.IsNullOrWhiteSpace(storageHash)) return false;
        var path = GetPayloadPath(storageHash!);
        if (!File.Exists(path)) return false;
        try
        {
            File.Delete(path);
            return true;
        }
        catch (IOException ex)
        {
            Log.Debug(ex, "Failed to delete metadata blob {Hash}: {Message}", storageHash, ex.Message);
            return false;
        }
    }

    private T LoadPayload<T>(string storageHash, Func<T> fallbackFactory) where T : class
    {
        try
        {
            var path = GetPayloadPath(storageHash);
            if (!File.Exists(path)) return fallbackFactory();
            var bytes = File.ReadAllBytes(path);
            return CompressionUtil.DeserializeCompressedJson(bytes, fallbackFactory);
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Failed to load metadata blob {Hash}: {Message}", storageHash, ex.Message);
            return fallbackFactory();
        }
    }

    private string GetPayloadPath(string storageHash)
    {
        if (string.IsNullOrWhiteSpace(storageHash))
            throw new ArgumentException("Storage hash must be provided.", nameof(storageHash));
        var normalized = storageHash.ToLowerInvariant();
        if (normalized.Length < 6)
            throw new ArgumentException("Storage hash must contain at least 6 hex characters.", nameof(storageHash));
        var root = GetStorageRoot();
        var first = normalized[..2];
        var second = normalized.Substring(2, 2);
        return Path.Combine(root, first, second, normalized);
    }

    private string GetStorageRoot()
    {
        if (_storageRoot != null) return _storageRoot;
        lock (_rootLock)
        {
            if (_storageRoot != null) return _storageRoot;
            var configured = _configManager.GetDavMetadataDirectory();
            Directory.CreateDirectory(configured);
            _storageRoot = configured;
            return _storageRoot;
        }
    }
}
