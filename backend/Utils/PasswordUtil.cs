using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Caching.Memory;

namespace NzbWebDAV.Utils;

public static class PasswordUtil
{
    // SizeLimit was 5 — far too small for an internet-exposed WebDAV endpoint.
    // Media clients (Plex/Jellyfin/Stremio) don't use cookies, so every request
    // sends Basic Auth and triggers PasswordHasher.VerifyHashedPassword. The
    // hasher uses PBKDF2 (~50-200ms per call) and is intentionally slow. With
    // SizeLimit=5, just a handful of distinct passwords (legit clients + bots
    // scanning the endpoint with random passwords) is enough to evict the legit
    // credential's entry via LRU — at which point every WebDAV request from
    // the legit client recomputes PBKDF2. During an NNTP outage, fail-fast
    // makes WebDAV clients retry at high rate, multiplying this cost into
    // CPU-bound PBKDF2 storms that starve the threadpool (manifesting as
    // outbound HttpClient timeouts and unresponsive /health endpoints).
    //
    // 1024 entries × ~200 bytes ≈ 200KB of memory — negligible — and gives
    // plenty of headroom to keep legit credentials cached even when bots are
    // pounding the endpoint with thousands of unique bad passwords.
    private static readonly MemoryCache Cache = new(new MemoryCacheOptions() { SizeLimit = 1024 });
    private static readonly PasswordHasher<object> Hasher = new();

    public static string Hash(string password, string salt = "")
    {
        return Hasher.HashPassword(null!, password + salt);
    }

    public static bool Verify(string hash, string password, string salt = "")
    {
        // If users forget to add the "--use-cookies" argument to Rclone, then Rclone will not store
        // session cookies, which means the Authorization header from HTTP Basic Auth will be sent and
        // validated on every single request. This means the password from the Authorization header will
        // get hashed on every single request in order to compare it against the hashed password in the
        // database. Password hashing is intentionally designed to be super slow in order to slow down brute
        // force attacks. Several hundred milliseconds would be added to every single webdav request
        // when the "--use-cookies" Rclone argument is not used, if not for the memory cache added here.
        return Cache.GetOrCreate(new CacheKey(hash, password, salt), cacheEntry =>
        {
            cacheEntry.Size = 1;
            return Hasher.VerifyHashedPassword(null!, hash, password + salt);
        }) == PasswordVerificationResult.Success;
    }

    private record CacheKey(string Hash, string Password, string Salt);
}