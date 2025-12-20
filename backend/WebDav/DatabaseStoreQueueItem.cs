using Microsoft.EntityFrameworkCore;
using NzbWebDAV.Database;
using NzbWebDAV.Database.Models;
using NzbWebDAV.Services;
using NzbWebDAV.WebDav.Base;

namespace NzbWebDAV.WebDav;

public class DatabaseStoreQueueItem(
    QueueItem queueItem,
    DavDatabaseClient dbClient,
    NzbStorageService nzbStorageService
) : BaseStoreReadonlyItem
{
    public override string Name => queueItem.FileName;
    public override string UniqueKey => queueItem.Id.ToString();
    public override long FileSize => queueItem.NzbFileSize;
    public override DateTime CreatedAt => queueItem.CreatedAt;

    public override async Task<Stream> GetReadableStreamAsync(CancellationToken ct)
    {
        var id = queueItem.Id;
        var document = await dbClient.Ctx.QueueNzbContents.Where(x => x.Id == id).FirstOrDefaultAsync(ct).ConfigureAwait(false);
        if (document is null) throw new FileNotFoundException($"Could not find nzb document with id: {id}");
        return await nzbStorageService.OpenReadableStreamAsync(document, ct).ConfigureAwait(false);
    }
}