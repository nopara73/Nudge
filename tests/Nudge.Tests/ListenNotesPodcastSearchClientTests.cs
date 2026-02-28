using System.Net;
using System.Net.Http;
using Nudge.Cli.Models;
using Nudge.Cli.Services;

namespace Nudge.Tests;

public sealed class ListenNotesPodcastSearchClientTests
{
    [Fact]
    public async Task SearchAsync_MapsResults_NormalizesReach_AndBuildsExpectedRequest()
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
                      "results": [
                        {
                          "id": "pod-1",
                          "title_original": "AI Weekly",
                          "description_original": "About AI startups.",
                          "rss": "https://example.com/ai.xml",
                          "listen_score": 80
                        },
                        {
                          "id": "pod-2",
                          "title_original": "No Score Podcast",
                          "description_original": "Missing score should be neutral.",
                          "rss": "https://example.com/no-score.xml"
                        },
                        {
                          "id": "pod-3",
                          "title_original": "Big Score Podcast",
                          "description_original": "Out of range score should clamp.",
                          "rss": "https://example.com/big-score.xml",
                          "listen_score": 120
                        },
                        {
                          "id": "pod-1",
                          "title_original": "Duplicate By Provider Id",
                          "description_original": "Must be deduped by id only.",
                          "rss": "https://example.com/duplicate.xml",
                          "listen_score": 10
                        },
                        {
                          "id": "pod-4",
                          "title_original": "Missing RSS Podcast",
                          "description_original": "Must be dropped.",
                          "listen_score": 50
                        }
                      ]
                    }
                    """)
            };
        });

        var client = BuildClient(handler, new NudgeOptions { ApiKey = "api-key-value", BaseUrl = NudgeOptions.DefaultBaseUrl });
        var before = DateTimeOffset.UtcNow.AddDays(-45).ToUnixTimeSeconds();
        var results = await client.SearchAsync(["ai", "startups"], 45);
        var after = DateTimeOffset.UtcNow.AddDays(-45).ToUnixTimeSeconds();

        Assert.NotNull(capturedRequest);
        Assert.Equal(HttpMethod.Get, capturedRequest!.Method);
        Assert.Equal("Nudge-Podcast-Bot/1.0", capturedRequest.Headers.UserAgent.ToString());
        Assert.Equal("api-key-value", capturedRequest.Headers.GetValues("X-ListenAPI-Key").Single());
        Assert.Equal("podcast", GetQueryValue(capturedRequest.RequestUri!, "type"));
        Assert.Equal("50", GetQueryValue(capturedRequest.RequestUri!, "len"));
        Assert.Equal("ai startups", Uri.UnescapeDataString(GetQueryValue(capturedRequest.RequestUri!, "q")));

        var publishedAfterValue = long.Parse(GetQueryValue(capturedRequest.RequestUri!, "published_after"));
        Assert.InRange(publishedAfterValue, before, after);

        Assert.Equal(3, results.Count);
        Assert.Equal("listennotes:pod-1", results[0].Id);
        Assert.Equal("AI Weekly", results[0].Name);
        Assert.Equal("https://example.com/ai.xml", results[0].FeedUrl);
        Assert.Equal(0.8, results[0].EstimatedReach, 3);
        Assert.Equal(0.5, results[1].EstimatedReach, 3);
        Assert.Equal(1.0, results[2].EstimatedReach, 3);
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
                      "results": [
                        {
                          "id": "pod-9",
                          "title_original": "Recovered Podcast",
                          "description_original": "Recovered after retry.",
                          "rss": "https://example.com/recovered.xml",
                          "listen_score": -10
                        }
                      ]
                    }
                    """)
            };
        });

        var client = BuildClient(handler, new NudgeOptions { ApiKey = "api-key-value", BaseUrl = NudgeOptions.DefaultBaseUrl });
        var results = await client.SearchAsync(["ai"], 30);

        Assert.Equal(2, calls);
        Assert.Single(results);
        Assert.Equal(0.0, results[0].EstimatedReach, 3);
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

    private static string GetQueryValue(Uri uri, string key)
    {
        foreach (var pair in uri.Query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var parts = pair.Split('=', 2);
            if (parts.Length == 2 && parts[0].Equals(key, StringComparison.Ordinal))
            {
                return parts[1];
            }
        }

        throw new InvalidOperationException($"Query parameter '{key}' was not found.");
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
