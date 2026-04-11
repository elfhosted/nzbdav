using NzbWebDAV.Utils;
using Serilog;

namespace NzbWebDAV.Database;

/// <summary>
/// Static singleton that provides the active IBlobStore implementation.
/// Defaults to FilesystemBlobStore (upstream behavior).
/// When S3_BLOB_* env vars are set, switches to S3BlobStore.
/// </summary>
public static class BlobStoreProvider
{
    public static IBlobStore Instance { get; private set; } = new FilesystemBlobStore();
    public static bool IsS3Configured { get; private set; }

    public static async Task InitializeAsync()
    {
        var endpoint = EnvironmentUtil.GetEnvironmentVariable("S3_BLOB_ENDPOINT");
        var bucket = EnvironmentUtil.GetEnvironmentVariable("S3_BLOB_BUCKET");
        var accessKey = EnvironmentUtil.GetEnvironmentVariable("S3_BLOB_ACCESS_KEY");
        var secretKey = EnvironmentUtil.GetEnvironmentVariable("S3_BLOB_SECRET_KEY");
        var region = EnvironmentUtil.GetEnvironmentVariable("S3_BLOB_REGION") ?? "auto";

        if (string.IsNullOrWhiteSpace(endpoint)
            || string.IsNullOrWhiteSpace(bucket)
            || string.IsNullOrWhiteSpace(accessKey)
            || string.IsNullOrWhiteSpace(secretKey))
        {
            return;
        }

        await BlobKeyMappingDbContext.EnsureTableCreatedAsync();
        var s3Store = new S3BlobStore(endpoint, bucket, accessKey, secretKey, region);
        Instance = s3Store;
        IsS3Configured = true;
        Log.Information("S3 blob store enabled: {Endpoint}/{Bucket}", endpoint, bucket);

        // Repair orphaned blobs in the background — don't block startup
        _ = Task.Run(async () =>
        {
            try
            {
                // Small delay to let the app finish starting
                await Task.Delay(TimeSpan.FromSeconds(10));
                await s3Store.RepairOrphanedBlobsAsync();
            }
            catch (Exception ex)
            {
                Log.Error(ex, "Background S3 repair failed");
            }
        });
    }
}
