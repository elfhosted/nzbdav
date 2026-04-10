namespace NzbWebDAV.Database;

public interface IBlobStore
{
    Task WriteBlob(Guid id, Stream stream);
    Task WriteBlob<T>(Guid id, T blob);
    Stream? ReadBlob(Guid id);
    Task<T?> ReadBlob<T>(Guid id);
    void Delete(Guid id);
}
