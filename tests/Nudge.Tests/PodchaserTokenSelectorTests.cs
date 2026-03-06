using System.Net;
using Nudge.Cli.Services;

namespace Nudge.Tests;

public sealed class PodchaserTokenSelectorTests
{
    [Fact]
    public async Task OrderByRemainingQuotaAsync_SortsTokensByHighestRemainingPoints()
    {
        var selector = new PodchaserTokenSelector(new HttpClient(new DelegateHttpMessageHandler(request =>
        {
            var token = request.Headers.Authorization?.Parameter;
            var remaining = token switch
            {
                "primary-token" => "10",
                "fallback-token-1" => "400",
                "fallback-token-2" => "120",
                _ => "0"
            };

            var response = new HttpResponseMessage(HttpStatusCode.OK);
            response.Headers.TryAddWithoutValidation("X-Podchaser-Points-Remaining", remaining);
            return Task.FromResult(response);
        }))
        {
            Timeout = TimeSpan.FromSeconds(5)
        });
        var ordered = await selector.OrderByRemainingQuotaAsync(
            "https://api.podchaser.com/",
            [
                new PodchaserResolvedToken("primary", "primary-token"),
                new PodchaserResolvedToken("fallback-1", "fallback-token-1"),
                new PodchaserResolvedToken("fallback-2", "fallback-token-2")
            ]);

        Assert.Equal(["fallback-1", "fallback-2", "primary"], ordered.Select(token => token.Label).ToArray());
    }

    [Fact]
    public async Task OrderByRemainingQuotaAsync_PreservesOriginalOrderWhenQuotaCannotBeDetermined()
    {
        var selector = new PodchaserTokenSelector(new HttpClient(new DelegateHttpMessageHandler(_ =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK))))
        {
            Timeout = TimeSpan.FromSeconds(5)
        });
        var original = new[]
        {
            new PodchaserResolvedToken("primary", "primary-token"),
            new PodchaserResolvedToken("fallback-1", "fallback-token-1"),
            new PodchaserResolvedToken("fallback-2", "fallback-token-2")
        };

        var ordered = await selector.OrderByRemainingQuotaAsync("https://api.podchaser.com/", original);

        Assert.Equal(original, ordered);
    }

    private sealed class DelegateHttpMessageHandler(
        Func<HttpRequestMessage, Task<HttpResponseMessage>> handler) : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, Task<HttpResponseMessage>> _handler = handler;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            return _handler(request);
        }
    }
}
