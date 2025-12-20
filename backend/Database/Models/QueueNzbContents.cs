namespace NzbWebDAV.Database.Models;

public class QueueNzbContents
{
    public Guid Id { get; set; }
    public string NzbContents { get; set; } = string.Empty;
    public string? ExternalPath { get; set; }
    public string? ExternalCompression { get; set; }
    public long? ExternalLengthBytes { get; set; }
    public string? ExternalSha256 { get; set; }

    public bool HasExternalPayload => !string.IsNullOrWhiteSpace(ExternalPath);

    // navigation helpers
    public QueueItem? QueueItem { get; set; }
}