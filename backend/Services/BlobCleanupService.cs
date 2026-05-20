using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Hosting;
using NzbWebDAV.Database;
using NzbWebDAV.Utils;
using Serilog;

namespace NzbWebDAV.Services;

/// <summary>
/// Background service that processes the blob cleanup queue.
/// Continuously monitors BlobCleanupQueueItems table and deletes corresponding blobs.
/// </summary>
public class BlobCleanupService : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await using var dbContext = new DavDatabaseContext();

                // Get the first item from the queue
                var cleanupItem = await dbContext.BlobCleanupItems
                    .FirstOrDefaultAsync(stoppingToken)
                    .ConfigureAwait(false);

                // If no items in queue, wait 10 seconds before checking again
                if (cleanupItem == null)
                {
                    await Task.Delay(TimeSpan.FromSeconds(10), stoppingToken).ConfigureAwait(false);
                    continue;
                }

                // Delete the blob
                BlobStoreProvider.Instance.Delete(cleanupItem.Id);

                // Remove the queue item from database
                dbContext.BlobCleanupItems.Remove(cleanupItem);
                await dbContext.SaveChangesAsync(stoppingToken).ConfigureAwait(false);

                // Throttle: cap sustained throughput at ~50 items/sec so a
                // large backlog doesn't peg a CPU core grinding through
                // deletes + SaveChangesAsync round-trips. 20ms is invisible
                // for normal-sized cleanup queues and keeps the threadpool
                // responsive for inbound requests during outages.
                await Task.Delay(20, stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (SigtermUtil.IsSigtermTriggered())
            {
                // OperationCanceledException is expected on sigterm
                return;
            }
            catch (Exception e)
            {
                Log.Error(e, $"Error processing blob cleanup queue: {e.Message}");

                // Wait 10 seconds before continuing on exception
                await Task.Delay(TimeSpan.FromSeconds(10), stoppingToken).ConfigureAwait(false);
            }
        }
    }
}
