using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Nudge.Cli.Models;
using Nudge.Cli.Services;
using Nudge.Core.Interfaces;
using Nudge.Core.Services;

var services = ConfigureServices();
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

static ServiceProvider ConfigureServices()
{
    var serviceCollection = new ServiceCollection();
    serviceCollection.AddSingleton<TimeProvider>(TimeProvider.System);
    serviceCollection.AddSingleton<IScoringService, ScoringService>();
    serviceCollection.AddSingleton<IRssParser, RssParser>();
    serviceCollection.AddSingleton<IPodcastSearchClient, MockPodcastSearchClient>();
    serviceCollection.AddSingleton<IRssFeedClient>(
        _ => new RetryingRssFeedClient(new InMemoryRssFeedClient(MockPodcastSearchClient.SeededFeeds)));
    serviceCollection.AddSingleton<PodcastRankingPipeline>();
    return serviceCollection.BuildServiceProvider();
}

static void PrintUsage()
{
    Console.WriteLine("Usage:");
    Console.WriteLine("  Nudge.Cli --keywords \"ai,startups\" --published-after-days 30 [--top 10] [--json] [--pretty]");
    Console.WriteLine("  Nudge.Cli \"ai,startups\" 30");
}
