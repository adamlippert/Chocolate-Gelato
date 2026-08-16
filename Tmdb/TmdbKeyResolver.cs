namespace Gelato.Tmdb;

/// <summary>
/// Resolves the TMDB API key for collection sync.
///
/// There is deliberately no hardcoded fallback here. The key at
/// <c>GelatoStremioProvider.cs:205</c> is shared by every Gelato installation and is
/// sized for occasional release-date lookups, not for backfills in the tens of
/// thousands of requests. Without a real key the feature stays disabled.
/// </summary>
public static class TmdbKeyResolver
{
    public static string? Resolve(string? configuredKey, Func<string?> jellyfinTmdbKey)
    {
        ArgumentNullException.ThrowIfNull(jellyfinTmdbKey);

        if (!string.IsNullOrWhiteSpace(configuredKey))
            return configuredKey.Trim();

        string? fallback;
        try
        {
            fallback = jellyfinTmdbKey();
        }
        catch
        {
            // The fallback reads another plugin's configuration by reflection.
            // Treat any failure as "no key available".
            return null;
        }

        return string.IsNullOrWhiteSpace(fallback) ? null : fallback.Trim();
    }

    /// <summary>
    /// Reads the Jellyfin TMDB plugin's key without a compile-time dependency on
    /// MediaBrowser.Providers, mirroring the approach in
    /// <c>GelatoStremioProvider.GetTmdbApiKey</c>.
    /// </summary>
    public static string? FromJellyfinTmdbPlugin()
    {
        var pluginType = Type.GetType(
            "MediaBrowser.Providers.Plugins.Tmdb.Plugin, Jellyfin.Providers",
            throwOnError: false
        );
        var instance = pluginType?.GetProperty("Instance")?.GetValue(null);
        var cfg = instance?.GetType().GetProperty("Configuration")?.GetValue(instance);
        return cfg?.GetType().GetProperty("TmdbApiKey")?.GetValue(cfg) as string;
    }
}
