using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Gelato.Tmdb;

/// <summary>
/// On-disk cache of TMDB detail responses.
///
/// Movie details are the bulk of a backfill and their <c>imdb_id</c> never changes,
/// so they are cached indefinitely rather than with the five-minute in-memory TTL
/// used for Stremio metadata.
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

    public bool TryGet<T>(string key, out T? value)
    {
        value = default;
        var path = PathFor(key);

        if (!File.Exists(path))
            return false;

        try
        {
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
        Directory.CreateDirectory(_dir);

        try
        {
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
