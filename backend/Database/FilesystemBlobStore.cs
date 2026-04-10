namespace NzbWebDAV.Database;

/// <summary>
/// Wraps the existing static BlobStore for use via IBlobStore interface.
/// This preserves 100% upstream behavior when S3 is not configured.
/// </summary>
public class FilesystemBlobStore : IBlobStore
{
    public Task WriteBlob(Guid id, Stream stream) => BlobStore.WriteBlob(id, stream);
    public Task WriteBlob<T>(Guid id, T blob) => BlobStore.WriteBlob(id, blob);
    public Stream? ReadBlob(Guid id) => BlobStore.ReadBlob(id);
    public Task<T?> ReadBlob<T>(Guid id) => BlobStore.ReadBlob<T>(id);
    public void Delete(Guid id) => BlobStore.Delete(id);
}
