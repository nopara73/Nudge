using System.Net;
using System.Net.Http;
using Nudge.Ui.Models;
using Nudge.Ui.Services;

namespace Nudge.Tests;

public sealed class EpisodeTranscriptAcquisitionServiceTests
{
    [Fact]
    public async Task AcquireAsync_ReturnsFullTranscript_WhenInlineTranscriptExists()
    {
        var service = new EpisodeTranscriptAcquisitionService(
            new HttpClient(new StubHttpMessageHandler(_ => throw new InvalidOperationException())),
            new StubEpisodeSttTranscriber(null));
        var episode = new QueueEpisode
        {
            Title = "Episode 1",
            Transcript = "Host: Welcome everyone."
        };

        var result = await service.AcquireAsync(episode, ["Host"], feedUrl: null, hostOnly: false);

        Assert.NotNull(result);
        Assert.Equal("Host: Welcome everyone.", result!.Body);
    }

    [Fact]
    public async Task AcquireAsync_ReturnsHostOnlyLines_WhenRequested()
    {
        var service = new EpisodeTranscriptAcquisitionService(
            new HttpClient(new StubHttpMessageHandler(_ => throw new InvalidOperationException())),
            new StubEpisodeSttTranscriber(null));
        var episode = new QueueEpisode
        {
            Title = "Episode 1",
            Transcript = """
                         Host One: Welcome everyone.
                         Guest: Thanks for joining.
                         [Host] Let's begin.
                         """
        };

        var result = await service.AcquireAsync(episode, ["Host One"], feedUrl: null, hostOnly: true);

        Assert.NotNull(result);
        Assert.Equal("Welcome everyone." + Environment.NewLine + Environment.NewLine + "Let's begin.", result!.Body);
    }

    [Fact]
    public async Task AcquireAsync_DownloadsTranscript_WhenOnlyTranscriptUrlExists()
    {
        var handler = new StubHttpMessageHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("Downloaded transcript")
        });
        var service = new EpisodeTranscriptAcquisitionService(new HttpClient(handler), new StubEpisodeSttTranscriber(null));
        var episode = new QueueEpisode
        {
            Title = "Episode 1",
            TranscriptUrl = "https://example.com/transcript.txt"
        };

        var result = await service.AcquireAsync(episode, ["Host"], feedUrl: null, hostOnly: false);

        Assert.NotNull(result);
        Assert.Equal("Downloaded transcript", result!.Body);
    }

    [Fact]
    public async Task AcquireAsync_DiscoversTranscriptFromFeed_WhenEpisodeHasNoTranscriptUrl()
    {
        const string feedUrl = "https://example.com/feed.xml";
        const string transcriptUrl = "https://example.com/transcript.txt";
        var handler = new StubHttpMessageHandler(request =>
        {
            if (request.RequestUri?.ToString() == feedUrl)
            {
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(
                        """
                        <rss version="2.0" xmlns:podcast="https://podcastindex.org/namespace/1.0">
                          <channel>
                            <item>
                              <title>Episode 1</title>
                              <link>https://example.com/ep-1</link>
                              <podcast:transcript url="https://example.com/transcript.txt" type="text/plain" />
                            </item>
                          </channel>
                        </rss>
                        """)
                };
            }

            if (request.RequestUri?.ToString() == transcriptUrl)
            {
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent("Discovered transcript")
                };
            }

            return new HttpResponseMessage(HttpStatusCode.NotFound);
        });
        var service = new EpisodeTranscriptAcquisitionService(new HttpClient(handler), new StubEpisodeSttTranscriber(null));
        var episode = new QueueEpisode
        {
            Title = "Episode 1",
            Url = "https://example.com/ep-1"
        };

        var result = await service.AcquireAsync(episode, ["Host"], feedUrl, hostOnly: false);

        Assert.NotNull(result);
        Assert.Equal("Discovered transcript", result!.Body);
    }

    [Fact]
    public async Task AcquireAsync_DiscoversTranscriptFromFeed_WhenFeedUsesGuidPermalink()
    {
        const string feedUrl = "https://example.com/feed.xml";
        const string transcriptUrl = "https://example.com/transcript.txt";
        var handler = new StubHttpMessageHandler(request =>
        {
            if (request.RequestUri?.ToString() == feedUrl)
            {
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(
                        """
                        <rss version="2.0" xmlns:podcast="https://podcastindex.org/namespace/1.0">
                          <channel>
                            <item>
                              <title>Episode 1</title>
                              <guid isPermaLink="true">https://example.com/ep-1</guid>
                              <podcast:transcript url="https://example.com/transcript.txt" type="text/plain" />
                            </item>
                          </channel>
                        </rss>
                        """)
                };
            }

            if (request.RequestUri?.ToString() == transcriptUrl)
            {
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent("Discovered transcript")
                };
            }

            return new HttpResponseMessage(HttpStatusCode.NotFound);
        });
        var service = new EpisodeTranscriptAcquisitionService(new HttpClient(handler), new StubEpisodeSttTranscriber(null));
        var episode = new QueueEpisode
        {
            Title = "Episode 1",
            Url = "https://example.com/ep-1"
        };

        var result = await service.AcquireAsync(episode, ["Host"], feedUrl, hostOnly: false);

        Assert.NotNull(result);
        Assert.Equal("Discovered transcript", result!.Body);
    }

    [Fact]
    public async Task AcquireAsync_UsesLocalStt_WhenNoPublishedTranscriptExists()
    {
        const string feedUrl = "https://example.com/feed.xml";
        var handler = new StubHttpMessageHandler(request =>
        {
            if (request.RequestUri?.ToString() == feedUrl)
            {
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(
                        """
                        <rss version="2.0">
                          <channel>
                            <item>
                              <title>Episode 1</title>
                              <link>https://example.com/ep-1</link>
                              <enclosure url="https://example.com/ep-1.mp3" type="audio/mpeg" />
                            </item>
                          </channel>
                        </rss>
                        """)
                };
            }

            return new HttpResponseMessage(HttpStatusCode.NotFound);
        });
        var service = new EpisodeTranscriptAcquisitionService(
            new HttpClient(handler),
            new StubEpisodeSttTranscriber("Local STT transcript"));
        var episode = new QueueEpisode
        {
            Title = "Episode 1",
            Url = "https://example.com/ep-1"
        };

        var result = await service.AcquireAsync(episode, ["Host"], feedUrl, hostOnly: false);

        Assert.NotNull(result);
        Assert.Equal("Local STT transcript", result!.Body);
    }

    [Fact]
    public async Task AcquireAsync_UsesFeedShowNotesFallback_WhenTranscriptMetadataMissing()
    {
        const string feedUrl = "https://example.com/feed.xml";
        var handler = new StubHttpMessageHandler(request =>
        {
            if (request.RequestUri?.ToString() == feedUrl)
            {
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(
                        """
                        <rss version="2.0" xmlns:content="http://purl.org/rss/1.0/modules/content/">
                          <channel>
                            <item>
                              <title>Episode 1</title>
                              <link>https://example.com/ep-1</link>
                              <content:encoded><![CDATA[
                                <p>Host A and Host B discuss practical programming strategies, review common mistakes,
                                and share step-by-step implementation examples that listeners can apply this week.</p>
                              ]]></content:encoded>
                            </item>
                          </channel>
                        </rss>
                        """)
                };
            }

            return new HttpResponseMessage(HttpStatusCode.NotFound);
        });
        var service = new EpisodeTranscriptAcquisitionService(
            new HttpClient(handler),
            new StubEpisodeSttTranscriber(null));
        var episode = new QueueEpisode
        {
            Title = "Episode 1",
            Url = "https://example.com/ep-1"
        };

        var result = await service.AcquireAsync(episode, ["Host"], feedUrl, hostOnly: false);

        Assert.NotNull(result);
        Assert.Contains("discuss practical programming strategies", result!.Body);
        Assert.Contains("step-by-step implementation examples", result.Body);
    }

    [Fact]
    public async Task AcquireAsync_UsesEpisodePageTranscriptLinkFallback_WhenFeedAndSttMiss()
    {
        const string episodeUrl = "https://example.com/ep-1";
        const string transcriptUrl = "https://example.com/ep-1/transcript.vtt";
        var handler = new StubHttpMessageHandler(request =>
        {
            if (request.RequestUri?.ToString() == episodeUrl)
            {
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(
                        """
                        <html>
                          <body>
                            <a href="/ep-1/transcript.vtt">Transcript</a>
                          </body>
                        </html>
                        """)
                };
            }

            if (request.RequestUri?.ToString() == transcriptUrl)
            {
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(
                        """
                        WEBVTT

                        00:00:01.000 --> 00:00:04.000
                        Host: Welcome back.

                        00:00:04.000 --> 00:00:07.000
                        Guest: Glad to be here.
                        """)
                };
            }

            return new HttpResponseMessage(HttpStatusCode.NotFound);
        });

        var service = new EpisodeTranscriptAcquisitionService(
            new HttpClient(handler),
            new StubEpisodeSttTranscriber(null));
        var episode = new QueueEpisode
        {
            Title = "Episode 1",
            Url = episodeUrl
        };

        var result = await service.AcquireAsync(episode, ["Host"], feedUrl: null, hostOnly: false);

        Assert.NotNull(result);
        Assert.Equal("Host: Welcome back. Guest: Glad to be here.", result!.Body);
    }

    [Fact]
    public async Task AcquireAsync_UsesEpisodePageTranscriptSectionFallback_WhenNoTranscriptLinksFound()
    {
        const string episodeUrl = "https://example.com/ep-2";
        var handler = new StubHttpMessageHandler(request =>
        {
            if (request.RequestUri?.ToString() == episodeUrl)
            {
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(
                        """
                        <html>
                          <body>
                            <h2>Transcript</h2>
                            <p>Host: Welcome everyone and thanks for listening today.</p>
                            <p>Guest: Thanks for having me on.</p>
                            <p>Host: We are diving into a practical, step-by-step approach for consistency and behavior change.</p>
                            <h2>Show Notes</h2>
                          </body>
                        </html>
                        """)
                };
            }

            return new HttpResponseMessage(HttpStatusCode.NotFound);
        });

        var service = new EpisodeTranscriptAcquisitionService(
            new HttpClient(handler),
            new StubEpisodeSttTranscriber(null));
        var episode = new QueueEpisode
        {
            Title = "Episode 2",
            Url = episodeUrl
        };

        var result = await service.AcquireAsync(episode, ["Host"], feedUrl: null, hostOnly: false);

        Assert.NotNull(result);
        Assert.Contains("Host: Welcome everyone", result!.Body);
        Assert.Contains("Guest: Thanks for having me on.", result.Body);
        Assert.DoesNotContain("Show Notes", result.Body);
    }

    [Fact]
    public async Task AcquireAsync_HostOnly_InferPrimarySpeaker_WhenHostMatchFails()
    {
        var service = new EpisodeTranscriptAcquisitionService(
            new HttpClient(new StubHttpMessageHandler(_ => throw new InvalidOperationException())),
            new StubEpisodeSttTranscriber(null));
        var episode = new QueueEpisode
        {
            Title = "Episode 1",
            Transcript = """
                         Speaker A: Intro line.
                         Speaker B: Guest response.
                         Speaker A: Main point.
                         """
        };

        var result = await service.AcquireAsync(episode, ["Unknown Host"], feedUrl: null, hostOnly: true);

        Assert.NotNull(result);
        Assert.Contains("Intro line.", result!.Body);
        Assert.Contains("Main point.", result.Body);
        Assert.DoesNotContain("Guest response.", result.Body);
    }

    [Fact]
    public async Task AcquireAsync_HostOnly_FallsBackToFullTranscript_WhenNoSpeakerLabels()
    {
        var service = new EpisodeTranscriptAcquisitionService(
            new HttpClient(new StubHttpMessageHandler(_ => throw new InvalidOperationException())),
            new StubEpisodeSttTranscriber(null));
        var episode = new QueueEpisode
        {
            Title = "Episode 1",
            Transcript = "This transcript has no explicit speaker labels."
        };

        var result = await service.AcquireAsync(episode, ["Host"], feedUrl: null, hostOnly: true);

        Assert.NotNull(result);
        Assert.Contains("Showing full transcript instead", result!.Body);
        Assert.Contains("This transcript has no explicit speaker labels.", result.Body);
    }

    [Fact]
    public async Task AcquireAsync_HostOnly_CapturesContinuationLines_AfterTimestampSpeakerLabels()
    {
        var service = new EpisodeTranscriptAcquisitionService(
            new HttpClient(new StubHttpMessageHandler(_ => throw new InvalidOperationException())),
            new StubEpisodeSttTranscriber(null));
        var episode = new QueueEpisode
        {
            Title = "Episode 1",
            Transcript = """
                         **Philip Pape:** 0:00
                         You probably heard this advice.
                         Guest: Thanks for having me.
                         Philip Pape: 1:05 Second host point.
                         """
        };

        var result = await service.AcquireAsync(episode, ["Philip Pape"], feedUrl: null, hostOnly: true);

        Assert.NotNull(result);
        Assert.Equal("You probably heard this advice." + Environment.NewLine + Environment.NewLine + "Second host point.", result!.Body);
    }

    [Fact]
    public async Task AcquireAsync_HostOnly_ParsesHtmlTranscriptSpeakerBlocks()
    {
        var service = new EpisodeTranscriptAcquisitionService(
            new HttpClient(new StubHttpMessageHandler(_ => throw new InvalidOperationException())),
            new StubEpisodeSttTranscriber(null));
        var episode = new QueueEpisode
        {
            Title = "Episode 1",
            Transcript = """
                         <cite class="speaker">Philip Pape:</cite>
                         <time datetime="PT0M0S">0:00</time>
                         <p class="segment">You probably heard this advice. Eat when you&#39;re hungry.</p>
                         <cite class="speaker">Guest:</cite>
                         <time datetime="PT0M3S">0:03</time>
                         <p class="segment">Thanks for having me.</p>
                         """
        };

        var result = await service.AcquireAsync(episode, ["Philip Pape"], feedUrl: null, hostOnly: true);

        Assert.NotNull(result);
        Assert.Equal("You probably heard this advice. Eat when you're hungry.", result!.Body);
    }

    private sealed class StubHttpMessageHandler(Func<HttpRequestMessage, HttpResponseMessage> responder) : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, HttpResponseMessage> _responder = responder;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            return Task.FromResult(_responder(request));
        }
    }

    private sealed class StubEpisodeSttTranscriber(string? transcript) : ILocalEpisodeSttTranscriber
    {
        private readonly string? _transcript = transcript;

        public Task<string?> TranscribeFromAudioUrlAsync(string audioUrl, CancellationToken cancellationToken = default)
        {
            return Task.FromResult(_transcript);
        }
    }
}
