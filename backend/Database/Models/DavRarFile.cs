using NzbWebDAV.Models;

namespace NzbWebDAV.Database.Models;

public class DavRarFile
{
    public Guid Id { get; set; } // foreign key to DavItem.Id
    public RarPart[]? RarParts { get; set; }
    public string? MetadataStorageHash { get; set; }

    // navigation helpers
    public DavItem? DavItem { get; set; }

    public class RarPart
    {
        public string[] SegmentIds { get; set; } = [];
        public long PartSize { get; set; }
        public long Offset { get; set; }
        public long ByteCount { get; set; }
    }

    public DavMultipartFile.Meta ToDavMultipartFileMeta(IEnumerable<RarPart>? source = null)
    {
        var rarParts = source?.ToArray() ?? RarParts ?? Array.Empty<RarPart>();
        return new DavMultipartFile.Meta
        {
            FileParts = rarParts.Select(x => new DavMultipartFile.FilePart()
            {
                SegmentIds = x.SegmentIds,
                SegmentIdByteRange = LongRange.FromStartAndSize(0, x.PartSize),
                FilePartByteRange = LongRange.FromStartAndSize(x.Offset, x.ByteCount),
            }).ToArray()
        };
    }
}