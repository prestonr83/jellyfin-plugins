using System.Globalization;
using System.Net.Http.Json;
using System.Text.Json;
using Jellyfin.Plugin.ParentalGuide.Configuration;
using Jellyfin.Plugin.ParentalGuide.Core;

namespace Jellyfin.Plugin.ParentalGuide.Providers;

public sealed class MetadataSource(RetryingJsonClient http, PluginConfiguration options) : IMetadataSource
{
    private const string GuideQuery = """
        query ParentsGuide($id: ID!) {
          title(id: $id) {
            parentsGuide {
              categories {
                category { text }
                severity { text }
                totalSeverityVotes
              }
            }
          }
        }
        """;

    private static string? Text(JsonElement element, string key) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(key, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString() : null;

    private static bool ContainsAny(string text, params string[] terms) => terms.Any(text.Contains);

    private static bool NotFound(string text) => text is "movie not found!" or "series not found!" or "incorrect imdb id.";

    public static ResponseIssue? InspectOmdb(JsonElement root)
    {
        if (!string.Equals(Text(root, "Response"), "False", StringComparison.OrdinalIgnoreCase))
        {
            return Text(root, "Title") is not null ? null : new ResponseIssue("missing OMDb title data", Retryable: true);
        }

        var error = (Text(root, "Error") ?? "").ToLowerInvariant();
        if (NotFound(error))
        {
            return null;
        }

        if (ContainsAny(error, "api key", "apikey", "request limit", "daily limit", "quota", "unauthorized", "forbidden", "authentication"))
        {
            return new ResponseIssue("OMDb API key rejected or quota exhausted", Fatal: true);
        }

        var throttled = ContainsAny(error, "too many", "toomanyrequests", "rate limit", "ratelimit", "throttl");
        var transient = throttled || ContainsAny(error, "temporar", "unavailable", "try again", "timeout", "timed out", "internal server");
        // Deliberately avoid echoing arbitrary provider messages, which could contain credentials.
        return new ResponseIssue(throttled ? "OMDb throttled the request" : "OMDb lookup failed", transient, Throttled: throttled);
    }

    public static ResponseIssue? InspectGuide(JsonElement root)
    {
        if (!root.TryGetProperty("errors", out var errors) || errors.ValueKind == JsonValueKind.Null ||
            (errors.ValueKind == JsonValueKind.Array && errors.GetArrayLength() == 0))
        {
            return root.TryGetProperty("data", out var data) && data.ValueKind == JsonValueKind.Object && data.TryGetProperty("title", out _)
                ? null : new ResponseIssue("missing IMDb response data", Retryable: true);
        }

        if (errors.ValueKind != JsonValueKind.Array)
        {
            return new ResponseIssue("invalid IMDb error response", Retryable: true);
        }

        // Include GraphQL extension codes as well as messages when classifying errors.
        var text = errors.GetRawText().ToLowerInvariant();
        if (ContainsAny(text, "unauthorized", "unauthenticated", "forbidden", "authentication", "access_denied", "access denied"))
        {
            return new ResponseIssue("IMDb authentication/access failure", Fatal: true);
        }

        var throttled = ContainsAny(text, "too many", "toomanyrequests", "rate limit", "ratelimit", "rate_limit", "throttl");
        var transient = throttled || ContainsAny(text, "temporar", "unavailable", "try again", "timeout", "timed out", "internal_server", "internal server");
        return new ResponseIssue(throttled ? "IMDb throttled the request" : "IMDb GraphQL request failed", transient, Throttled: throttled);
    }

    private async Task<JsonDocument> LookupAsync(string query, CancellationToken cancellationToken) =>
        await http.RequestAsync("OMDb", () => new HttpRequestMessage(HttpMethod.Get,
                "https://www.omdbapi.com/?apikey=" + Uri.EscapeDataString(options.OmdbApiKey.Trim()) + "&" + query),
            InspectOmdb, options.OmdbDelaySeconds, cancellationToken).ConfigureAwait(false);

    private static bool Found(JsonElement root) => !string.Equals(Text(root, "Response"), "False", StringComparison.OrdinalIgnoreCase);
    private static OmdbTitle ReadTitle(JsonElement root) => new(Text(root, "imdbID"), Text(root, "Rated"), Text(root, "Language"));

    public async Task<OmdbTitle?> FindAsync(MediaTitle item, CancellationToken cancellationToken)
    {
        if (MetadataRules.ValidImdbId(item.ImdbId))
        {
            using var byId = await LookupAsync("i=" + Uri.EscapeDataString(item.ImdbId!), cancellationToken).ConfigureAwait(false);
            if (Found(byId.RootElement))
            {
                return ReadTitle(byId.RootElement);
            }
        }

        if (!options.TitleFallback)
        {
            return null;
        }

        var query = "t=" + Uri.EscapeDataString(item.Name) + "&type=" + (item.IsSeries ? "series" : "movie");
        if (item.Year.HasValue)
        {
            query += "&y=" + item.Year.Value.ToString(CultureInfo.InvariantCulture);
        }

        using var byName = await LookupAsync(query, cancellationToken).ConfigureAwait(false);
        var root = byName.RootElement;
        if (!Found(root) || MetadataRules.NormalizeTitle(Text(root, "Title") ?? "") != MetadataRules.NormalizeTitle(item.Name))
        {
            return null;
        }

        // Do not accept a same-name remake or a different content type as a fallback.
        var type = Text(root, "Type");
        var year = Text(root, "Year");
        if ((type is not null && type != (item.IsSeries ? "series" : "movie")) ||
            (item.Year.HasValue && year is { Length: >= 4 } && year[..4] != item.Year.Value.ToString(CultureInfo.InvariantCulture)))
        {
            return null;
        }

        return ReadTitle(root);
    }

    public async Task<IReadOnlyList<GuideCategory>> GetGuideAsync(string imdbId, CancellationToken cancellationToken)
    {
        if (!MetadataRules.ValidImdbId(imdbId))
        {
            throw new ProviderException("Invalid IMDb ID.");
        }

        using var document = await http.RequestAsync("IMDb Parents Guide", () =>
        {
            var request = new HttpRequestMessage(HttpMethod.Post, "https://api.graphql.imdb.com/")
            {
                Content = JsonContent.Create(new { query = GuideQuery, variables = new { id = imdbId } })
            };
            request.Headers.Add("Origin", "https://www.imdb.com");
            request.Headers.Referrer = new Uri("https://www.imdb.com/");
            request.Headers.Add("x-imdb-client-name", "imdb-web-next");
            return request;
        }, InspectGuide, options.ImdbDelaySeconds, cancellationToken).ConfigureAwait(false);

        var title = document.RootElement.GetProperty("data").GetProperty("title");
        if (title.ValueKind == JsonValueKind.Null)
        {
            return [];
        }

        if (title.ValueKind != JsonValueKind.Object || !title.TryGetProperty("parentsGuide", out var guide))
        {
            // Schema changes are failures, never terminal no-data checkpoints.
            throw new ProviderException("IMDb returned an unexpected Parents Guide structure.");
        }

        if (guide.ValueKind == JsonValueKind.Null)
        {
            return [];
        }

        if (guide.ValueKind != JsonValueKind.Object || !guide.TryGetProperty("categories", out var categories))
        {
            throw new ProviderException("IMDb returned an unexpected Parents Guide structure.");
        }

        if (categories.ValueKind == JsonValueKind.Null)
        {
            return [];
        }

        if (categories.ValueKind != JsonValueKind.Array)
        {
            throw new ProviderException("IMDb returned an unexpected category collection.");
        }

        var result = new List<GuideCategory>();
        foreach (var entry in categories.EnumerateArray())
        {
            if (entry.ValueKind != JsonValueKind.Object)
            {
                continue;
            }

            var name = entry.TryGetProperty("category", out var category) ? Text(category, "text")?.Trim() : null;
            var severity = entry.TryGetProperty("severity", out var level) ? Text(level, "text")?.Trim() : null;
            var votes = entry.TryGetProperty("totalSeverityVotes", out var voteElement) &&
                voteElement.ValueKind == JsonValueKind.Number && voteElement.TryGetInt32(out var count) ? Math.Max(0, count) : 0;
            if (name is not null && MetadataRules.CategoryNames.Contains(name, StringComparer.Ordinal) && MetadataRules.KnownSeverity(severity))
            {
                result.Add(new GuideCategory(name, severity!, votes));
            }
        }

        return result;
    }
}
