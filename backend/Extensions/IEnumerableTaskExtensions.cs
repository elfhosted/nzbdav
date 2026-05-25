// ReSharper disable InconsistentNaming

namespace NzbWebDAV.Extensions;

public static class IEnumerableTaskExtensions
{
    /// <summary>
    /// Executes tasks with specified concurrency and enumerates results as they come in
    /// </summary>
    /// <param name="tasks">The tasks to execute</param>
    /// <param name="concurrency">The max concurrency</param>
    /// <param name="cancellationToken">The cancellation token</param>
    /// <typeparam name="T">The resulting type of each task</typeparam>
    /// <returns>An IAsyncEnumerable that enumerates task results as they come in</returns>
    public static IEnumerable<Task<T>> WithConcurrency<T>
    (
        this IEnumerable<Task<T>> tasks,
        int concurrency
    ) where T : IDisposable
    {
        if (concurrency < 1)
            throw new ArgumentException("concurrency must be greater than zero.");

        if (concurrency == 1)
        {
            foreach (var task in tasks) yield return task;
            yield break;
        }

        var isFirst = true;
        var runningTasks = new Queue<Task<T>>();
        try
        {
            foreach (var task in tasks)
            {
                if (isFirst)
                {
                    // help with time-to-first-byte
                    yield return task;
                    isFirst = false;
                    continue;
                }

                runningTasks.Enqueue(task);
                if (runningTasks.Count < concurrency) continue;
                yield return runningTasks.Dequeue();
            }

            while (runningTasks.Count > 0)
                yield return runningTasks.Dequeue();
        }
        finally
        {
            while (runningTasks.Count > 0)
            {
                runningTasks.Dequeue().ContinueWith(x =>
                {
                    if (x.Status == TaskStatus.RanToCompletion)
                        x.Result.Dispose();
                });
            }
        }
    }

    public static async IAsyncEnumerable<T> WithConcurrencyAsync<T>
    (
        this IEnumerable<Task<T>> tasks,
        int concurrency
    )
    {
        if (concurrency < 1)
            throw new ArgumentException("concurrency must be greater than zero.");

        var runningTasks = new HashSet<Task<T>>();
        try
        {
            foreach (var task in tasks)
            {
                runningTasks.Add(task);
                if (runningTasks.Count < concurrency) continue;
                var completedTask = await Task.WhenAny(runningTasks).ConfigureAwait(false);
                runningTasks.Remove(completedTask);
                yield return await completedTask.ConfigureAwait(false);
            }

            while (runningTasks.Count > 0)
            {
                var completedTask = await Task.WhenAny(runningTasks).ConfigureAwait(false);
                runningTasks.Remove(completedTask);
                yield return await completedTask.ConfigureAwait(false);
            }
        }
        finally
        {
            // When the iterator exits abnormally (yielded a faulted task and
            // the caller propagated, or the caller broke out early), the
            // remaining tasks in `runningTasks` would otherwise become orphans
            // — they were started but no one is observing them, and they keep
            // pinning threadpool threads until they complete naturally. For
            // NNTP fetches with 15s connect timeouts × 2 retries, that's 30s
            // per orphan; with QueueManager starting items at ~10/s and each
            // item fanning out 8 concurrent fetches, the orphans accumulate
            // faster than they complete and the threadpool starves (observed
            // in production as t=83->999, pend=121->975 over 14 min while
            // cascade=released and only 2 actual provider failures recorded).
            //
            // We can't cancel them (no CT was passed in), but awaiting them
            // here ensures the iteration can't leave tasks running that the
            // caller has stopped paying attention to. Per-item failure path
            // gets ~30s slower in exchange for a stable threadpool.
            if (runningTasks.Count > 0)
            {
                try { await Task.WhenAll(runningTasks).ConfigureAwait(false); }
                catch { /* orphan task failures are not the caller's problem */ }
            }
        }
    }
}