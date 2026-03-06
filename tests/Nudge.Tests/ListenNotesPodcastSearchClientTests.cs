using System.Net;
using System.Net.Http;
using System.Text.Json;
using Nudge.Cli.Models;
using Nudge.Cli.Services;

namespace Nudge.Tests;

public sealed class ListenNotesPodcastSearchClientTests
{
    [Fact]
    public async Task SearchAsync_UsesBearerToken_MapsPodchaserResults_AndSkipsNullRss()
    {
        HttpRequestMessage? capturedRequest = null;
        string? capturedRequestBody = null;
        var handler = new DelegateHttpMessageHandler(async (request, cancellationToken) =>
        {
            capturedRequest = request;
            capturedRequestBody = request.Content is null ? null : await request.Content.ReadAsStringAsync(cancellationToken);
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
                              "language": "en-US",
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
        Assert.NotNull(capturedRequestBody);
        Assert.Contains("episodeAudienceEstimate", capturedRequestBody!, StringComparison.Ordinal);
        Assert.Contains("socialFollowerCounts", capturedRequestBody!, StringComparison.Ordinal);

        Assert.Single(results);
        Assert.Equal("podchaser:pod-1", results[0].Id);
        Assert.Equal("AI Weekly", results[0].Name);
        Assert.Equal("en-US", results[0].Language);
        Assert.Equal("https://example.com/ai.xml", results[0].FeedUrl);
        Assert.True(results[0].EstimatedReach > 0.0);
    }

    [Fact]
    public async Task SearchAsync_RetriesOnce_OnTransientFailure()
    {
        var calls = 0;
        var handler = new DelegateHttpMessageHandler((_, _) =>
        {
            calls++;
            if (calls == 1)
            {
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.InternalServerError));
            }

            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
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
            });
        });

        var client = BuildClient(handler, new NudgeOptions { ApiKey = "api-key-value", BaseUrl = NudgeOptions.DefaultBaseUrl });
        var results = await client.SearchAsync(["ai"], 30);

        Assert.Equal(2, calls);
        Assert.Single(results);
        Assert.Equal("podchaser:pod-9", results[0].Id);
        Assert.Equal(0.34, results[0].EstimatedReach, 3);
    }

    [Fact]
    public async Task SearchAsync_BlendsMultipleSignals_ForEstimatedReach()
    {
        var handler = new DelegateHttpMessageHandler((_, _) => Task.FromResult(
            new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(
                    """
                    {
                      "data": {
                        "podcasts": {
                          "data": [
                            {
                              "id": "pod-42",
                              "title": "Signal Blend Podcast",
                              "description": "Combines multiple audience signals.",
                              "rssUrl": "https://example.com/signal.xml",
                              "audienceEstimate": 1000000,
                              "powerScore": 80,
                              "episodeAudienceEstimate": {
                                "from": 200000,
                                "to": 400000
                              },
                              "socialFollowerCounts": {
                                "youtube": 100000,
                                "instagram": 50000,
                                "twitter": 25000
                              }
                            }
                          ]
                        }
                      }
                    }
                    """)
            }));

        var client = BuildClient(handler, new NudgeOptions { ApiKey = "api-key-value", BaseUrl = NudgeOptions.DefaultBaseUrl });
        var results = await client.SearchAsync(["ai"], 30);

        Assert.Single(results);
        Assert.Equal("podchaser:pod-42", results[0].Id);
        Assert.Equal(0.825, results[0].EstimatedReach, 3);
    }

    [Fact]
    public async Task SearchAsync_FallsBackToLegacyQuery_WhenExtendedFieldsAreUnauthorized()
    {
        var calls = 0;
        var requestBodies = new List<string>();
        var handler = new DelegateHttpMessageHandler(async (request, cancellationToken) =>
        {
            calls++;
            var body = request.Content is null ? string.Empty : await request.Content.ReadAsStringAsync(cancellationToken);
            requestBodies.Add(body);
            if (calls == 1)
            {
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(
                        """
                        {
                          "errors": [
                            {
                              "message": "Field \"episodeAudienceEstimate\" is not accessible for this plan"
                            }
                          ],
                          "data": {
                            "podcasts": null
                          }
                        }
                        """)
                };
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
                              "id": "pod-fallback",
                              "title": "Fallback Podcast",
                              "description": "Legacy fields still available.",
                              "rssUrl": "https://example.com/fallback.xml",
                              "powerScore": 60
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
        Assert.Equal(2, requestBodies.Count);
        Assert.Contains("episodeAudienceEstimate", requestBodies[0], StringComparison.Ordinal);
        Assert.DoesNotContain("episodeAudienceEstimate", requestBodies[1], StringComparison.Ordinal);
        Assert.Single(results);
        Assert.Equal("podchaser:pod-fallback", results[0].Id);
        Assert.Equal(0.6, results[0].EstimatedReach, 3);
    }

    [Fact]
    public async Task SearchAsync_SetsBudgetExhaustedFlag_WhenApiReturnsRemainingPointsError()
    {
        var handler = new DelegateHttpMessageHandler((_, _) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.BadRequest)
            {
                Content = new StringContent(
                    """
                    {
                      "message": "This request would exceed your remaining points."
                    }
                    """)
            }));

        var client = BuildClient(handler, new NudgeOptions { ApiKey = "api-key-value", BaseUrl = NudgeOptions.DefaultBaseUrl });
        var results = await client.SearchAsync(["ai"], 30);

        Assert.Empty(results);
        Assert.True(client.WasPointBudgetExceeded);
    }

    [Fact]
    public async Task SearchAsync_SetsTokenRejectedFlag_WhenApiReturnsUnauthorized()
    {
        var handler = new DelegateHttpMessageHandler((_, _) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.Unauthorized)
            {
                Content = new StringContent("{\"message\":\"Unauthorized\"}")
            }));

        var client = BuildClient(handler, new NudgeOptions { ApiKey = "api-key-value", BaseUrl = NudgeOptions.DefaultBaseUrl });
        var results = await client.SearchAsync(["ai"], 30);

        Assert.Empty(results);
        Assert.True(client.WasTokenRejected);
    }

    [Fact]
    public async Task SearchAsync_UsesConservativeFallback_WhenNoReachSignalsExist()
    {
        var handler = new DelegateHttpMessageHandler((_, _) => Task.FromResult(
            new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(
                    """
                    {
                      "data": {
                        "podcasts": {
                          "data": [
                            {
                              "id": "pod-low-confidence",
                              "title": "Low Confidence Reach Podcast",
                              "description": "No audience metrics available.",
                              "rssUrl": "https://example.com/low-confidence.xml"
                            }
                          ]
                        }
                      }
                    }
                    """)
            }));

        var client = BuildClient(handler, new NudgeOptions { ApiKey = "api-key-value", BaseUrl = NudgeOptions.DefaultBaseUrl });
        var results = await client.SearchAsync(["ai"], 30);

        Assert.Single(results);
        Assert.Equal("podchaser:pod-low-confidence", results[0].Id);
        Assert.Equal(0.2, results[0].EstimatedReach, 3);
    }

    [Fact]
    public async Task SearchAsync_PaginatesBeyondFirstPage_UntilExhausted()
    {
        var calls = 0;
        var handler = new DelegateHttpMessageHandler(async (request, cancellationToken) =>
        {
            calls++;
            var requestBody = request.Content is null ? string.Empty : await request.Content.ReadAsStringAsync(cancellationToken);
            var page = ExtractPage(requestBody);
            var items = page == 0
                ? Enumerable.Range(1, 50)
                    .Select(i => $$"""
                    {
                      "id": "pod-{{i}}",
                      "title": "Podcast {{i}}",
                      "description": "Page one item",
                      "language": "en",
                      "rssUrl": "https://example.com/{{i}}.xml",
                      "powerScore": 40
                    }
                    """)
                : [
                    """
                    {
                      "id": "pod-51",
                      "title": "Podcast 51",
                      "description": "Page two item",
                      "language": "en",
                      "rssUrl": "https://example.com/51.xml",
                      "powerScore": 40
                    }
                    """
                ];
            var payload =
                $$"""
                {
                  "data": {
                    "podcasts": {
                      "data": [{{string.Join(",", items)}}]
                    }
                  }
                }
                """;
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(payload)
            };
        });

        var client = BuildClient(handler, new NudgeOptions { ApiKey = "api-key-value", BaseUrl = NudgeOptions.DefaultBaseUrl });
        var results = await client.SearchAsync(["ai"], 30);

        Assert.Equal(2, calls);
        Assert.Equal(51, results.Count);
        Assert.Contains(results, r => r.Id == "podchaser:pod-1");
        Assert.Contains(results, r => r.Id == "podchaser:pod-51");
    }

    [Fact]
    public async Task SearchAsync_WithMultipleKeywords_MergesDistinctResultsAcrossTerms()
    {
        var calls = 0;
        var handler = new DelegateHttpMessageHandler(async (request, cancellationToken) =>
        {
            calls++;
            var requestBody = request.Content is null ? string.Empty : await request.Content.ReadAsStringAsync(cancellationToken);
            var term = ExtractSearchTerm(requestBody);
            var item = term switch
            {
                "alpha" =>
                    """
                    {
                      "id": "pod-alpha",
                      "title": "Alpha Podcast",
                      "description": "Alpha term result.",
                      "language": "en",
                      "rssUrl": "https://example.com/alpha.xml",
                      "powerScore": 40
                    }
                    """,
                "beta" =>
                    """
                    {
                      "id": "pod-beta",
                      "title": "Beta Podcast",
                      "description": "Beta term result.",
                      "language": "en",
                      "rssUrl": "https://example.com/beta.xml",
                      "powerScore": 45
                    }
                    """,
                _ => string.Empty
            };
            var data = string.IsNullOrWhiteSpace(item) ? "[]" : $"[{item}]";
            var payload =
                $$"""
                {
                  "data": {
                    "podcasts": {
                      "data": {{data}}
                    }
                  }
                }
                """;

            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(payload)
            };
        });

        var client = BuildClient(handler, new NudgeOptions { ApiKey = "api-key-value", BaseUrl = NudgeOptions.DefaultBaseUrl });
        var results = await client.SearchAsync(["alpha", "beta"], 30);

        Assert.Equal(2, calls);
        Assert.Equal(2, results.Count);
        Assert.Contains(results, r => r.Id == "podchaser:pod-alpha");
        Assert.Contains(results, r => r.Id == "podchaser:pod-beta");
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

    private static int ExtractPage(string requestBody)
    {
        using var document = JsonDocument.Parse(requestBody);
        if (document.RootElement.TryGetProperty("variables", out var variablesNode) &&
            variablesNode.ValueKind == JsonValueKind.Object &&
            variablesNode.TryGetProperty("page", out var pageNode) &&
            pageNode.TryGetInt32(out var page))
        {
            return page;
        }

        return 1;
    }

    private static string ExtractSearchTerm(string requestBody)
    {
        using var document = JsonDocument.Parse(requestBody);
        if (document.RootElement.TryGetProperty("variables", out var variablesNode) &&
            variablesNode.ValueKind == JsonValueKind.Object &&
            variablesNode.TryGetProperty("searchTerm", out var termNode) &&
            termNode.ValueKind == JsonValueKind.String)
        {
            return termNode.GetString() ?? string.Empty;
        }

        return string.Empty;
    }

    private sealed class DelegateHttpMessageHandler(
        Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> handler) : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> _handler = handler;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            return _handler(request, cancellationToken);
        }
    }
}
