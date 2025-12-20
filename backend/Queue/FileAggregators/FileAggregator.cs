using NzbWebDAV.Database;
using NzbWebDAV.Database.Models;
using NzbWebDAV.Extensions;
using NzbWebDAV.Queue.FileProcessors;
using NzbWebDAV.Services;

namespace NzbWebDAV.Queue.FileAggregators;

public class FileAggregator(
    DavDatabaseClient dbClient,
    DavMetadataStorageService metadataStorageService,
    DavItem mountDirectory,
    bool checkedFullHealth
) : BaseAggregator
{
    protected override DavDatabaseClient DBClient => dbClient;
    protected override DavItem MountDirectory => mountDirectory;
    private readonly DavMetadataStorageService _metadataStorageService = metadataStorageService;

    public override void UpdateDatabase(List<BaseProcessor.Result> processorResults)
    {
        foreach (var processorResult in processorResults)
        {
            if (processorResult is not FileProcessor.Result result) continue;
            if (result.FileName == "") continue; // skip files whose name we can't determine

            var davItem = DavItem.New(
                id: Guid.NewGuid(),
                parent: mountDirectory,
                name: result.FileName,
                fileSize: result.FileSize,
                type: DavItem.ItemType.NzbFile,
                releaseDate: result.ReleaseDate,
                lastHealthCheck: checkedFullHealth ? DateTimeOffset.UtcNow : null
            );

            var segmentIds = result.NzbFile.GetSegmentIds();
            var davNzbFile = new DavNzbFile()
            {
                Id = davItem.Id
            };

            if (_metadataStorageService.IsEnabled)
            {
                davNzbFile.MetadataStorageHash = _metadataStorageService.StorePayload(segmentIds);
                davNzbFile.SegmentIds = null;
            }
            else
            {
                davNzbFile.SegmentIds = segmentIds;
            }

            dbClient.Ctx.Items.Add(davItem);
            dbClient.Ctx.NzbFiles.Add(davNzbFile);
        }
    }
}