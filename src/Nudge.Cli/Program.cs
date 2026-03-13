using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
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

var verboseDiagnostics = ParseVerboseDiagnostics(args);
var configuration = BuildConfiguration();
var podchaserOptions = BuildPodchaserOptions(configuration);
var podchaserBaseUrlResult = ResolvePodchaserBaseUrl();
if (!podchaserBaseUrlResult.Success)
{
    Console.Error.WriteLine($"Configuration error: {podchaserBaseUrlResult.Error}");
    return 1;
}

var tokenMemory = new PodchaserTokenMemory();
var tokenSelector = new PodchaserTokenSelector(new HttpClient { Timeout = TimeSpan.FromSeconds(20) });
var candidateTokens = await tokenSelector.OrderByRemainingQuotaAsync(
    podchaserBaseUrlResult.BaseUrl!,
    PodchaserTokenResolver.ResolveLabeled(podchaserOptions));
var selectedToken = candidateTokens.FirstOrDefault();
if (verboseDiagnostics)
{
    LogPodchaserConfigurationStatus(podchaserOptions);
}

var optionsResult = BuildNudgeOptionsFromEnvironment(selectedToken?.Value);
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
    selectedToken?.Value);
// #region agent log
WriteDebugLog(
    hypothesisId: "H1_mode_token",
    location: "Program.cs:ResolveUseMock",
    message: "Resolved podcast search mode.",
    data: new
    {
        cliUseMock = cliUseMockResult.UseMock,
        envUseMockRaw,
        envUseMockParsed = envUseMock,
        resolvedUseMock = mode.UseMock,
        missingApiKeyWarning = mode.MissingApiKeyWarning,
        selectedTokenLength = selectedToken?.Value.Length ?? 0,
        selectedTokenDotCount = selectedToken?.Value.Count(c => c == '.') ?? 0,
        searchTermsCount = cliArgs.SearchTerms.Count,
        keywordsCount = cliArgs.Keywords.Count
    },
    runId: "initial");
// #endregion
if (mode.MissingApiKeyWarning)
{
    Console.Error.WriteLine("Warning: Podchaser token missing or invalid; falling back to mock podcast search client.");
}

var services = ConfigureServices(options, mode.UseMock, configuration, selectedToken?.Value, selectedToken?.Label);
var pipeline = services.GetRequiredService<PodcastRankingPipeline>();
// #region agent log
WriteDebugLog(
    hypothesisId: "H1_mode_token",
    location: "Program.cs:BeforePipelineRun",
    message: "Prepared pipeline invocation.",
    data: new
    {
        modeUseMock = mode.UseMock,
        apiBaseUrl = options.BaseUrl,
        publishedAfterDays = cliArgs.PublishedAfterDays,
        top = cliArgs.Top,
        minReach = cliArgs.MinReach,
        maxReach = cliArgs.MaxReach
    },
    runId: "initial");
// #endregion
var run = await pipeline.RunAsync(cliArgs, includeDebugDiagnostics: verboseDiagnostics);
if (!mode.UseMock)
{
    EmitPodchaserSearchSummary(
        services.GetService<ListenNotesPodcastSearchClient>(),
        selectedToken?.Label ?? "primary");
}

string? successfulApiToken = null;
var exhaustedAllApiTokens = false;
if (!mode.UseMock && run.Results.Count == 0)
{
    var liveClient = services.GetService<ListenNotesPodcastSearchClient>();
    var tokenFailed = DidPodchaserTokenFail(liveClient);
    if (tokenFailed)
    {
        var failedAttemptLabel = selectedToken?.Label ?? "primary";
        foreach (var fallbackToken in candidateTokens.Skip(1))
        {
            Console.Error.WriteLine(
                $"Warning: Podchaser token attempt [{failedAttemptLabel}] failed; retrying with [{fallbackToken.Label}].");

            using var fallbackTokenServices = ConfigureServices(options, useMock: false, configuration, fallbackToken.Value, fallbackToken.Label);
            var fallbackPipeline = fallbackTokenServices.GetRequiredService<PodcastRankingPipeline>();
            var fallbackRun = await fallbackPipeline.RunAsync(cliArgs, includeDebugDiagnostics: verboseDiagnostics);
            var fallbackClient = fallbackTokenServices.GetService<ListenNotesPodcastSearchClient>();
            EmitPodchaserSearchSummary(fallbackClient, fallbackToken.Label);
            var fallbackTokenFailed = DidPodchaserTokenFail(fallbackClient);

            run = new RankingRunResult
            {
                Results = fallbackRun.Results,
                Warnings = run.Warnings
                    .Concat(fallbackRun.Warnings)
                    .Distinct(StringComparer.Ordinal)
                    .OrderBy(w => w, StringComparer.OrdinalIgnoreCase)
                    .ToArray(),
                Diagnostics = run.Diagnostics
                    .Concat(fallbackRun.Diagnostics)
                    .ToArray()
            };

            if (!fallbackTokenFailed)
            {
                successfulApiToken = fallbackToken.Value;
                break;
            }

            failedAttemptLabel = fallbackToken.Label;
        }

        exhaustedAllApiTokens = run.Results.Count == 0;
    }
    else if (!string.IsNullOrWhiteSpace(selectedToken?.Value))
    {
        successfulApiToken = selectedToken!.Value;
    }
}
else if (!mode.UseMock &&
         !DidPodchaserTokenFail(services.GetService<ListenNotesPodcastSearchClient>()) &&
         !string.IsNullOrWhiteSpace(selectedToken?.Value))
{
    successfulApiToken = selectedToken!.Value;
}

if (!mode.UseMock &&
    exhaustedAllApiTokens &&
    run.Results.Count == 0)
{
    Console.Error.WriteLine("Warning: All Podchaser tokens were exhausted or rejected; retrying with mock podcast search client.");
    using var fallbackServices = ConfigureServices(options, useMock: true, configuration);
    var fallbackPipeline = fallbackServices.GetRequiredService<PodcastRankingPipeline>();
    var fallbackRun = await fallbackPipeline.RunAsync(cliArgs, includeDebugDiagnostics: verboseDiagnostics);
    run = new RankingRunResult
    {
        Results = fallbackRun.Results,
        Warnings = run.Warnings
            .Concat(fallbackRun.Warnings)
            .Append("All Podchaser tokens failed; returned fallback mock search results.")
            .Distinct(StringComparer.Ordinal)
            .OrderBy(w => w, StringComparer.OrdinalIgnoreCase)
            .ToArray(),
        Diagnostics = run.Diagnostics
            .Concat(fallbackRun.Diagnostics)
            .Append("Auto-fallback: switched to mock search after Podchaser token failure.")
            .ToArray()
    };
}

if (!string.IsNullOrWhiteSpace(successfulApiToken))
{
    tokenMemory.RememberToken(successfulApiToken);
}
var runWarnings = run.Warnings.ToList();
var (reachFilteredResults, filteredOutByReach) = RankedTargetSelection.FilterByReach(run.Results, cliArgs.MinReach, cliArgs.MaxReach);
if (filteredOutByReach > 0)
{
    var minLabel = cliArgs.MinReach.HasValue ? cliArgs.MinReach.Value.ToString("0.###") : "0.0";
    var maxLabel = cliArgs.MaxReach.HasValue ? cliArgs.MaxReach.Value.ToString("0.###") : "1.0";
    runWarnings.Add($"Reach filter [{minLabel}, {maxLabel}] removed {filteredOutByReach} show(s).");
}
var limitedResults = RankedTargetSelection.SelectTop(reachFilteredResults, cliArgs.Top);

foreach (var warning in runWarnings)
{
    Console.Error.WriteLine($"Warning: {warning}");
}

if (verboseDiagnostics)
{
    foreach (var diagnostic in run.Diagnostics)
    {
        Console.Error.WriteLine($"Debug: {diagnostic}");
    }
}

RankedTargetTableRenderer.Write(limitedResults, Console.Out);

if (cliArgs.JsonOutput)
{
    var payload = new JsonOutputEnvelope
    {
        GeneratedAtUtc = services.GetRequiredService<TimeProvider>().GetUtcNow(),
        Arguments = cliArgs,
        Total = limitedResults.Count,
        Results = limitedResults,
        Warnings = runWarnings
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

static ServiceProvider ConfigureServices(
    NudgeOptions options,
    bool useMock,
    IConfiguration configuration,
    string? apiTokenOverride = null,
    string? tokenScope = null)
{
    var serviceCollection = new ServiceCollection();
    serviceCollection.AddOptions();
    serviceCollection.Configure<PodchaserOptions>(configuration.GetSection("Podchaser"));
    serviceCollection.AddSingleton<TimeProvider>(TimeProvider.System);
    serviceCollection.AddSingleton(options);
    serviceCollection.AddSingleton<PodchaserSearchCache>();
    serviceCollection.AddSingleton<IScoringService, ScoringService>();
    serviceCollection.AddSingleton<IRssParser, RssParser>();
    serviceCollection.AddSingleton<IHostTranscriptLineExtractor, HostTranscriptLineExtractor>();
    serviceCollection.AddSingleton<IEpisodeTranscriptService, EpisodeTranscriptService>();
    serviceCollection.AddSingleton<IEpisodeSttTranscriber, NoOpEpisodeSttTranscriber>();
    serviceCollection.AddSingleton<MockPodcastSearchClient>();
    serviceCollection.AddSingleton<ListenNotesPodcastSearchClient>(provider =>
        new ListenNotesPodcastSearchClient(
            provider.GetRequiredService<IHttpClientFactory>().CreateClient("podcast-search"),
            provider.GetRequiredService<NudgeOptions>() with
            {
                ApiKey = string.IsNullOrWhiteSpace(apiTokenOverride)
                    ? PodchaserTokenResolver.Resolve(provider.GetRequiredService<IOptions<PodchaserOptions>>().Value).FirstOrDefault()
                    : apiTokenOverride.Trim()
            },
            provider.GetRequiredService<PodchaserSearchCache>(),
            tokenScope));
    serviceCollection.AddHttpClient("podcast-search", (provider, client) =>
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
    serviceCollection.AddSingleton<ITranscriptContentClient>(provider =>
    {
        var factory = provider.GetRequiredService<IHttpClientFactory>();
        return new HttpTranscriptContentClient(factory.CreateClient("rss"));
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
    Console.WriteLine("  Nudge.Cli --search-terms \"longevity\" --keywords \"ai,startups\" --published-after-days 60 [--top 3] [--min-reach 0.2] [--max-reach 0.9] [--json] [--pretty] [--use-mock] [--verbose]");
    Console.WriteLine("  Nudge.Cli --search-terms \"ai\" --keywords \"ai,startups\" 30");
}

static bool ParseVerboseDiagnostics(IReadOnlyList<string> args)
{
    return args.Any(a => a.Equals("--verbose", StringComparison.OrdinalIgnoreCase));
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

static IConfigurationRoot BuildConfiguration()
{
    var builder = new ConfigurationBuilder();
    builder.SetBasePath(Directory.GetCurrentDirectory());

    return builder
        .AddJsonFile("nudge.local.json", optional: true, reloadOnChange: false)
        .AddEnvironmentVariables()
        .Build();
}

static PodchaserOptions BuildPodchaserOptions(IConfiguration configuration)
{
    var options = new PodchaserOptions();
    configuration.GetSection("Podchaser").Bind(options);
    return options;
}

static void LogPodchaserConfigurationStatus(PodchaserOptions options)
{
    var configPath = Path.Combine(Directory.GetCurrentDirectory(), "nudge.local.json");
    var configFound = File.Exists(configPath);
    var hasToken = !string.IsNullOrWhiteSpace(options.Token);
    var fallbackCount = options.FallbackTokens?.Count(token => !string.IsNullOrWhiteSpace(token)) ?? 0;

    Console.Error.WriteLine(
        $"Debug: nudge.local.json found={configFound}; Podchaser token detected={hasToken}; fallback tokens detected={fallbackCount}.");
}

static (bool Success, NudgeOptions? Options, string? Error) BuildNudgeOptionsFromEnvironment(string? apiKey)
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
        ApiKey = apiKey,
        BaseUrl = string.IsNullOrWhiteSpace(baseUrl) ? NudgeOptions.DefaultBaseUrl : baseUrl,
        PublishedAfterDays = publishedAfterDays
    };

    if (!Uri.TryCreate(options.BaseUrl, UriKind.Absolute, out _))
    {
        return (false, null, "NUDGE_PODCAST_API_BASEURL must be a valid absolute URL.");
    }

    return (true, options, null);
}

static (bool Success, string? BaseUrl, string? Error) ResolvePodchaserBaseUrl()
{
    var baseUrl = Environment.GetEnvironmentVariable("NUDGE_PODCAST_API_BASEURL");
    var resolved = string.IsNullOrWhiteSpace(baseUrl) ? NudgeOptions.DefaultBaseUrl : baseUrl.Trim();
    if (!Uri.TryCreate(resolved, UriKind.Absolute, out _))
    {
        return (false, null, "NUDGE_PODCAST_API_BASEURL must be a valid absolute URL.");
    }

    return (true, resolved, null);
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
                args[i].Equals("--use-mock", StringComparison.OrdinalIgnoreCase) ||
                args[i].Equals("--verbose", StringComparison.OrdinalIgnoreCase))
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

static bool DidPodchaserTokenFail(ListenNotesPodcastSearchClient? client)
{
    return client is { WasPointBudgetExceeded: true } || client is { WasTokenRejected: true };
}

static void EmitPodchaserSearchSummary(ListenNotesPodcastSearchClient? client, string attemptLabel)
{
    if (client is null)
    {
        return;
    }

    var diagnostics = client.LastSearchDiagnostics;
    if (!diagnostics.Executed)
    {
        return;
    }

    Console.Error.WriteLine(
        $"Info: Podchaser search [{attemptLabel}] keywords={diagnostics.KeywordCount}, targetTop={diagnostics.TargetResultCount}, candidateBudget={diagnostics.TargetCandidateCount}, requests={diagnostics.HttpRequestsSent}, pages={diagnostics.SuccessfulPageCount}, rawCandidates={diagnostics.RawCandidatesReturned}, cache={(diagnostics.CacheHit ? "hit" : "miss")}, legacyFallback={(diagnostics.LegacyFallbackTriggered ? "yes" : "no")}, lowCostRetry={(diagnostics.ReducedPageSizeTriggered ? "yes" : "no")}, earlyExit={(diagnostics.EarlyExitTriggered ? "yes" : "no")}.");
}

static void WriteDebugLog(string hypothesisId, string location, string message, object data, string runId)
{
    try
    {
        var entry = new
        {
            sessionId = "8d2ec3",
            runId,
            hypothesisId,
            location,
            message,
            data,
            timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()
        };
        File.AppendAllText("debug-8d2ec3.log", JsonSerializer.Serialize(entry) + Environment.NewLine);
    }
    catch
    {
        // Debug logging should never break CLI execution.
    }
}
