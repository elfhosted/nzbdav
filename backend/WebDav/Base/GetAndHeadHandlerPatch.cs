using Microsoft.AspNetCore.Http;
using NWebDav.Server;
using NWebDav.Server.Handlers;
using NWebDav.Server.Helpers;
using NWebDav.Server.Props;
using NWebDav.Server.Stores;
using NzbWebDAV.Clients.Usenet;

namespace NzbWebDAV.WebDav.Base;

/// <summary>
/// Implementation of the GET and HEAD method.
/// </summary>
/// <remarks>
/// The specification of the WebDAV GET and HEAD methods for collections
/// can be found in the
/// <see href="http://www.webdav.org/specs/rfc2518.html#rfc.section.8.4">
/// WebDAV specification
/// </see>.
/// </remarks>
public class GetAndHeadHandlerPatch : IRequestHandler
{
    private readonly IStore _store;
    private readonly UsenetStreamingClient _usenetClient;

    public GetAndHeadHandlerPatch(IStore store, UsenetStreamingClient usenetClient)
    {
        _store = store;
        _usenetClient = usenetClient;
    }

    /// <summary>
    /// Handle a GET or HEAD request.
    /// </summary>
    /// <param name="httpContext">
    /// The HTTP context of the request.
    /// </param>
    /// <returns>
    /// A task that represents the asynchronous GET or HEAD operation. The
    /// task will always return <see langword="true"/> upon completion.
    /// </returns>
    public async Task<bool> HandleRequestAsync(HttpContext httpContext)
    {
        // Obtain request and response
        var request = httpContext.Request;
        var response = httpContext.Response;

        // Determine if we are invoked as HEAD
        var isHeadRequest = request.Method == HttpMethods.Head;

        // Determine the requested range
        var range = request.GetRange();

        // Obtain the WebDAV collection
        var entry = await _store.GetItemAsync(request.GetUri(), httpContext.RequestAborted).ConfigureAwait(false);
        if (entry == null)
        {
            // Set status to not found
            response.SetStatus(DavStatusCode.NotFound);
            return true;
        }

        // ETag might be used for a conditional request
        string? etag = null;

        // Add non-expensive headers based on properties. Done BEFORE the
        // outage short-circuit below so that an If-None-Match-matching
        // client can be answered with 304 Not Modified even while NNTP is
        // unreachable — the 304 path doesn't need the body, so denying it
        // with 503 would needlessly degrade cached-client availability.
        var propertyManager = entry.PropertyManager;
        if (propertyManager != null)
        {
            // Add Last-Modified header
            var lastModifiedUtc = (string?)await propertyManager.GetPropertyAsync(entry, DavGetLastModified<IStoreItem>.PropertyName, true, httpContext.RequestAborted).ConfigureAwait(false);
            if (lastModifiedUtc != null)
                response.Headers.LastModified = lastModifiedUtc;

            // Add ETag
            etag = (string?)await propertyManager.GetPropertyAsync(entry, DavGetEtag<IStoreItem>.PropertyName, true, httpContext.RequestAborted).ConfigureAwait(false);
            if (etag != null)
                response.Headers.ETag = etag;

            // Add type
            var contentType = (string?)await propertyManager.GetPropertyAsync(entry, DavGetContentType<IStoreItem>.PropertyName, true, httpContext.RequestAborted).ConfigureAwait(false);
            if (contentType != null)
                response.ContentType = contentType;

            // Add language
            var contentLanguage = (string?)await propertyManager.GetPropertyAsync(entry, DavGetContentLanguage<IStoreItem>.PropertyName, true, httpContext.RequestAborted).ConfigureAwait(false);
            if (contentLanguage != null)
                response.Headers.ContentLanguage = contentLanguage;
        }

        // Conditional GET: if the client's cached ETag still matches, return
        // 304 Not Modified without opening the body stream. Done before the
        // outage short-circuit so a cached client gets a useful answer even
        // when NNTP is down.
        if (etag != null && request.Headers.IfNoneMatch == etag)
        {
            response.ContentLength = 0;
            response.SetStatus(DavStatusCode.NotModified);
            return true;
        }

        // Short-circuit GETs only for items whose body would come from NNTP
        // (DatabaseStoreNzbFile / DatabaseStoreRarFile / DatabaseStoreMultipartFile,
        // marked with IUsenetBackedStoreItem). Streaming clients hammer 500s
        // into retry storms, so returning 503 + Retry-After here saves the
        // per-request stream-construction + exception-unwind cost. Items
        // that can still be served from the DB or filesystem during an
        // outage — DatabaseStoreQueueItem, DatabaseStoreSymlinkFile,
        // StaticEmbeddedFile, BaseStoreCollection (directory listings) —
        // are explicitly NOT short-circuited; users need them to keep
        // working. HEAD is also left alone since it returns metadata only.
        if (!isHeadRequest
            && entry is IUsenetBackedStoreItem
            && _usenetClient.AreAllProvidersTripped)
        {
            response.Headers["Retry-After"] = "60";
            response.SetStatus((DavStatusCode)503);
            return true;
        }

        // Stream the actual entry
        var stream = await entry.GetReadableStreamAsync(httpContext.RequestAborted).ConfigureAwait(false);
        await using (stream.ConfigureAwait(false))
        {
            if (stream != Stream.Null)
            {
                // Set the response
                response.SetStatus(DavStatusCode.Ok);

                // Set the expected content length
                try
                {
                    // We can only specify the Content-Length header if the
                    // length is known (this is typically true for seekable streams)
                    if (stream.CanSeek)
                    {
                        // Add a header that we accept ranges (bytes only)
                        response.Headers.AcceptRanges = "bytes";

                        // Determine the total length
                        var length = stream.Length;

                        // Check if a range was specified
                        if (range != null)
                        {
                            var start = range.Start ?? 0;
                            var end = Math.Min(range.End ?? long.MaxValue, length - 1);

                            // Return 416 if the range start is beyond the end of the file
                            if (start > end)
                            {
                                response.Headers.ContentRange = $"bytes */{stream.Length}";
                                response.SetStatus((DavStatusCode)416);
                                return true;
                            }

                            length = end - start + 1;

                            // Write the range
                            response.Headers.ContentRange = $"bytes {start}-{end}/{stream.Length}";

                            // Set status to partial result if not all data can be sent
                            if (length < stream.Length)
                                response.SetStatus(DavStatusCode.PartialContent);
                        }

                        // Set the header, so the client knows how much data is required
                        response.ContentLength = length;
                    }
                }
                catch (NotSupportedException)
                {
                    // If the content length is not supported, then we just skip it
                }

                // HEAD method doesn't require the actual item data
                if (!isHeadRequest)
                    await CopyToAsync(stream, response.Body, range?.Start ?? 0, range?.End, httpContext.RequestAborted).ConfigureAwait(false);
            }
            else
            {
                // Set the response
                response.SetStatus(DavStatusCode.NoContent);
            }
        }
        return true;
    }

    private async Task CopyToAsync(Stream src, Stream dest, long start, long? end, CancellationToken cancellationToken)
    {
        // Skip to the first offset
        if (start > 0)
        {
            // We prefer seeking instead of draining data
            if (!src.CanSeek)
                throw new IOException("Cannot use range, because the source stream isn't seekable");
            
            src.Seek(start, SeekOrigin.Begin);
        }

        // Determine the number of bytes to read
        var bytesToRead = end - start + 1 ?? long.MaxValue;

        // Read in 64KB blocks
        var buffer = new byte[64 * 1024];

        // Copy, until we don't get any data anymore
        while (bytesToRead > 0)
        {
            // Read the requested bytes into memory
            var requestedBytes = (int)Math.Min(bytesToRead, buffer.Length);
            var bytesRead = await src.ReadAsync(buffer, 0, requestedBytes, cancellationToken).ConfigureAwait(false);

            // We're done, if we cannot read any data anymore
            if (bytesRead == 0)
                return;
            
            // Write the data to the destination stream
            await dest.WriteAsync(buffer, 0, bytesRead, cancellationToken).ConfigureAwait(false);

            // Decrement the number of bytes left to read
            bytesToRead -= bytesRead;
        }
    }
}