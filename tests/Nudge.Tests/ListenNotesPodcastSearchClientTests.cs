using System.Net;
using System.Net.Http;
using Nudge.Cli.Models;
using Nudge.Cli.Services;

namespace Nudge.Tests;

public sealed class ListenNotesPodcastSearchClientTests
{
    [Fact]
    public async Task SearchAsync_UsesBearerToken_MapsPodchaserResults_AndSkipsNullRss()
    {
        HttpRequestMessage? capturedRequest = null;
        var handler = new DelegateHttpMessageHandler(request =>
        {
            capturedRequest = request;
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(
                    """
                    {
                      "data": {
                        "podcasts": {
                          "data": [
                            {
                              "id": "pod-1",
                              "title": "AI Weekly",
                              "description": "About AI startups.",
                              "rssUrl": "https://example.com/ai.xml",
                              "audienceEstimate": 100000
                            },
                            {
                              "id": "pod-2",
                              "title": "Missing RSS Podcast",
                              "description": "Must be dropped.",
                              "rssUrl": null,
                              "powerScore": 70
                            }
                          ]
                        }
                      }
                    }
                    """)
            };
        });

        var client = BuildClient(handler, new NudgeOptions { ApiKey = "token-part-1.token-part-2.token-part-3", BaseUrl = NudgeOptions.DefaultBaseUrl });
        var results = await client.SearchAsync(["ai", "fitness"], 45);

        Assert.NotNull(capturedRequest);
        Assert.Equal(HttpMethod.Post, capturedRequest!.Method);
        Assert.Equal("Nudge-Podcast-Bot/1.0", capturedRequest.Headers.UserAgent.ToString());
        Assert.Equal("Bearer", capturedRequest.Headers.Authorization?.Scheme);
        Assert.Equal("token-part-1.token-part-2.token-part-3", capturedRequest.Headers.Authorization?.Parameter);
        Assert.Equal("https://api.podchaser.com/graphql", capturedRequest.RequestUri!.ToString());

        Assert.Single(results);
        Assert.Equal("podchaser:pod-1", results[0].Id);
        Assert.Equal("AI Weekly", results[0].Name);
        Assert.Equal("https://example.com/ai.xml", results[0].FeedUrl);
        Assert.True(results[0].EstimatedReach > 0.0);
    }

    [Fact]
    public async Task SearchAsync_RetriesOnce_OnTransientFailure()
    {
        var calls = 0;
        var handler = new DelegateHttpMessageHandler(_ =>
        {
            calls++;
            if (calls == 1)
            {
                return new HttpResponseMessage(HttpStatusCode.InternalServerError);
            }

            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(
                    """
                    {
                      "data": {
                        "podcasts": {
                          "data": [
                            {
                              "id": "pod-9",
                              "title": "Recovered Podcast",
                              "description": "Recovered after retry.",
                              "rssUrl": "https://example.com/recovered.xml",
                              "powerScore": 34
                            }
                          ]
                        }
                      }
                    }
                    """)
            };
        });

        var client = BuildClient(handler, new NudgeOptions { ApiKey = "api-key-value", BaseUrl = NudgeOptions.DefaultBaseUrl });
        var results = await client.SearchAsync(["ai"], 30);

        Assert.Equal(2, calls);
        Assert.Single(results);
        Assert.Equal("podchaser:pod-9", results[0].Id);
    }

    private static ListenNotesPodcastSearchClient BuildClient(HttpMessageHandler handler, NudgeOptions options)
    {
        var httpClient = new HttpClient(handler)
        {
            BaseAddress = new Uri(options.BaseUrl),
            Timeout = TimeSpan.FromSeconds(10)
        };

        return new ListenNotesPodcastSearchClient(httpClient, options);
    }

    private sealed class DelegateHttpMessageHandler(Func<HttpRequestMessage, HttpResponseMessage> handler) : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, HttpResponseMessage> _handler = handler;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            return Task.FromResult(_handler(request));
        }
    }
}
