using System.Text.Json.Serialization;
using System.Xml.Serialization;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Model.Plugins;
using Microsoft.Extensions.Logging;

namespace Gelato.Config;

public class PluginConfiguration : BasePluginConfiguration
{
    public string MoviePath { get; set; } = Path.Combine(Path.GetTempPath(), "gelato", "movies");
    public string SeriesPath { get; set; } = Path.Combine(Path.GetTempPath(), "gelato", "series");
    public int StreamTTL { get; set; } = 3600;
    public int CatalogMaxItems { get; set; } = 100;
    public string Url { get; set; } = "";
    public bool EnableMixed { get; set; } = false;
    public bool ExtendLocalSeriesTrees { get; set; } = false;
    public bool FilterUnreleased { get; set; } = false;
    public int FilterUnreleasedBufferDays { get; set; } = 0;
    public bool DisableSourceCount { get; set; } = true;
    public bool P2PEnabled { get; set; } = false;
    public int P2PDLSpeed { get; set; } = 0;
    public int P2PULSpeed { get; set; } = 0;
    public string FFmpegAnalyzeDuration { get; set; } = "5M";
    public string FFmpegProbeSize { get; set; } = "40M";
    public bool CreateCollections { get; set; } = false;
    public int MaxCollectionItems { get; set; } = 100;
    public bool DisableSearch { get; set; } = false;
    public bool EnableJavaScriptInjection { get; set; } = false;
    public bool LazyImages { get; set; } = false;

    /// <summary>
    /// When enabled, the real stream URL is handed to the client in the PlaybackInfo response so
    /// the client fetches the media directly instead of having the server proxy every byte.
    /// Saves server bandwidth, but exposes the stream URL (including any debrid token it carries)
    /// to anyone who can inspect the client's network traffic.
    /// </summary>
    public bool EnableDirectPlay { get; set; } = false;

    /// <summary>Tracked collections. See docs/superpowers/specs/2026-08-16-catalog-collections-design.md.</summary>
    public List<CollectionRow> CollectionRows { get; set; } = [];

    /// <summary>
    /// TMDB API key for collection sync. Falls back to the Jellyfin TMDB plugin's key.
    /// There is deliberately no hardcoded fallback — without a key the feature stays off.
    /// </summary>
    public string TmdbApiKey { get; set; } = "";

    /// <summary>
    /// Maximum items this feature may create in total. 0 means unlimited. On reaching it,
    /// sync stops creating new rows, logs the shortfall, and keeps reconciling existing
    /// membership. Nothing is deleted.
    /// </summary>
    public int GlobalItemCeiling { get; set; } = 25000;
    public List<CatalogConfig> Catalogs { get; set; } = [];
    public List<UserConfig> UserConfigs { get; set; } = [];

    public string GetBaseUrl()
    {
        if (string.IsNullOrWhiteSpace(Url))
            throw new InvalidOperationException("Gelato Url not configured.");

        var u = Url.Trim().TrimEnd('/');

        if (u.EndsWith("/manifest.json", StringComparison.OrdinalIgnoreCase))
            u = u[..^"/manifest.json".Length];

        return u;
    }

    [JsonIgnore]
    [XmlIgnore]
    public GelatoStremioProvider? Stremio;

    [JsonIgnore]
    [XmlIgnore]
    public Folder? MovieFolder;

    [JsonIgnore]
    [XmlIgnore]
    public Folder? SeriesFolder;

    public PluginConfiguration GetEffectiveConfig(Guid userId)
    {
        var userConfig = UserConfigs.FirstOrDefault(u => u.UserId == userId);
        return userConfig is null ? this : userConfig.ApplyOverrides(this);
    }
}

public class UserConfig
{
    public Guid UserId { get; set; }
    public string Url { get; set; } = "";
    public string MoviePath { get; set; } = "";
    public string SeriesPath { get; set; } = "";
    public bool DisableSearch { get; set; } = false;

    /// <summary>
    /// Apply user overrides to base configuration - replaces all overridable fields
    /// </summary>
    public PluginConfiguration ApplyOverrides(PluginConfiguration baseConfig)
    {
        return new PluginConfiguration
        {
            // User overridable fields - all required, no fallback to baseConfig
            Url = Url,
            MoviePath = MoviePath,
            SeriesPath = SeriesPath,
            DisableSearch = DisableSearch,

            // All other fields from base config
            StreamTTL = baseConfig.StreamTTL,
            CatalogMaxItems = baseConfig.CatalogMaxItems,
            EnableMixed = baseConfig.EnableMixed,
            ExtendLocalSeriesTrees = baseConfig.ExtendLocalSeriesTrees,
            FilterUnreleased = baseConfig.FilterUnreleased,
            FilterUnreleasedBufferDays = baseConfig.FilterUnreleasedBufferDays,
            DisableSourceCount = baseConfig.DisableSourceCount,
            P2PEnabled = baseConfig.P2PEnabled,
            P2PDLSpeed = baseConfig.P2PDLSpeed,
            P2PULSpeed = baseConfig.P2PULSpeed,
            FFmpegAnalyzeDuration = baseConfig.FFmpegAnalyzeDuration,
            FFmpegProbeSize = baseConfig.FFmpegProbeSize,
            CreateCollections = baseConfig.CreateCollections,
            MaxCollectionItems = baseConfig.MaxCollectionItems,
            EnableDirectPlay = baseConfig.EnableDirectPlay,
            CollectionRows = baseConfig.CollectionRows,
            TmdbApiKey = baseConfig.TmdbApiKey,
            GlobalItemCeiling = baseConfig.GlobalItemCeiling,
            UserConfigs = baseConfig.UserConfigs,
        };
    }
}

public class GelatoStremioProviderFactory(IHttpClientFactory http, ILoggerFactory log)
{
    private readonly System.Collections.Concurrent.ConcurrentDictionary<
        string,
        GelatoStremioProvider
    > _cache = new(StringComparer.OrdinalIgnoreCase);

    public GelatoStremioProvider Create(Guid userId)
    {
        var cfg = GelatoPlugin.Instance!.Configuration.GetEffectiveConfig(userId);
        return Create(cfg);
    }

    public GelatoStremioProvider Create(PluginConfiguration cfg)
    {
        var baseUrl = cfg.GetBaseUrl();
        return _cache.GetOrAdd(
            baseUrl,
            url => new GelatoStremioProvider(url, http, log.CreateLogger<GelatoStremioProvider>())
        );
    }

    public void ClearCache() => _cache.Clear();
}

public class CatalogConfig
{
    public string Id { get; set; } = "";
    public string Type { get; set; } = "movie";
    public string Name { get; set; } = "";
    public bool Enabled { get; set; } = false;

    /// <summary>0 means "use global CatalogMaxItems".</summary>
    public int MaxItems { get; set; } = 0;
    public bool CreateCollection { get; set; } = false;
    public string Url { get; set; } = "";
}

/// <summary>
/// One tracked collection. Persisted in the plugin's XML configuration, so every
/// property must be a public settable type the XML serializer can round-trip.
/// </summary>
public class CollectionRow
{
    /// <summary>Stable identifier for this row, independent of its source id.</summary>
    public string Id { get; set; } = "";

    /// <summary>BoxSet display name.</summary>
    public string Name { get; set; } = "";

    public Gelato.Collections.CollectionKind Kind { get; set; } =
        Gelato.Collections.CollectionKind.Franchise;

    public Gelato.Collections.CollectionMode Mode { get; set; } =
        Gelato.Collections.CollectionMode.Auto;

    /// <summary>TMDB collection id, watch-provider id, or Stremio catalog id.</summary>
    public string SourceId { get; set; } = "";

    /// <summary>ISO 3166-1 region. Platform rows only.</summary>
    public string Region { get; set; } = "";

    /// <summary>0 means unlimited.</summary>
    public int MaxItems { get; set; }

    /// <summary>
    /// Refresh floor in days. This never causes a sync — it only suppresses one.
    /// 0 means "every task run".
    /// </summary>
    public int MinIntervalDays { get; set; } = 7;

    public bool Enabled { get; set; } = true;

    /// <summary>Last <em>successful completion</em>. Failed or cancelled runs must not set this.</summary>
    public DateTime? LastSyncedUtc { get; set; }

    /// <summary>Opaque resume state for a partial backfill. Unused until spec phase 2.</summary>
    public string Checkpoint { get; set; } = "";
}
