namespace Nudge.Ui.Models;

public sealed class QueueItem
{
    public required string IdentityKey { get; init; }
    public required string ShowId { get; init; }
    public required string ShowName { get; init; }
    public string? ContactEmail { get; init; }
    public string? ManualContactEmail { get; init; }
    public required string EffectiveContactEmail { get; init; }
    public required string DetectedLanguage { get; init; }
    public string FeedUrl { get; init; } = string.Empty;
    public double Score { get; init; }
    public double Reach { get; init; }
    public double Frequency { get; init; }
    public double NicheFit { get; init; }
    public double ActivityScore { get; init; }
    public string OutreachPriority { get; init; } = "Low";
    public DateTimeOffset? NewestEpisodePublishedAtUtc { get; init; }
    public IReadOnlyList<QueueEpisode> RecentEpisodes { get; init; } = Array.Empty<QueueEpisode>();
    public IReadOnlyList<string> RecentEpisodeTitles => RecentEpisodes.Select(episode => episode.Title).ToArray();
    public string NicheFitBreakdownJson { get; init; } = string.Empty;
    public OutreachState State { get; init; } = OutreachState.New;
    public DateTimeOffset? CooldownUntilUtc { get; init; }
    public DateTimeOffset? SnoozeUntilUtc { get; init; }
    public DateTimeOffset? ContactedAtUtc { get; init; }
    public string Tags { get; init; } = string.Empty;
    public string Note { get; init; } = string.Empty;

    public string StateDisplayLabel => State switch
    {
        OutreachState.New => "New",
        OutreachState.ContactedWaiting => "Contacted",
        OutreachState.Snoozed => "Snoozed",
        OutreachState.RepliedYes => "Replied YES",
        OutreachState.RepliedNo => "Replied NO",
        OutreachState.Dismissed => "Dismissed",
        OutreachState.InterviewDone => "Interview done",
        _ => State.ToString()
    };

    public string WaitingUntilDisplay
    {
        get
        {
            if (State == OutreachState.Snoozed && SnoozeUntilUtc is not null)
            {
                return $"Snoozed for {FormatRemainingDuration(SnoozeUntilUtc.Value)}";
            }

            if (State == OutreachState.ContactedWaiting && CooldownUntilUtc is not null)
            {
                return $"Cooldown for {FormatRemainingDuration(CooldownUntilUtc.Value)}";
            }

            if (State == OutreachState.RepliedYes && CooldownUntilUtc is not null)
            {
                return $"Follow-up in {FormatRemainingDuration(CooldownUntilUtc.Value)}";
            }

            return string.Empty;
        }
    }

    public bool HasWaitingUntilDisplay => !string.IsNullOrWhiteSpace(WaitingUntilDisplay);

    private static string FormatRemainingDuration(DateTimeOffset targetUtc)
    {
        var nowLocal = DateTimeOffset.Now;
        var targetLocal = targetUtc.ToLocalTime();

        if (targetLocal <= nowLocal)
        {
            return "0 days";
        }

        var years = GetWholeYears(nowLocal, targetLocal);
        if (years >= 1)
        {
            return FormatUnit(years, "year");
        }

        var months = GetWholeMonths(nowLocal, targetLocal);
        if (months >= 1)
        {
            return FormatUnit(months, "month");
        }

        var remaining = targetLocal - nowLocal;
        var weeks = (int)Math.Floor(remaining.TotalDays / 7);
        if (weeks >= 1)
        {
            return FormatUnit(weeks, "week");
        }

        var days = (int)Math.Floor(remaining.TotalDays);
        if (days >= 1)
        {
            return FormatUnit(days, "day");
        }

        var hours = (int)Math.Floor(remaining.TotalHours);
        if (hours >= 1)
        {
            return FormatUnit(hours, "hour");
        }

        var minutes = (int)Math.Floor(remaining.TotalMinutes);
        return FormatUnit(Math.Max(minutes, 1), "minute");
    }

    private static int GetWholeYears(DateTimeOffset start, DateTimeOffset end)
    {
        var years = end.Year - start.Year;
        if (start.AddYears(years) > end)
        {
            years--;
        }

        return Math.Max(years, 0);
    }

    private static int GetWholeMonths(DateTimeOffset start, DateTimeOffset end)
    {
        var months = ((end.Year - start.Year) * 12) + (end.Month - start.Month);
        if (start.AddMonths(months) > end)
        {
            months--;
        }

        return Math.Max(months, 0);
    }

    private static string FormatUnit(int value, string unit)
    {
        return value == 1 ? $"1 {unit}" : $"{value} {unit}s";
    }
}
