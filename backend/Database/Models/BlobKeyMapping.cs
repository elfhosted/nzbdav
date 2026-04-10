namespace NzbWebDAV.Database.Models;

public class BlobKeyMapping
{
    public Guid BlobId { get; set; }
    public required string Sha256Hash { get; set; }
    public required string BlobType { get; set; }
}
