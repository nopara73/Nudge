using System.Net.Http.Headers;
using System.Text;

namespace Nudge.Cli.Services;

public sealed class PodchaserTokenSelector(HttpClient httpClient)
{
    private const string CostPath = "graphql/cost";
    private const string PreviewPayload = """{"query":"query { __typename }"}""";
    private const string UserAgent = "Nudge-Cli/1.0";
    private readonly HttpClient _httpClient = httpClient;

    public async Task<IReadOnlyList<PodchaserResolvedToken>> OrderByRemainingQuotaAsync(
        string baseUrl,
        IReadOnlyList<PodchaserResolvedToken> tokens,
        CancellationToken cancellationToken = default)
    {
        if (tokens.Count <= 1)
        {
            return tokens;
        }

        var probeTasks = tokens
            .Select((token, index) => ProbeAsync(baseUrl, token, index, cancellationToken))
            .ToArray();
        var probes = await Task.WhenAll(probeTasks);

        if (!probes.Any(probe => probe.RemainingPoints.HasValue))
        {
            return tokens;
        }

        return probes
            .OrderByDescending(static probe => probe.RemainingPoints.HasValue)
            .ThenByDescending(static probe => probe.RemainingPoints ?? int.MinValue)
            .ThenBy(static probe => probe.Index)
            .Select(static probe => probe.Token)
            .ToArray();
    }

    private async Task<PodchaserTokenProbeResult> ProbeAsync(
        string baseUrl,
        PodchaserResolvedToken token,
        int index,
        CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, new Uri(new Uri(baseUrl), CostPath));
        request.Headers.Accept.Clear();
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        request.Headers.UserAgent.Clear();
        request.Headers.UserAgent.Add(ProductInfoHeaderValue.Parse(UserAgent));
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token.Value);
        request.Content = new StringContent(PreviewPayload, Encoding.UTF8, "application/json");

        try
        {
            using var response = await _httpClient.SendAsync(request, cancellationToken);
            return new PodchaserTokenProbeResult(
                token,
                index,
                TryParseHeaderInt(response.Headers, "X-Podchaser-Points-Remaining"));
        }
        catch (HttpRequestException)
        {
            return new PodchaserTokenProbeResult(token, index, null);
        }
        catch (TaskCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return new PodchaserTokenProbeResult(token, index, null);
        }
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

    private sealed record PodchaserTokenProbeResult(
        PodchaserResolvedToken Token,
        int Index,
        int? RemainingPoints);
}
