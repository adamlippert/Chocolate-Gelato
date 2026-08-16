using System.Net;
using Microsoft.Extensions.Logging;

namespace Gelato.Tmdb;

/// <summary>
/// TMDB HTTP access for collection sync: key gating, a single shared concurrency
/// limit, and retry with backoff.
/// </summary>
public sealed class TmdbClient(IHttpClientFactory httpFactory, ILogger<TmdbClient> log)
{
    private const string BaseUrl = "https://api.themoviedb.org/3";
    private const int MaxAttempts = 5;

    // One shared gate across every caller. TMDB throttles per key and per IP, so
    // parallelism beyond this buys nothing but 429s.
    private static readonly SemaphoreSlim Gate = new(8, 8);

    private readonly TmdbDetailCache _cache = new(
        Path.Combine(Path.GetTempPath(), "gelato", "tmdb-cache")
    );

    private static string? CurrentKey() =>
        TmdbKeyResolver.Resolve(
            GelatoPlugin.Instance?.Configuration.TmdbApiKey,
            TmdbKeyResolver.FromJellyfinTmdbPlugin
        );

    /// <summary>False when no API key is available — the feature must stay off.</summary>
    public bool IsEnabled => CurrentKey() is not null;

    public Task<TmdbCollection?> GetCollectionAsync(int collectionId, CancellationToken ct) =>
        GetAsync<TmdbCollection>($"collection/{collectionId}", $"collection:{collectionId}", ct);

    public Task<TmdbMovieDetail?> GetMovieAsync(int movieId, CancellationToken ct) =>
        GetAsync<TmdbMovieDetail>($"movie/{movieId}", $"movie:{movieId}", ct);

    private async Task<T?> GetAsync<T>(string path, string cacheKey, CancellationToken ct)
        where T : class
    {
        if (_cache.TryGet<T>(cacheKey, out var cached))
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
            try
            {
                var client = httpFactory.CreateClient(nameof(TmdbClient));
                client.Timeout = TimeSpan.FromSeconds(30);
                resp = await client.GetAsync(url, ct).ConfigureAwait(false);
            }
            finally
            {
                Gate.Release();
            }

            using (resp)
            {
                if (resp.IsSuccessStatusCode)
                {
                    var body = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
                    var value = System.Text.Json.JsonSerializer.Deserialize<T>(
                        body,
                        TmdbJson.Options
                    );
                    if (value is not null)
                        _cache.Set(cacheKey, value);
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
