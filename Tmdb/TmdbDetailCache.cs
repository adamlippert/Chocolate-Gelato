using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Gelato.Tmdb;

/// <summary>
/// On-disk cache of TMDB detail responses.
///
/// Freshness is the caller's decision, not this class's: <see cref="TryGet{T}(string, out T?,
/// TimeSpan?)"/> takes an optional maximum age and treats anything older as a miss.
///
/// Movie details are the bulk of a backfill and their <c>imdb_id</c> never changes, so callers
/// read them with no maximum age — cached indefinitely rather than with the five-minute
/// in-memory TTL used for Stremio metadata. Collections are the opposite: their <c>parts</c>
/// array grows whenever a studio ships a sequel, so they must be read with a bounded age or a
/// franchise collection would never gain a new film.
/// </summary>
public sealed class TmdbDetailCache(string cacheDirectory)
{
    private readonly string _dir = cacheDirectory;

    private string PathFor(string key)
    {
        // Hash the key so arbitrary ids cannot escape the cache directory.
        var hash = Convert.ToHexString(MD5.HashData(Encoding.UTF8.GetBytes(key)));
        return Path.Combine(_dir, hash + ".json");
    }

    /// <param name="maxAge">
    /// Treat an entry last written longer ago than this as a miss. Null caches indefinitely.
    /// </param>
    public bool TryGet<T>(string key, out T? value, TimeSpan? maxAge = null)
    {
        value = default;
        var path = PathFor(key);

        if (!File.Exists(path))
            return false;

        try
        {
            if (maxAge is { } age && DateTime.UtcNow - File.GetLastWriteTimeUtc(path) > age)
                return false;

            value = JsonSerializer.Deserialize<T>(File.ReadAllText(path), TmdbJson.Options);
            return value is not null;
        }
        catch
        {
            // A truncated or corrupt entry is a miss, not a failure.
            return false;
        }
    }

    public void Set<T>(string key, T value)
    {
        try
        {
            // Inside the try: the cache directory now lives under Jellyfin's data paths, which
            // may be read-only or owned by another user.
            Directory.CreateDirectory(_dir);

            var path = PathFor(key);
            // Same directory, so same volume — File.Move is atomic here. Writing in place
            // is not, and concurrent callers for one key would otherwise tear the file.
            var tmp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(value, TmdbJson.Options));
            File.Move(tmp, path, overwrite: true);
        }
        catch
        {
            // A cache that cannot be written is a performance problem, not a correctness
            // one. Never fail a sync because of it.
        }
    }
}
