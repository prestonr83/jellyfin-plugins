using System.Net;
using System.Text;
using System.Text.Json;
using Jellyfin.Plugin.ParentalGuide.Configuration;
using Jellyfin.Plugin.ParentalGuide.Core;
using Jellyfin.Plugin.ParentalGuide.Providers;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Jellyfin.Plugin.ParentalGuide.Tests;

public class ProviderTests
{
    private const string Movie = """{"Response":"True","Title":"Example","Year":"2024","Type":"movie","imdbID":"tt1234567","Rated":"PG-13","Language":"English, French"}""";
    private static HttpResponseMessage Json(string payload, int status = 200) => new((HttpStatusCode)status)
    {
        Content = new StringContent(payload, Encoding.UTF8, "application/json")
    };
    private static PluginConfiguration Settings(int retries = 2)
    {
        var options = MetadataRulesTests.Options();
        options.MaximumRetries = retries;
        options.RetryMaximumDelaySeconds = 3;
        return options;
    }

    private static MetadataSource Source(HttpClient http, PluginConfiguration options, List<TimeSpan>? waits = null) =>
        new(new RetryingJsonClient(http, options, NullLogger.Instance,
            (duration, _) => { waits?.Add(duration); return Task.CompletedTask; }, () => 0.5), options);

    [Fact]
    public async Task HttpBackoffIncludesJitterAndHonorsRetryAfterBeyondCap()
    {
        using var handler = new SequenceHandler((request, n) =>
        {
            if (n == 1)
            {
                var response = Json("{}", 429);
                response.Headers.Add("Retry-After", "7");
                return response;
            }
            return n == 2 ? Json("{}", 503) : Json(Movie);
        });
        using var http = new HttpClient(handler);
        var waits = new List<TimeSpan>();
        var result = await Source(http, Settings(), waits).FindAsync(MetadataRulesTests.Title(), default);
        Assert.Equal("PG-13", result!.Rating);
        Assert.Equal(new[] { TimeSpan.FromSeconds(7), TimeSpan.FromSeconds(2.25) }, waits);
        Assert.Equal(3, handler.Requests);
    }

    [Theory]
    [InlineData(401)]
    [InlineData(403)]
    public async Task HttpAuthenticationFailuresStopImmediately(int status)
    {
        using var handler = new SequenceHandler((_, _) => Json("{}", status));
        using var http = new HttpClient(handler);
        await Assert.ThrowsAsync<FatalProviderException>(() => Source(http, Settings()).FindAsync(MetadataRulesTests.Title(), default));
        Assert.Equal(1, handler.Requests);
    }

    [Fact]
    public async Task PersistentHttpThrottlingStopsAfterBoundedRetries()
    {
        using var handler = new SequenceHandler((_, _) => Json("{}", 429));
        using var http = new HttpClient(handler);
        await Assert.ThrowsAsync<FatalProviderException>(() => Source(http, Settings()).FindAsync(MetadataRulesTests.Title(), default));
        Assert.Equal(3, handler.Requests);
    }

    [Fact]
    public async Task PersistentServerBackpressureWithRetryAfterStopsTask()
    {
        using var handler = new SequenceHandler((_, _) =>
        {
            var response = Json("{}", 503);
            response.Headers.Add("Retry-After", "1");
            return response;
        });
        using var http = new HttpClient(handler);
        await Assert.ThrowsAsync<FatalProviderException>(() => Source(http, Settings()).FindAsync(MetadataRulesTests.Title(), default));
        Assert.Equal(3, handler.Requests);
    }

    [Theory]
    [InlineData("Invalid API key!")]
    [InlineData("Request limit reached!")]
    [InlineData("Daily limit exhausted")]
    [InlineData("Quota exhausted")]
    public async Task OmdbKeyAndQuotaErrorsStopWithoutRetry(string error)
    {
        using var handler = new SequenceHandler((_, _) => Json(JsonSerializer.Serialize(new { Response = "False", Error = error })));
        using var http = new HttpClient(handler);
        await Assert.ThrowsAsync<FatalProviderException>(() => Source(http, Settings()).FindAsync(MetadataRulesTests.Title(), default));
        Assert.Equal(1, handler.Requests);
    }

    [Fact]
    public async Task OmdbSemanticTransientFailureRetriesAndDoesNotLeakKey()
    {
        using var handler = new SequenceHandler((_, n) => n < 3
            ? Json("""{"Response":"False","Error":"temporarily unavailable test-key"}""") : Json(Movie));
        using var http = new HttpClient(handler);
        var waits = new List<TimeSpan>();
        Assert.NotNull(await Source(http, Settings(), waits).FindAsync(MetadataRulesTests.Title(), default));
        Assert.Equal(2, waits.Count);
        using var failedHandler = new SequenceHandler((_, _) => Json("""{"Response":"False","Error":"failure test-key"}"""));
        using var failedHttp = new HttpClient(failedHandler);
        var exception = await Assert.ThrowsAsync<ProviderException>(() => Source(failedHttp, Settings()).FindAsync(MetadataRulesTests.Title(), default));
        Assert.DoesNotContain("test-key", exception.ToString());
    }

    [Fact]
    public async Task PersistentOmdbPayloadThrottlingIsFatal()
    {
        using var handler = new SequenceHandler((_, _) => Json("""{"Response":"False","Error":"Too many requests"}"""));
        using var http = new HttpClient(handler);
        await Assert.ThrowsAsync<FatalProviderException>(() => Source(http, Settings()).FindAsync(MetadataRulesTests.Title(), default));
        Assert.Equal(3, handler.Requests);
    }

    [Fact]
    public async Task TransientNetworkAndMalformedJsonResponsesAreRetried()
    {
        using var handler = new SequenceHandler((_, n) => n switch
        {
            1 => throw new HttpRequestException("secret test-key"),
            2 => Json("<html>temporary failure</html>"),
            _ => Json(Movie)
        });
        using var http = new HttpClient(handler);
        Assert.NotNull(await Source(http, Settings()).FindAsync(MetadataRulesTests.Title(), default));
        Assert.Equal(3, handler.Requests);
    }

    [Fact]
    public async Task ExhaustedTransientErrorsRemainItemErrors()
    {
        using var handler = new SequenceHandler((_, _) => Json("{}", 503));
        using var http = new HttpClient(handler);
        await Assert.ThrowsAsync<ProviderException>(() => Source(http, Settings()).FindAsync(MetadataRulesTests.Title(), default));
        Assert.Equal(3, handler.Requests);
    }

    [Fact]
    public async Task ValidTitleFallbackUsesYearAndRejectsFuzzyMatches()
    {
        using var handler = new SequenceHandler((request, n) =>
        {
            if (n == 1) return Json("""{"Response":"False","Error":"Incorrect IMDb ID."}""");
            Assert.Contains("t=Example", request.RequestUri!.Query);
            Assert.Contains("y=2024", request.RequestUri.Query);
            Assert.Contains("type=movie", request.RequestUri.Query);
            return Json(Movie);
        });
        using var http = new HttpClient(handler);
        Assert.NotNull(await Source(http, Settings()).FindAsync(MetadataRulesTests.Title(), default));
        using var fuzzy = new SequenceHandler((_, _) => Json(Movie.Replace("Example", "Example Part II")));
        using var fuzzyHttp = new HttpClient(fuzzy);
        Assert.Null(await Source(fuzzyHttp, Settings()).FindAsync(MetadataRulesTests.Title() with { ImdbId = null }, default));
    }

    [Fact]
    public async Task TitleFallbackRejectsWrongYearOrType()
    {
        foreach (var payload in new[] { Movie.Replace("2024", "2023"), Movie.Replace("movie", "series") })
        {
            using var handler = new SequenceHandler((_, _) => Json(payload));
            using var http = new HttpClient(handler);
            Assert.Null(await Source(http, Settings()).FindAsync(MetadataRulesTests.Title() with { ImdbId = null }, default));
        }
    }

    [Fact]
    public async Task DisabledFallbackDoesNotSpendRequestsOnMissingIds()
    {
        var options = Settings();
        options.TitleFallback = false;
        using var handler = new SequenceHandler((_, _) => Json(Movie));
        using var http = new HttpClient(handler);
        Assert.Null(await Source(http, options).FindAsync(MetadataRulesTests.Title() with { ImdbId = null }, default));
        Assert.Equal(0, handler.Requests);
    }

    [Fact]
    public async Task GraphQlRetriesPayloadThrottleAndReturnsAllLowVoteCategories()
    {
        var payload = JsonSerializer.Serialize(new
        {
            data = new
            {
                title = new
                {
                    parentsGuide = new
                    {
                        categories = MetadataRules.CategoryNames.Select(name => new
                        {
                            category = new { text = name }, severity = new { text = "Mild" }, totalSeverityVotes = 0
                        })
                    }
                }
            }
        });
        using var handler = new SequenceHandler((request, n) =>
        {
            Assert.Equal(HttpMethod.Post, request.Method);
            Assert.Equal("imdb-web-next", Assert.Single(request.Headers.GetValues("x-imdb-client-name")));
            return n == 1 ? Json("""{"errors":[{"message":"Slow down","extensions":{"code":"THROTTLED"}}]}""") : Json(payload);
        });
        using var http = new HttpClient(handler);
        var guide = await Source(http, Settings()).GetGuideAsync("tt1234567", default);
        Assert.Equal(5, guide.Count);
        Assert.All(guide, category => Assert.Equal(0, category.Votes));
        Assert.Equal(2, handler.Requests);
    }

    [Theory]
    [InlineData("UNAUTHENTICATED")]
    [InlineData("FORBIDDEN")]
    public async Task GraphQlAuthenticationStopsImmediately(string code)
    {
        using var handler = new SequenceHandler((_, _) => Json(JsonSerializer.Serialize(new
        {
            errors = new[] { new { message = "Denied", extensions = new { code } } }
        })));
        using var http = new HttpClient(handler);
        await Assert.ThrowsAsync<FatalProviderException>(() => Source(http, Settings()).GetGuideAsync("tt1234567", default));
        Assert.Equal(1, handler.Requests);
    }

    [Fact]
    public async Task PersistentGraphQlThrottlingStopsTask()
    {
        using var handler = new SequenceHandler((_, _) => Json("""{"errors":[{"message":"Rate limit reached"}]}"""));
        using var http = new HttpClient(handler);
        await Assert.ThrowsAsync<FatalProviderException>(() => Source(http, Settings()).GetGuideAsync("tt1234567", default));
        Assert.Equal(3, handler.Requests);
    }

    [Fact]
    public async Task UnexpectedGuideShapeIsNotCompletedAsNoData()
    {
        using var handler = new SequenceHandler((_, _) => Json("""{"data":{"title":{"newField":[]}}}"""));
        using var http = new HttpClient(handler);
        await Assert.ThrowsAsync<ProviderException>(() => Source(http, Settings()).GetGuideAsync("tt1234567", default));
    }

    [Theory]
    [InlineData("{\"data\":{\"title\":null}}")]
    [InlineData("{\"data\":{\"title\":{\"parentsGuide\":null}}}")]
    [InlineData("{\"data\":{\"title\":{\"parentsGuide\":{\"categories\":null}}}}")]
    public async Task ExplicitNullGuidesAreValidNoData(string payload)
    {
        using var handler = new SequenceHandler((_, _) => Json(payload));
        using var http = new HttpClient(handler);
        Assert.Empty(await Source(http, Settings()).GetGuideAsync("tt1234567", default));
    }

    [Fact]
    public void RetryAfterSupportsHttpDateSecondsAndPastDates()
    {
        var now = new DateTimeOffset(2026, 10, 17, 12, 0, 0, TimeSpan.Zero);
        Assert.Equal(TimeSpan.FromSeconds(10), RetryingJsonClient.RetryAfter("10", now));
        Assert.Equal(TimeSpan.FromSeconds(30), RetryingJsonClient.RetryAfter("Sat, 17 Oct 2026 12:00:30 GMT", now));
        Assert.Equal(TimeSpan.Zero, RetryingJsonClient.RetryAfter("Fri, 16 Oct 2026 12:00:30 GMT", now));
        Assert.Null(RetryingJsonClient.RetryAfter("bad", now));
    }

    [Fact]
    public async Task CancellationDuringBackoffStopsRequests()
    {
        using var cancellation = new CancellationTokenSource();
        using var handler = new SequenceHandler((_, _) => Json("{}", 429));
        using var http = new HttpClient(handler);
        var options = Settings();
        var client = new RetryingJsonClient(http, options, NullLogger.Instance, (_, token) =>
        {
            cancellation.Cancel();
            return Task.FromCanceled(token);
        });
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => new MetadataSource(client, options)
            .FindAsync(MetadataRulesTests.Title(), cancellation.Token));
        Assert.Equal(1, handler.Requests);
    }

    private sealed class SequenceHandler(Func<HttpRequestMessage, int, HttpResponseMessage> response) : HttpMessageHandler
    {
        public int Requests { get; private set; }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            return Task.FromResult(response(request, ++Requests));
        }
    }
}
