using System.Globalization;
using Nudge.Cli.Models;

namespace Nudge.Cli.Services;

public static class RankedTargetTableRenderer
{
    public static void Write(IReadOnlyList<RankedTarget> results, TextWriter writer)
    {
        if (results.Count == 0)
        {
            writer.WriteLine("No ranked targets found.");
            return;
        }

        writer.WriteLine("Rank | Show                          | Lang | Score | Reach | Frequency | NicheFit | Activity | Priority | NewestEp   | Contact");
        writer.WriteLine("-----+-------------------------------+------+-------+-------+-----------+----------+----------+----------+------------+----------------------------");
        for (var i = 0; i < results.Count; i++)
        {
            var r = results[i];
            var newestEpisode = r.NewestEpisodePublishedAtUtc?.UtcDateTime.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) ?? "-";
            writer.WriteLine(
                $"{(i + 1),4} | {TrimTo(r.ShowName, 29),-29} | {r.DetectedLanguage,-4} | {FormatDecimal(r.Score),5} | {FormatDecimal(r.Reach),5} | {FormatDecimal(r.Frequency),9} | {FormatDecimal(r.NicheFit),8} | {FormatDecimal(r.ActivityScore),8} | {r.OutreachPriority,-8} | {newestEpisode,-10} | {TrimTo(r.ContactEmail ?? "-", 26)}");
        }
    }

    private static string FormatDecimal(double value)
    {
        return value.ToString("F3", CultureInfo.InvariantCulture);
    }

    private static string TrimTo(string input, int max)
    {
        if (string.IsNullOrEmpty(input) || input.Length <= max)
        {
            return input;
        }

        return input[..(max - 3)] + "...";
    }
}
