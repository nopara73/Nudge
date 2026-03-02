using Nudge.Cli.Services;

namespace Nudge.Tests;

public sealed class HostTranscriptLineExtractorTests
{
    [Fact]
    public void ExtractHostLines_ReturnsOnlyHostLines_ForLabeledTranscript()
    {
        var extractor = new HostTranscriptLineExtractor();
        var transcript =
            """
            Jane Doe: Welcome back everyone.
            Guest: Thanks for having me.
            JOHN ROE - Today we are talking training blocks.
            [Jane] First, consistency matters.
            Moderator: Let's move on.
            """;

        var lines = extractor.ExtractHostLines(transcript, ["Jane Doe", "John Roe"]);

        Assert.Equal(
        [
            "Welcome back everyone.",
            "Today we are talking training blocks.",
            "First, consistency matters."
        ], lines);
    }

    [Fact]
    public void ExtractHostLines_ReturnsEmpty_WhenHostsOrTranscriptMissing()
    {
        var extractor = new HostTranscriptLineExtractor();

        Assert.Empty(extractor.ExtractHostLines(null, ["Jane Doe"]));
        Assert.Empty(extractor.ExtractHostLines("Jane Doe: Hi", Array.Empty<string>()));
    }

    [Fact]
    public void ExtractHostLines_CapturesContinuationLines_AfterTimestampMarkers()
    {
        var extractor = new HostTranscriptLineExtractor();
        var transcript =
            """
            **Philip Pape:** 0:00
            You probably heard this advice.
            Guest: Thanks for having me.
            Philip Pape: 1:05 Second host point.
            """;

        var lines = extractor.ExtractHostLines(transcript, ["Philip Pape"]);

        Assert.Equal(
        [
            "You probably heard this advice.",
            "Second host point."
        ], lines);
    }

    [Fact]
    public void ExtractHostLines_ParsesHtmlSpeakerBlocks_AndDecodesEntities()
    {
        var extractor = new HostTranscriptLineExtractor();
        var transcript =
            """
            <cite>Philip Pape:</cite>
            <time>0:00</time>
            <p>You probably heard this advice. Eat when you&#39;re hungry.</p>
            <cite>Guest:</cite>
            <time>0:03</time>
            <p>Thanks for having me.</p>
            """;

        var lines = extractor.ExtractHostLines(transcript, ["Philip Pape"]);

        Assert.Equal(
        [
            "You probably heard this advice. Eat when you're hungry."
        ], lines);
    }

    [Fact]
    public void ExtractHostLines_ParsesHtmlSpeakerBlocks_WithAttributes_AndEncodedTags()
    {
        var extractor = new HostTranscriptLineExtractor();
        var transcript =
            """
            &lt;cite class="speaker"&gt;Philip Pape:&lt;/cite&gt;
            &lt;time datetime="PT0M0S"&gt;0:00&lt;/time&gt;
            &lt;p class="segment"&gt;Host line one.&lt;/p&gt;
            &lt;cite class="speaker"&gt;Guest:&lt;/cite&gt;
            &lt;time datetime="PT0M3S"&gt;0:03&lt;/time&gt;
            &lt;p class="segment"&gt;Guest line.&lt;/p&gt;
            """;

        var lines = extractor.ExtractHostLines(transcript, ["Philip Pape"]);

        Assert.Equal(["Host line one."], lines);
    }

    [Fact]
    public void ExtractHostLines_ParsesHtmlSpeakerSections_WhenMetadataTagsExistBetweenSpeakerAndParagraph()
    {
        var extractor = new HostTranscriptLineExtractor();
        var transcript =
            """
            <cite>Philip Pape:</cite><time>0:00</time><div class="meta">chapter 1</div><p>First host line.</p>
            <cite>Guest:</cite><time>0:03</time><div class="meta">chapter 1</div><p>Guest line.</p>
            """;

        var lines = extractor.ExtractHostLines(transcript, ["Philip Pape"]);

        Assert.Equal(["First host line."], lines);
    }
}
