namespace NzbWebDAV.WebDav.Base;

/// <summary>
/// Marker interface for WebDAV store items whose data stream is served from
/// NNTP segments. Used by GetAndHeadHandlerPatch to scope the cascade
/// short-circuit (HTTP 503 + Retry-After when every NNTP provider's
/// circuit breaker is open) to only those GETs that would actually fail —
/// without blocking GETs for items that can still be served from the local
/// database or filesystem during an outage (DatabaseStoreQueueItem,
/// DatabaseStoreSymlinkFile, StaticEmbeddedFile, etc).
/// </summary>
public interface IUsenetBackedStoreItem
{
}
