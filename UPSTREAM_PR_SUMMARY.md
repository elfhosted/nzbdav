# Catalog Metadata Offload

## Feature Overview

* Adds `DavMetadataStorageService`, a filesystem-backed store for large catalog payloads (NZB segment lists, RAR maps, multipart descriptors).
* New nullable pointer columns (`MetadataStorageHash`) keep SQLite rows tiny; payload bytes live once on disk and are deduplicated by SHA-256.
* WebDAV streaming paths, health checks, and queue processors now hydrate metadata through the storage service transparently.
* Database maintenance runs an exporter so existing libraries gradually move to the filesystem without downtime.

## How To Enable

1. Set `database.offload-dav-metadata` to `true` (or export `DATABASE_OFFLOAD_DAV_METADATA=1`).
2. (Optional) override the target directory with `database.dav-metadata-dir` or `DATABASE_DAV_METADATA_DIR`. Defaults to `${CONFIG_PATH}/dav-metadata` and is created automatically.
3. Restart the backend so the queue processor and maintenance worker pick up the new configuration.

## Migration Notes

* With the flag enabled, all newly ingested catalog items immediately store their metadata via `DavMetadataStorageService`.
* `DatabaseMaintenanceService` now calls `DatabaseMaintenance.OffloadDavMetadataAsync(...)`, which batches through legacy rows and writes blobs to disk. Progress is logged; the job piggybacks on the existing maintenance cadence (`DATABASE_MAINTENANCE_INTERVAL_HOURS`).
* Exports are idempotent and skip rows that already have `MetadataStorageHash` set. Because each blob is content-addressed, deleting a DavItem does not disturb other rows that reference the same payload.

## Operational Considerations

* Ensure the metadata directory resides on persistent storage alongside `/config`, or point it somewhere with ample space using `DATABASE_DAV_METADATA_DIR`.
* Turning the flag back off leaves previously exported rows untouched—they continue to resolve correctly from disk—but new catalog writes fall back to inline storage.
* The exporter emits compressed JSON identical to the prior DB representation, so no extra compatibility handling is required when reading old backups or replicating blobs.
