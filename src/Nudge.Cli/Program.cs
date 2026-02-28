using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Nudge.Cli.Models;
using Nudge.Cli.Services;
using Nudge.Core.Interfaces;
using Nudge.Core.Services;

var argumentsResult = CliArgumentParser.TryParse(args);
if (!argumentsResult.Success || argumentsResult.Payload is null)
{
    foreach (var error in argumentsResult.Errors)
    {
        Console.Error.WriteLine($"Argument error: {error.Message}");
    }

    PrintUsage();
    return 1;
}

var cliArgs = argumentsResult.Payload;
var cliUseMockResult = ParseCliUseMock(args);
if (!cliUseMockResult.Success)
{
    Console.Error.WriteLine($"Argument error: {cliUseMockResult.Error}");
    PrintUsage();
    return 1;
}

var optionsResult = BuildNudgeOptionsFromEnvironment();
if (!optionsResult.Success || optionsResult.Options is null)
{
    Console.Error.WriteLine($"Configuration error: {optionsResult.Error}");
    return 1;
}

var options = optionsResult.Options;
if (!WasPublishedAfterDaysProvided(args))
{
    cliArgs = cliArgs with { PublishedAfterDays = options.PublishedAfterDays };
}

var envUseMockRaw = Environment.GetEnvironmentVariable("NUDGE_USE_MOCK");
var envUseMock = PodcastSearchClientModeResolver.TryParseUseMockValue(envUseMockRaw);
if (envUseMockRaw is not null && !envUseMock.HasValue)
{
    Console.Error.WriteLine("Configuration error: NUDGE_USE_MOCK must be true/false or 1/0.");
    return 1;
}

var mode = PodcastSearchClientModeResolver.ResolveUseMock(
    cliUseMockResult.UseMock,
    envUseMock,
    options.ApiKey);
if (mode.MissingApiKeyWarning)
{
    Console.Error.WriteLine("Warning: NUDGE_PODCAST_API_KEY is missing; falling back to mock podcast search client.");
}

var services = ConfigureServices(options, mode.UseMock);
var pipeline = services.GetRequiredService<PodcastRankingPipeline>();
var run = await pipeline.RunAsync(cliArgs);
var limitedResults = RankedTargetSelection.SelectTop(run.Results, cliArgs.Top);

foreach (var warning in run.Warnings)
{
    Console.Error.WriteLine($"Warning: {warning}");
}

Console.WriteLine($"Warnings: {run.Warnings.Count}");
RankedTargetTableRenderer.Write(limitedResults, Console.Out);

if (cliArgs.JsonOutput)
{
    var payload = new JsonOutputEnvelope
    {
        GeneratedAtUtc = services.GetRequiredService<TimeProvider>().GetUtcNow(),
        Arguments = cliArgs,
        Total = limitedResults.Count,
        Results = limitedResults,
        Warnings = run.Warnings
    };

    var json = JsonSerializer.Serialize(
        payload,
        new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            WriteIndented = cliArgs.PrettyJson
        });
    Console.WriteLine(json);
}

return 0;

static ServiceProvider ConfigureServices(NudgeOptions options, bool useMock)
{
    var serviceCollection = new ServiceCollection();
    serviceCollection.AddSingleton<TimeProvider>(TimeProvider.System);
    serviceCollection.AddSingleton(options);
    serviceCollection.AddSingleton<IScoringService, ScoringService>();
    serviceCollection.AddSingleton<IRssParser, RssParser>();
    serviceCollection.AddSingleton<MockPodcastSearchClient>();
    serviceCollection.AddHttpClient<ListenNotesPodcastSearchClient>(
            (provider, client) =>
            {
                var opts = provider.GetRequiredService<NudgeOptions>();
                client.BaseAddress = ParseAbsoluteUriOrThrow(opts.BaseUrl);
                client.Timeout = TimeSpan.FromSeconds(10);
            });
    serviceCollection.AddSingleton<IPodcastSearchClient>(
        provider => useMock
            ? provider.GetRequiredService<MockPodcastSearchClient>()
            : provider.GetRequiredService<ListenNotesPodcastSearchClient>());
    serviceCollection.AddHttpClient("rss", client =>
    {
        client.Timeout = TimeSpan.FromSeconds(10);
    });
    serviceCollection.AddSingleton<IRssFeedClient>(provider =>
    {
        if (useMock)
        {
            return new RetryingRssFeedClient(new InMemoryRssFeedClient(MockPodcastSearchClient.SeededFeeds));
        }

        var factory = provider.GetRequiredService<IHttpClientFactory>();
        return new RetryingRssFeedClient(new HttpRssFeedClient(factory.CreateClient("rss")));
    });
    serviceCollection.AddSingleton<PodcastRankingPipeline>();
    return serviceCollection.BuildServiceProvider();
}

static void PrintUsage()
{
    Console.WriteLine("Usage:");
    Console.WriteLine("  Nudge.Cli --keywords \"ai,startups\" --published-after-days 60 [--top 10] [--json] [--pretty] [--use-mock]");
    Console.WriteLine("  Nudge.Cli \"ai,startups\" 30");
}

static (bool Success, bool UseMock, string? Error) ParseCliUseMock(IReadOnlyList<string> args)
{
    for (var i = 0; i < args.Count; i++)
    {
        var arg = args[i];
        if (!arg.Equals("--use-mock", StringComparison.OrdinalIgnoreCase))
        {
            continue;
        }

        if (i + 1 >= args.Count || args[i + 1].StartsWith("--", StringComparison.Ordinal))
        {
            return (true, true, null);
        }

        var parsed = PodcastSearchClientModeResolver.TryParseUseMockValue(args[i + 1]);
        if (!parsed.HasValue)
        {
            return (false, false, "Invalid value for --use-mock. Use true/false or 1/0.");
        }

        return (true, parsed.Value, null);
    }

    return (true, false, null);
}

static (bool Success, NudgeOptions? Options, string? Error) BuildNudgeOptionsFromEnvironment()
{
    var baseUrl = Environment.GetEnvironmentVariable("NUDGE_PODCAST_API_BASEURL");
    var publishedAfterDaysRaw = Environment.GetEnvironmentVariable("NUDGE_PODCAST_PUBLISHED_AFTER_DAYS");
    var publishedAfterDays = 60;

    if (!string.IsNullOrWhiteSpace(publishedAfterDaysRaw) &&
        (!int.TryParse(publishedAfterDaysRaw, out publishedAfterDays) || publishedAfterDays < 0))
    {
        return (false, null, "NUDGE_PODCAST_PUBLISHED_AFTER_DAYS must be a non-negative integer.");
    }

    var options = new NudgeOptions
    {
        ApiKey = Environment.GetEnvironmentVariable("NUDGE_PODCAST_API_KEY"),
        BaseUrl = string.IsNullOrWhiteSpace(baseUrl) ? NudgeOptions.DefaultBaseUrl : baseUrl,
        PublishedAfterDays = publishedAfterDays
    };

    if (!Uri.TryCreate(options.BaseUrl, UriKind.Absolute, out _))
    {
        return (false, null, "NUDGE_PODCAST_API_BASEURL must be a valid absolute URL.");
    }

    return (true, options, null);
}

static bool WasPublishedAfterDaysProvided(IReadOnlyList<string> args)
{
    if (args.Any(a => a.Equals("--published-after-days", StringComparison.OrdinalIgnoreCase)))
    {
        return true;
    }

    var positionalCount = 0;
    for (var i = 0; i < args.Count; i++)
    {
        if (args[i].StartsWith("--", StringComparison.Ordinal))
        {
            if (args[i].Equals("--json", StringComparison.OrdinalIgnoreCase) ||
                args[i].Equals("--pretty", StringComparison.OrdinalIgnoreCase) ||
                args[i].Equals("--use-mock", StringComparison.OrdinalIgnoreCase))
            {
                if (args[i].Equals("--use-mock", StringComparison.OrdinalIgnoreCase) &&
                    i + 1 < args.Count &&
                    !args[i + 1].StartsWith("--", StringComparison.Ordinal))
                {
                    i++;
                }

                continue;
            }

            i++;
            continue;
        }

        positionalCount++;
        if (positionalCount >= 2)
        {
            return true;
        }
    }

    return false;
}

static Uri ParseAbsoluteUriOrThrow(string url)
{
    if (Uri.TryCreate(url, UriKind.Absolute, out var uri))
    {
        return uri;
    }

    throw new InvalidOperationException("NUDGE_PODCAST_API_BASEURL must be a valid absolute URL.");
}
