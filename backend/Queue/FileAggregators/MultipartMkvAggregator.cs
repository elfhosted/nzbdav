using NzbWebDAV.Database;
using NzbWebDAV.Database.Models;
using NzbWebDAV.Queue.FileProcessors;
using NzbWebDAV.Services;

namespace NzbWebDAV.Queue.FileAggregators;

public class MultipartMkvAggregator
(
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
        var multipartMkvFiles = processorResults
            .OfType<MultipartMkvProcessor.Result>()
            .ToList();

        ProcessMultipartMkvFiles(multipartMkvFiles);
    }

    private void ProcessMultipartMkvFiles(List<MultipartMkvProcessor.Result> multipartMkvFiles)
    {
        foreach (var multipartMkvFile in multipartMkvFiles)
        {
            var fileParts = multipartMkvFile.Parts;
            var parentDirectory = MountDirectory;
            var name = multipartMkvFile.Filename;

            var davItem = DavItem.New(
                id: Guid.NewGuid(),
                parent: parentDirectory,
                name: name,
                fileSize: fileParts.Sum(x => x.FilePartByteRange.Count),
                type: DavItem.ItemType.MultipartFile,
                releaseDate: multipartMkvFile.ReleaseDate,
                lastHealthCheck: checkedFullHealth ? DateTimeOffset.UtcNow : null
            );

            var metadata = new DavMultipartFile.Meta()
            {
                FileParts = fileParts.ToArray()
            };

            var davMultipartFile = new DavMultipartFile()
            {
                Id = davItem.Id
            };

            if (_metadataStorageService.IsEnabled)
            {
                davMultipartFile.MetadataStorageHash = _metadataStorageService.StorePayload(metadata);
                davMultipartFile.Metadata = null;
            }
            else
            {
                davMultipartFile.Metadata = metadata;
            }

            dbClient.Ctx.Items.Add(davItem);
            dbClient.Ctx.MultipartFiles.Add(davMultipartFile);
        }
    }
}