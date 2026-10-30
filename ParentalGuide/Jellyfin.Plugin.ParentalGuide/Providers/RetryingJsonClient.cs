using System.Globalization;
using System.Net;
using System.Text.Json;
using Jellyfin.Plugin.ParentalGuide.Configuration;
using Jellyfin.Plugin.ParentalGuide.Core;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.ParentalGuide.Providers;

public sealed record ResponseIssue(string Reason, bool Retryable = false, bool Fatal = false, bool Throttled = false);

public sealed class RetryingJsonClient(HttpClient http, PluginConfiguration options, ILogger logger,
    Func<TimeSpan, CancellationToken, Task>? delay = null, Func<double>? jitter = null)
{
    private readonly Func<TimeSpan, CancellationToken, Task> _delay = delay ?? Task.Delay;
    private readonly Func<double> _jitter = jitter ?? Random.Shared.NextDouble;

    public static TimeSpan? RetryAfter(string? value, DateTimeOffset now)
    {
        if (double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var seconds) && double.IsFinite(seconds))
        {
            return TimeSpan.FromSeconds(Math.Clamp(seconds, 0, TimeSpan.MaxValue.TotalSeconds / 2));
        }

        return DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var date)
            ? (date > now ? date - now : TimeSpan.Zero) : null;
    }

    public async Task<JsonDocument> RequestAsync(string service, Func<HttpRequestMessage> createRequest,
        Func<JsonElement, ResponseIssue?> inspect, double requestDelaySeconds, CancellationToken cancellationToken)
    {
        var sawThrottling = false;
        for (var attempt = 0; ; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (requestDelaySeconds > 0)
            {
                await _delay(TimeSpan.FromSeconds(requestDelaySeconds), cancellationToken).ConfigureAwait(false);
            }

            ResponseIssue issue;
            TimeSpan? retryAfter = null;
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(options.RequestTimeoutSeconds));
            try
            {
                using var request = createRequest();
                using var response = await http.SendAsync(request, HttpCompletionOption.ResponseContentRead, timeout.Token).ConfigureAwait(false);
                if (response.Headers.TryGetValues("Retry-After", out var values))
                {
                    retryAfter = RetryAfter(values.FirstOrDefault(), DateTimeOffset.UtcNow);
                }

                if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
                {
                    throw new FatalProviderException($"{service}: authentication/access failure (HTTP {(int)response.StatusCode}).");
                }

                if (!response.IsSuccessStatusCode)
                {
                    var status = (int)response.StatusCode;
                    issue = new ResponseIssue($"HTTP {status}", status is 408 or 425 or 429 or >= 500,
                        Throttled: status == 429 || (status == 503 && retryAfter.HasValue));
                }
                else
                {
                    await using var body = await response.Content.ReadAsStreamAsync(timeout.Token).ConfigureAwait(false);
                    var document = await JsonDocument.ParseAsync(body, cancellationToken: timeout.Token).ConfigureAwait(false);
                    ResponseIssue? semanticIssue;
                    try
                    {
                        semanticIssue = document.RootElement.ValueKind == JsonValueKind.Object
                            ? inspect(document.RootElement) : new ResponseIssue("invalid JSON response shape", Retryable: true);
                    }
                    catch
                    {
                        document.Dispose();
                        throw;
                    }

                    if (semanticIssue is null)
                    {
                        return document;
                    }

                    document.Dispose();
                    issue = semanticIssue;
                }
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                issue = new ResponseIssue("request timed out", Retryable: true);
            }
            catch (HttpRequestException)
            {
                // Never include request URLs/inner exceptions: the OMDb key is a query parameter.
                issue = new ResponseIssue("network request failed", Retryable: true);
            }
            catch (IOException)
            {
                issue = new ResponseIssue("response transport failed", Retryable: true);
            }
            catch (JsonException)
            {
                issue = new ResponseIssue("invalid JSON response", Retryable: true);
            }

            sawThrottling |= issue.Throttled;
            if (issue.Fatal || (attempt >= options.MaximumRetries && sawThrottling))
            {
                throw new FatalProviderException($"{service}: {issue.Reason}. Task stopped to avoid further requests.");
            }

            if (!issue.Retryable || attempt >= options.MaximumRetries)
            {
                throw new ProviderException($"{service}: {issue.Reason}.");
            }

            var exponential = options.RetryBaseDelaySeconds * Math.Pow(2, attempt);
            var seconds = Math.Min(options.RetryMaximumDelaySeconds, exponential * (1 + 0.25 * _jitter()));
            // Retry-After is a minimum wait and may exceed the calculated backoff cap.
            var wait = TimeSpan.FromSeconds(seconds);
            if (retryAfter > wait)
            {
                wait = retryAfter.Value;
            }

            logger.LogWarning("{Service}: {Reason}; retry {Retry}/{Maximum} in {Delay:F2}s",
                service, issue.Reason, attempt + 1, options.MaximumRetries, wait.TotalSeconds);
            // Chunk very long server-directed waits so cancellation works even for huge Retry-After values.
            while (wait > TimeSpan.Zero)
            {
                var chunk = wait > TimeSpan.FromHours(1) ? TimeSpan.FromHours(1) : wait;
                await _delay(chunk, cancellationToken).ConfigureAwait(false);
                wait -= chunk;
            }
        }
    }
}
