using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Nudge.Ui.Models;

namespace Nudge.Ui.Services;

public sealed class PodchaserQuotaService(HttpClient httpClient)
{
    private const string DefaultBaseUrl = "https://api.podchaser.com/";
    private const string CostPath = "graphql/cost";
    private const string PreviewPayload = """{"query":"query { __typename }"}""";
    private const string UserAgent = "Nudge-Ui/1.0";
    private readonly HttpClient _httpClient = httpClient;

    public async Task<PodchaserQuotaSnapshot> GetSnapshotAsync(CancellationToken cancellationToken = default)
    {
        var configuredTokens = LoadConfiguredTokens();
        if (configuredTokens.Count == 0)
        {
            return new PodchaserQuotaSnapshot
            {
                CheckedAtUtc = DateTimeOffset.UtcNow
            };
        }

        var baseUrl = ResolveBaseUrl();
        var probeTasks = configuredTokens
            .Select(token => ProbeTokenAsync(baseUrl, token.Label, token.Value, cancellationToken))
            .ToArray();
        var probes = await Task.WhenAll(probeTasks);

        return new PodchaserQuotaSnapshot
        {
            CheckedAtUtc = DateTimeOffset.UtcNow,
            Tokens = probes
        };
    }

    private static List<(string Label, string Value)> LoadConfiguredTokens()
    {
        var repoRoot = RepositoryPaths.LocateRepositoryRoot();
        var configPath = Path.Combine(repoRoot, "nudge.local.json");
        if (!File.Exists(configPath))
        {
            return [];
        }

        try
        {
            using var document = JsonDocument.Parse(File.ReadAllText(configPath));
            if (!document.RootElement.TryGetProperty("Podchaser", out var podchaserNode) ||
                podchaserNode.ValueKind != JsonValueKind.Object)
            {
                return [];
            }

            var tokens = new List<(string Label, string Value)>();
            if (TryGetTrimmedString(podchaserNode, "Token", out var primaryToken))
            {
                tokens.Add(("Primary", primaryToken));
            }

            if (podchaserNode.TryGetProperty("FallbackTokens", out var fallbackNode) &&
                fallbackNode.ValueKind == JsonValueKind.Array)
            {
                var fallbackIndex = 1;
                foreach (var item in fallbackNode.EnumerateArray())
                {
                    if (item.ValueKind != JsonValueKind.String)
                    {
                        continue;
                    }

                    var raw = item.GetString();
                    if (string.IsNullOrWhiteSpace(raw))
                    {
                        continue;
                    }

                    tokens.Add(($"Fallback {fallbackIndex}", raw.Trim()));
                    fallbackIndex++;
                }
            }

            return tokens
                .DistinctBy(static token => token.Value, StringComparer.Ordinal)
                .ToList();
        }
        catch (JsonException)
        {
            return [];
        }
    }

    private async Task<PodchaserQuotaTokenStatus> ProbeTokenAsync(
        string baseUrl,
        string label,
        string token,
        CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, new Uri(new Uri(baseUrl), CostPath));
        request.Headers.Accept.Clear();
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        request.Headers.UserAgent.Clear();
        request.Headers.UserAgent.Add(ProductInfoHeaderValue.Parse(UserAgent));
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        request.Content = new StringContent(PreviewPayload, Encoding.UTF8, "application/json");

        try
        {
            using var response = await _httpClient.SendAsync(request, cancellationToken);
            var body = await response.Content.ReadAsStringAsync(cancellationToken);
            return BuildStatus(label, response.StatusCode, response.Headers, body);
        }
        catch (HttpRequestException ex)
        {
            return new PodchaserQuotaTokenStatus
            {
                Label = label,
                Detail = $"Network error: {ex.Message}"
            };
        }
        catch (TaskCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return new PodchaserQuotaTokenStatus
            {
                Label = label,
                Detail = "Timed out while checking quota."
            };
        }
    }

    private static PodchaserQuotaTokenStatus BuildStatus(
        string label,
        HttpStatusCode statusCode,
        HttpResponseHeaders headers,
        string body)
    {
        var remaining = TryParseHeaderInt(headers, "X-Podchaser-Points-Remaining");
        var previewCost = TryParseHeaderInt(headers, "X-Podchaser-Query-Cost");
        var retryAfter = TryParseHeaderInt(headers, "Retry-After");
        var detail = BuildDetail(statusCode, body, previewCost, retryAfter);

        return new PodchaserQuotaTokenStatus
        {
            Label = label,
            IsSuccessful = (int)statusCode is >= 200 and < 300,
            StatusCode = (int)statusCode,
            RemainingPoints = remaining,
            PreviewCost = previewCost,
            RetryAfterSeconds = retryAfter,
            Detail = detail
        };
    }

    private static string ResolveBaseUrl()
    {
        var configured = Environment.GetEnvironmentVariable("NUDGE_PODCAST_API_BASEURL");
        var baseUrl = string.IsNullOrWhiteSpace(configured) ? DefaultBaseUrl : configured.Trim();
        if (!baseUrl.EndsWith("/", StringComparison.Ordinal))
        {
            baseUrl += "/";
        }

        return baseUrl;
    }

    private static int? TryParseHeaderInt(HttpResponseHeaders headers, string headerName)
    {
        if (!headers.TryGetValues(headerName, out var values))
        {
            return null;
        }

        var raw = values.FirstOrDefault();
        return int.TryParse(raw, out var parsed) ? parsed : null;
    }

    private static string BuildDetail(HttpStatusCode statusCode, string body, int? previewCost, int? retryAfterSeconds)
    {
        if ((int)statusCode is >= 200 and < 300)
        {
            if (previewCost.HasValue)
            {
                var pointLabel = previewCost.Value == 1 ? "point" : "points";
                return $"Preview check cost {previewCost.Value} {pointLabel}.";
            }

            return "Quota check succeeded.";
        }

        if (statusCode == HttpStatusCode.BadRequest &&
            body.Contains("remaining points", StringComparison.OrdinalIgnoreCase))
        {
            return "Preview request would exceed remaining points.";
        }

        if (statusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
        {
            return "Token was rejected by Podchaser.";
        }

        if (statusCode == HttpStatusCode.TooManyRequests)
        {
            return retryAfterSeconds.HasValue
                ? $"Rate limited. Retry in {retryAfterSeconds.Value}s."
                : "Rate limited by Podchaser.";
        }

        var trimmedBody = (body ?? string.Empty).Trim();
        if (trimmedBody.Length > 120)
        {
            trimmedBody = trimmedBody[..120] + "...";
        }

        return string.IsNullOrWhiteSpace(trimmedBody)
            ? $"Quota check failed with HTTP {(int)statusCode}."
            : $"HTTP {(int)statusCode}: {trimmedBody}";
    }

    private static bool TryGetTrimmedString(JsonElement node, string propertyName, out string value)
    {
        value = string.Empty;
        if (!node.TryGetProperty(propertyName, out var property) || property.ValueKind != JsonValueKind.String)
        {
            return false;
        }

        var raw = property.GetString();
        if (string.IsNullOrWhiteSpace(raw))
        {
            return false;
        }

        value = raw.Trim();
        return true;
    }
}
