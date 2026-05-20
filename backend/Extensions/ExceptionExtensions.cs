using NzbWebDAV.Exceptions;

namespace NzbWebDAV.Extensions;

public static class ExceptionExtensions
{
    public static bool IsRetryableDownloadException(this Exception exception)
    {
        // CouldNot{Connect,Login}ToUsenetException is transient by definition —
        // the provider's circuit breaker is just open, or the network blip is
        // brief. Treating it as non-retryable causes QueueItemProcessor to
        // mark the queue item completed-with-error and drop it on the floor
        // during NNTP outages, which loses user data.
        if (exception is CouldNotConnectToUsenetException
            or CouldNotLoginToUsenetException
            or RetryableDownloadException)
            return true;

        // Same when those exceptions are nested (e.g. inside an AggregateException
        // surfaced by WithConcurrencyAsync or CheckAllSegmentsAsync's child-CT path).
        return exception.TryGetCausingException(out CouldNotConnectToUsenetException? _)
               || exception.TryGetCausingException(out CouldNotLoginToUsenetException? _);
    }

    public static bool IsNonRetryableDownloadException(this Exception exception)
    {
        return exception is NonRetryableDownloadException
            or SharpCompress.Common.InvalidFormatException;
    }

    public static bool IsCancellationException(this Exception exception)
    {
        return exception is TaskCanceledException or OperationCanceledException;
    }

    public static bool TryGetCausingException<T>(this Exception exception, out T? exceptionType) where T : Exception
    {
        ArgumentNullException.ThrowIfNull(exception);
        var current = exception;

        while (current != null)
        {
            if (current is T matching)
            {
                exceptionType = matching;
                return true;
            }

            current = current.InnerException;
        }

        exceptionType = null;
        return false;
    }
}