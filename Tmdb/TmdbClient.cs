using System.Net;
using MediaBrowser.Common.Configuration;
using Microsoft.Extensions.Logging;

namespace Gelato.Tmdb;

/// <summary>
/// TMDB HTTP access for collection sync: key gating, a single shared concurrency
/// limit, and retry with backoff.
/// </summary>
public sealed class TmdbClient(
    IHttpClientFactory httpFactory,
    TmdbDetailCache cache,
    ILogger<TmdbClient> log
)
{
    private const string BaseUrl = "https://api.themoviedb.org/3";
    private const int MaxAttempts = 5;

    // A collection's `parts` array grows whenever a sequel ships, so its cache entry has to
    // expire. Movie details never change and are read with no maximum age.
    private static readonly TimeSpan CollectionMaxAge = TimeSpan.FromDays(1);

    // One shared gate across every caller. TMDB throttles per key and per IP, so
    // parallelism beyond this buys nothing but 429s.
    private static readonly SemaphoreSlim Gate = new(8, 8);

    /// <summary>The on-disk cache location, under Jellyfin's cache path rather than /tmp —
    /// the shipped systemd unit sets PrivateTmp=yes, so /tmp dies on every restart.</summary>
    public static string CacheDirectoryFor(IApplicationPaths appPaths) =>
        Path.Combine(appPaths.CachePath, "gelato", "tmdb");

    private static string? CurrentKey() =>
        TmdbKeyResolver.Resolve(
            GelatoPlugin.Instance?.Configuration.TmdbApiKey,
            TmdbKeyResolver.FromJellyfinTmdbPlugin
        );

    /// <summary>False when no API key is available — the feature must stay off.</summary>
    public bool IsEnabled => CurrentKey() is not null;

    public Task<TmdbCollection?> GetCollectionAsync(int collectionId, CancellationToken ct) =>
        GetAsync<TmdbCollection>(
            $"collection/{collectionId}",
            $"collection:{collectionId}",
            ct,
            CollectionMaxAge
        );

    public Task<TmdbMovieDetail?> GetMovieAsync(int movieId, CancellationToken ct) =>
        GetAsync<TmdbMovieDetail>($"movie/{movieId}", $"movie:{movieId}", ct, maxCacheAge: null);

    // Release dates for an already-released film do not change, same reasoning as
    // GetMovieAsync: no maximum cache age. Deserializes into the TmdbReleaseDatesContainer /
    // TmdbReleaseDateCountry / TmdbReleaseDateItem types GelatoStremioProvider already defines
    // for the same TMDB response shape, rather than parallel DTOs.
    public Task<TmdbReleaseDatesContainer?> GetReleaseDatesAsync(
        int movieId,
        CancellationToken ct
    ) =>
        GetAsync<TmdbReleaseDatesContainer>(
            $"movie/{movieId}/release_dates",
            $"release_dates:{movieId}",
            ct,
            maxCacheAge: null
        );

    private async Task<T?> GetAsync<T>(
        string path,
        string cacheKey,
        CancellationToken ct,
        TimeSpan? maxCacheAge
    )
        where T : class
    {
        if (cache.TryGet<T>(cacheKey, out var cached, maxCacheAge))
            return cached;

        var key = CurrentKey();
        if (key is null)
        {
            log.LogWarning("TMDB request skipped: no API key configured for collection sync");
            return null;
        }

        var url = $"{BaseUrl}/{path}?api_key={Uri.EscapeDataString(key)}";

        for (var attempt = 1; attempt <= MaxAttempts; attempt++)
        {
            await Gate.WaitAsync(ct).ConfigureAwait(false);
            HttpResponseMessage resp;
            Exception? transportError = null;
            try
            {
                var client = httpFactory.CreateClient(nameof(TmdbClient));
                client.Timeout = TimeSpan.FromSeconds(30);
                resp = await client.GetAsync(url, ct).ConfigureAwait(false);
            }
            // DNS failure, connection reset, TLS error — retryable transport faults. Also
            // HttpClient's own timeout, which since .NET 5 surfaces as TaskCanceledException
            // and so IS an OperationCanceledException. Callers rethrow those to honour real
            // cancellation, so letting a timeout escape would abort a whole backfill. Only a
            // genuine trip of `ct` is allowed out of this method.
            catch (Exception ex)
                when (ex is HttpRequestException
                    || (ex is TaskCanceledException && !ct.IsCancellationRequested)
                )
            {
                transportError = ex;
                resp = null!;
            }
            finally
            {
                Gate.Release();
            }

            // Handled outside the gate so backoff does not occupy a concurrency slot.
            if (transportError is not null)
            {
                if (attempt == MaxAttempts)
                {
                    log.LogWarning(
                        transportError,
                        "TMDB request failed for {Path}: transport error",
                        path
                    );
                    return null;
                }

                var transportDelay = TmdbBackoff.Compute(attempt, null);
                log.LogDebug(
                    transportError,
                    "TMDB transport error for {Path}, retrying in {Delay}s (attempt {Attempt})",
                    path,
                    transportDelay.TotalSeconds,
                    attempt
                );
                await Task.Delay(transportDelay, ct).ConfigureAwait(false);
                continue;
            }

            using (resp)
            {
                if (resp.IsSuccessStatusCode)
                {
                    var body = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
                    T? value;
                    try
                    {
                        value = System.Text.Json.JsonSerializer.Deserialize<T>(
                            body,
                            TmdbJson.Options
                        );
                    }
                    catch (System.Text.Json.JsonException ex)
                    {
                        // A captive portal or corporate proxy answering 200 with an HTML error
                        // page lands here. Not retryable — the response was well-formed HTTP.
                        log.LogWarning(ex, "TMDB returned an unparseable body for {Path}", path);
                        return null;
                    }

                    if (value is not null)
                        cache.Set(cacheKey, value);
                    return value;
                }

                if (resp.StatusCode == HttpStatusCode.NotFound)
                {
                    log.LogDebug("TMDB 404 for {Path}", path);
                    return null;
                }

                var retryable =
                    resp.StatusCode == HttpStatusCode.TooManyRequests
                    || (int)resp.StatusCode >= 500;

                if (!retryable || attempt == MaxAttempts)
                {
                    log.LogWarning(
                        "TMDB request failed for {Path}: {Status}",
                        path,
                        resp.StatusCode
                    );
                    return null;
                }

                var delay = TmdbBackoff.Compute(attempt, resp.Headers.RetryAfter?.Delta);
                log.LogDebug(
                    "TMDB {Status} for {Path}, retrying in {Delay}s (attempt {Attempt})",
                    resp.StatusCode,
                    path,
                    delay.TotalSeconds,
                    attempt
                );
                await Task.Delay(delay, ct).ConfigureAwait(false);
            }
        }

        return null;
    }
}
