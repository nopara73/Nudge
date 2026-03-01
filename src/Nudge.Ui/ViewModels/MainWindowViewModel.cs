using System.Collections.ObjectModel;
using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Nudge.Ui.Models;
using Nudge.Ui.Services;

namespace Nudge.Ui.ViewModels;

public partial class MainWindowViewModel : ViewModelBase
{
    private const int DefaultPublishedAfterDays = 60;
    private const int DefaultTop = 30;

    private static readonly string[] DefaultRunKeywords =
    [
        "masters athlete",
        "aging athlete",
        "over 40 fitness",
        "over 50 fitness",
        "longevity performance",
        "strength after 40",
        "hyrox",
        "crossfit masters",
        "vo2 max aging"
    ];

    private readonly CliRunnerService _cliRunner;
    private readonly OutreachRepository _repository;
    private readonly SessionStateStore _sessionStateStore;
    private readonly TimeProvider _timeProvider;

    public MainWindowViewModel(
        CliRunnerService cliRunner,
        OutreachRepository repository,
        SessionStateStore sessionStateStore,
        TimeProvider timeProvider)
    {
        _cliRunner = cliRunner;
        _repository = repository;
        _sessionStateStore = sessionStateStore;
        _timeProvider = timeProvider;

        QueueItems = [];
        HistoryItems = [];
        FilteredHistoryItems = [];

        var session = _sessionStateStore.Load();
        CurrentViewIndex = ParseViewIndex(session.LastView);
        RunStatus = "Using nudge.local.json. Run when ready.";
        RunKeywords = string.IsNullOrWhiteSpace(session.RunKeywords)
            ? string.Join(", ", DefaultRunKeywords)
            : session.RunKeywords;
        PublishedAfterDays = string.IsNullOrWhiteSpace(session.PublishedAfterDays)
            ? DefaultPublishedAfterDays.ToString()
            : session.PublishedAfterDays;
        RunTop = string.IsNullOrWhiteSpace(session.RunTop)
            ? DefaultTop.ToString()
            : session.RunTop;
        EvaluateRunConfigurationState();
        _ = LoadInitialDataAsync();
    }

    public ObservableCollection<QueueItem> QueueItems { get; }
    public ObservableCollection<HistoryEvent> HistoryItems { get; }
    public ObservableCollection<HistoryEvent> FilteredHistoryItems { get; }

    [ObservableProperty]
    private int currentViewIndex;

    [ObservableProperty]
    private string runStatus = string.Empty;

    [ObservableProperty]
    private string commandPreview = string.Empty;

    [ObservableProperty]
    private string warningsText = string.Empty;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(RunFromConfigCommand))]
    private string runKeywords = string.Empty;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(RunFromConfigCommand))]
    private string publishedAfterDays = string.Empty;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(RunFromConfigCommand))]
    private string runTop = string.Empty;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(RunFromConfigCommand))]
    [NotifyCanExecuteChangedFor(nameof(RefreshQueueCommand))]
    [NotifyCanExecuteChangedFor(nameof(MarkContactedCommand))]
    [NotifyCanExecuteChangedFor(nameof(MarkRepliedYesCommand))]
    [NotifyCanExecuteChangedFor(nameof(MarkRepliedNoCommand))]
    [NotifyCanExecuteChangedFor(nameof(SnoozeCommand))]
    [NotifyCanExecuteChangedFor(nameof(SaveAnnotationCommand))]
    [NotifyCanExecuteChangedFor(nameof(StartFullResetCommand))]
    [NotifyCanExecuteChangedFor(nameof(ConfirmFullResetCommand))]
    [NotifyCanExecuteChangedFor(nameof(CancelFullResetCommand))]
    private bool isBusy;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(MarkContactedCommand))]
    [NotifyCanExecuteChangedFor(nameof(MarkRepliedYesCommand))]
    [NotifyCanExecuteChangedFor(nameof(MarkRepliedNoCommand))]
    [NotifyCanExecuteChangedFor(nameof(SnoozeCommand))]
    [NotifyCanExecuteChangedFor(nameof(SaveAnnotationCommand))]
    private QueueItem? selectedQueueItem;

    [ObservableProperty]
    private string queueTags = string.Empty;

    [ObservableProperty]
    private string queueNote = string.Empty;

    [ObservableProperty]
    private string manualContactEmail = string.Empty;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SnoozeCommand))]
    private DateTimeOffset? snoozeUntilUtc;

    [ObservableProperty]
    private string historyFilterText = string.Empty;

    [ObservableProperty]
    private string runConfigMessage = string.Empty;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(StartFullResetCommand))]
    [NotifyCanExecuteChangedFor(nameof(ConfirmFullResetCommand))]
    [NotifyCanExecuteChangedFor(nameof(CancelFullResetCommand))]
    private bool isFullResetConfirmVisible;

    public int ContactableCount => QueueItems.Count;

    public string SelectedItemSummary
    {
        get
        {
            if (SelectedQueueItem is null)
            {
                return "Select an item in the queue to view details and actions.";
            }

            var newestEpisode = SelectedQueueItem.NewestEpisodePublishedAtUtc?.ToString("u") ?? "-";
            return
                $"State: {SelectedQueueItem.State} | Score: {SelectedQueueItem.Score:F3} | " +
                $"Contact: {SelectedQueueItem.EffectiveContactEmail} | Newest episode: {newestEpisode}";
        }
    }

    public IReadOnlyList<string> SelectedRecentEpisodeTitles =>
        SelectedQueueItem?.RecentEpisodeTitles ?? Array.Empty<string>();

    public bool HasQueueSelection => SelectedQueueItem is not null;

    public string QueueEmptyMessage =>
        QueueItems.Count == 0
            ? "No contactable targets yet. Run the workflow in the Run tab, then refresh queue."
            : string.Empty;

    public string HistoryEmptyMessage =>
        FilteredHistoryItems.Count == 0
            ? "No history events match this filter."
            : string.Empty;

    public string SelectedEpisodesEmptyMessage =>
        SelectedRecentEpisodeTitles.Count == 0
            ? "No recent episode titles are available for this target."
            : string.Empty;

    partial void OnCurrentViewIndexChanged(int value)
    {
        PersistSession();
    }

    partial void OnSelectedQueueItemChanged(QueueItem? value)
    {
        if (value is null)
        {
            QueueTags = string.Empty;
            QueueNote = string.Empty;
            ManualContactEmail = string.Empty;
            SnoozeUntilUtc = null;
            OnPropertyChanged(nameof(HasQueueSelection));
            OnPropertyChanged(nameof(SelectedItemSummary));
            OnPropertyChanged(nameof(SelectedRecentEpisodeTitles));
            OnPropertyChanged(nameof(SelectedEpisodesEmptyMessage));
            return;
        }

        QueueTags = value.Tags;
        QueueNote = value.Note;
        ManualContactEmail = value.ManualContactEmail ?? value.ContactEmail ?? string.Empty;
        SnoozeUntilUtc = value.SnoozeUntilUtc;
        OnPropertyChanged(nameof(HasQueueSelection));
        OnPropertyChanged(nameof(SelectedItemSummary));
        OnPropertyChanged(nameof(SelectedRecentEpisodeTitles));
        OnPropertyChanged(nameof(SelectedEpisodesEmptyMessage));
        _ = RefreshHistoryAsync(value.IdentityKey);
    }

    partial void OnHistoryFilterTextChanged(string value)
    {
        ApplyHistoryFilter();
    }

    partial void OnRunKeywordsChanged(string value)
    {
        EvaluateRunConfigurationState();
        PersistSession();
    }

    partial void OnPublishedAfterDaysChanged(string value)
    {
        EvaluateRunConfigurationState();
        PersistSession();
    }

    partial void OnRunTopChanged(string value)
    {
        EvaluateRunConfigurationState();
        PersistSession();
    }

    [RelayCommand]
    private async Task RunFromConfigAsync()
    {
        var profileResult = TryBuildRunProfile();
        if (!profileResult.Success || profileResult.Profile is null)
        {
            RunStatus = profileResult.ErrorMessage;
            return;
        }

        IsBusy = true;
        try
        {
            var profile = profileResult.Profile;
            RunStatus = "Running CLI...";
            var cliRun = await _cliRunner.RunAsync(profile);
            CommandPreview = cliRun.CommandPreview;
            WarningsText = string.IsNullOrWhiteSpace(cliRun.StdErr) ? "-" : cliRun.StdErr.Trim();

            if (!cliRun.Success || cliRun.Envelope is null)
            {
                RunStatus = cliRun.ErrorMessage;
                return;
            }

            await _repository.SaveRunAsync(cliRun.Envelope, cliRun.CommandPreview, cliRun.StdOut, cliRun.StdErr);
            RunStatus = $"Run ingested at {_timeProvider.GetUtcNow():u}. Loaded {cliRun.Envelope.Total} targets.";

            await RefreshQueueAsync();
            await RefreshHistoryAsync();
            CurrentViewIndex = 1;
        }
        catch (Exception ex)
        {
            RunStatus = $"Run failed: {ex.Message}";
        }
        finally
        {
            IsBusy = false;
        }
    }

    private bool CanRunFromConfig()
    {
        return !IsBusy && TryBuildRunProfile().Success;
    }

    private bool CanRefreshQueue()
    {
        return !IsBusy;
    }

    [RelayCommand]
    private void ResetRunConfig()
    {
        RunKeywords = string.Join(", ", DefaultRunKeywords);
        PublishedAfterDays = DefaultPublishedAfterDays.ToString();
        RunTop = DefaultTop.ToString();
        RunStatus = "Run settings reset to defaults.";
    }

    [RelayCommand(CanExecute = nameof(CanStartFullReset))]
    private void StartFullReset()
    {
        IsFullResetConfirmVisible = true;
        RunStatus = "Warning: full reset will clear all queue/history/state data.";
    }

    private bool CanStartFullReset()
    {
        return !IsBusy && !IsFullResetConfirmVisible;
    }

    [RelayCommand(CanExecute = nameof(CanConfirmOrCancelFullReset))]
    private void CancelFullReset()
    {
        IsFullResetConfirmVisible = false;
        RunStatus = "Full reset canceled.";
    }

    [RelayCommand(CanExecute = nameof(CanConfirmOrCancelFullReset))]
    private async Task ConfirmFullResetAsync()
    {
        IsBusy = true;
        try
        {
            await _repository.ClearAllDataAsync();
            _sessionStateStore.Clear();

            QueueItems.Clear();
            SelectedQueueItem = null;
            OnPropertyChanged(nameof(ContactableCount));
            OnPropertyChanged(nameof(QueueEmptyMessage));

            HistoryItems.Clear();
            FilteredHistoryItems.Clear();
            OnPropertyChanged(nameof(HistoryEmptyMessage));

            HistoryFilterText = string.Empty;
            WarningsText = string.Empty;
            IsFullResetConfirmVisible = false;

            RunKeywords = string.Join(", ", DefaultRunKeywords);
            PublishedAfterDays = DefaultPublishedAfterDays.ToString();
            RunTop = DefaultTop.ToString();
            CurrentViewIndex = 0;

            RunStatus = "Full reset complete. All saved run and outreach data was cleared.";
        }
        catch (Exception ex)
        {
            RunStatus = $"Full reset failed: {ex.Message}";
        }
        finally
        {
            IsBusy = false;
        }
    }

    private bool CanConfirmOrCancelFullReset()
    {
        return !IsBusy && IsFullResetConfirmVisible;
    }

    [RelayCommand]
    private async Task RefreshQueueAsync()
    {
        IsBusy = true;
        try
        {
            var queue = await _repository.GetContactableQueueAsync();
            QueueItems.Clear();
            foreach (var item in queue)
            {
                QueueItems.Add(item);
            }

            SelectedQueueItem = QueueItems.FirstOrDefault();
            OnPropertyChanged(nameof(ContactableCount));
            OnPropertyChanged(nameof(QueueEmptyMessage));
            RunStatus = $"Queue refreshed: {QueueItems.Count} contactable target(s).";
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    private async Task MarkContactedAsync()
    {
        if (SelectedQueueItem is null)
        {
            return;
        }

        IsBusy = true;
        try
        {
            await _repository.MarkContactedAsync(SelectedQueueItem, QueueTags, QueueNote);
            RunStatus = $"Marked contacted. Cooldown active for {SelectedQueueItem.ShowName}.";
            await RefreshQueueAsync();
            await RefreshHistoryAsync();
        }
        finally
        {
            IsBusy = false;
        }
    }

    private bool CanMarkContacted()
    {
        return SelectedQueueItem is not null && !IsBusy;
    }

    [RelayCommand]
    private async Task MarkRepliedYesAsync()
    {
        if (SelectedQueueItem is null)
        {
            return;
        }

        IsBusy = true;
        try
        {
            await _repository.MarkRepliedYesAsync(SelectedQueueItem, QueueTags, QueueNote);
            RunStatus = $"Marked replied YES for {SelectedQueueItem.ShowName}.";
            await RefreshQueueAsync();
            await RefreshHistoryAsync();
        }
        finally
        {
            IsBusy = false;
        }
    }

    private bool CanMarkRepliedYes()
    {
        return SelectedQueueItem is not null && !IsBusy;
    }

    [RelayCommand]
    private async Task MarkRepliedNoAsync()
    {
        if (SelectedQueueItem is null)
        {
            return;
        }

        IsBusy = true;
        try
        {
            await _repository.MarkRepliedNoAsync(SelectedQueueItem, QueueTags, QueueNote);
            RunStatus = $"Marked replied NO (forever block) for {SelectedQueueItem.ShowName}.";
            await RefreshQueueAsync();
            await RefreshHistoryAsync();
        }
        finally
        {
            IsBusy = false;
        }
    }

    private bool CanMarkRepliedNo()
    {
        return SelectedQueueItem is not null && !IsBusy;
    }

    [RelayCommand]
    private async Task SnoozeAsync()
    {
        if (SelectedQueueItem is null || SnoozeUntilUtc is null)
        {
            RunStatus = "Set a snooze date first.";
            return;
        }

        IsBusy = true;
        try
        {
            await _repository.MarkSnoozedAsync(SelectedQueueItem, SnoozeUntilUtc.Value, QueueTags, QueueNote);
            RunStatus = $"Snoozed {SelectedQueueItem.ShowName} until {SnoozeUntilUtc:yyyy-MM-dd}.";
            await RefreshQueueAsync();
            await RefreshHistoryAsync();
        }
        finally
        {
            IsBusy = false;
        }
    }

    private bool CanSnooze()
    {
        return SelectedQueueItem is not null && SnoozeUntilUtc is not null && !IsBusy;
    }

    [RelayCommand]
    private async Task SaveAnnotationAsync()
    {
        if (SelectedQueueItem is null)
        {
            return;
        }

        IsBusy = true;
        try
        {
            await _repository.SaveAnnotationAsync(SelectedQueueItem, QueueTags, QueueNote, ManualContactEmail);
            RunStatus = $"Saved note for {SelectedQueueItem.ShowName}.";
            await RefreshQueueAsync();
            await RefreshHistoryAsync(SelectedQueueItem.IdentityKey);
        }
        finally
        {
            IsBusy = false;
        }
    }

    private bool CanSaveAnnotation()
    {
        return SelectedQueueItem is not null && !IsBusy;
    }

    [RelayCommand]
    private async Task RefreshHistoryAsync(string? identityKey = null)
    {
        var history = await _repository.GetHistoryAsync(identityKey);
        HistoryItems.Clear();
        foreach (var entry in history)
        {
            HistoryItems.Add(entry);
        }

        ApplyHistoryFilter();
    }

    private void ApplyHistoryFilter()
    {
        var filter = HistoryFilterText?.Trim() ?? string.Empty;
        var filtered = HistoryItems
            .Where(h =>
                string.IsNullOrWhiteSpace(filter) ||
                h.IdentityKey.Contains(filter, StringComparison.OrdinalIgnoreCase) ||
                (h.ShowName?.Contains(filter, StringComparison.OrdinalIgnoreCase) ?? false) ||
                (h.EffectiveContactEmail?.Contains(filter, StringComparison.OrdinalIgnoreCase) ?? false) ||
                h.EventType.Contains(filter, StringComparison.OrdinalIgnoreCase) ||
                h.Note.Contains(filter, StringComparison.OrdinalIgnoreCase) ||
                h.Tags.Contains(filter, StringComparison.OrdinalIgnoreCase))
            .ToList();

        FilteredHistoryItems.Clear();
        foreach (var item in filtered)
        {
            FilteredHistoryItems.Add(item);
        }

        OnPropertyChanged(nameof(HistoryEmptyMessage));
    }

    private async Task LoadInitialDataAsync()
    {
        try
        {
            await RefreshQueueAsync();
            await RefreshHistoryAsync();
            if (QueueItems.Count == 0)
            {
                RunStatus = "No contactable targets yet. Configure a run when ready.";
            }
        }
        catch (Exception ex)
        {
            RunStatus = $"Ready, but failed to load saved data: {ex.Message}";
        }
    }

    private int ParseViewIndex(string raw)
    {
        if (int.TryParse(raw, out var parsedIndex) && parsedIndex is >= 0 and <= 2)
        {
            return parsedIndex;
        }

        return 0;
    }

    private void PersistSession()
    {
        _sessionStateStore.Save(new SessionState
        {
            LastView = CurrentViewIndex.ToString(),
            RunKeywords = RunKeywords,
            PublishedAfterDays = PublishedAfterDays,
            RunTop = RunTop
        });
    }

    private RunConfigProfile BuildRunProfile()
    {
        var parsedKeywords = ParseRunKeywords(RunKeywords);
        var publishedAfterDaysValue = TryParsePublishedAfterDays(PublishedAfterDays, out var parsedDays)
            ? parsedDays
            : DefaultPublishedAfterDays;
        var topValue = TryParseTop(RunTop, out var parsedTop)
            ? parsedTop
            : DefaultTop;

        return new RunConfigProfile(
            parsedKeywords.Count == 0 ? DefaultRunKeywords : parsedKeywords,
            publishedAfterDaysValue,
            topValue,
            false,
            false);
    }

    private (bool Success, RunConfigProfile? Profile, string ErrorMessage) TryBuildRunProfile()
    {
        var parsedKeywords = ParseRunKeywords(RunKeywords);
        if (parsedKeywords.Count == 0)
        {
            return (false, null, "Enter at least one keyword for --keywords.");
        }

        if (!TryParsePublishedAfterDays(PublishedAfterDays, out var parsedDays))
        {
            return (false, null, "Published after days must be a non-negative whole number.");
        }

        if (!TryParseTop(RunTop, out var parsedTop))
        {
            return (false, null, "Top must be a positive whole number.");
        }

        return (true, new RunConfigProfile(parsedKeywords, parsedDays, parsedTop, false, false), string.Empty);
    }

    private static List<string> ParseRunKeywords(string rawKeywords)
    {
        return (rawKeywords ?? string.Empty)
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(keyword => !string.IsNullOrWhiteSpace(keyword))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static bool TryParsePublishedAfterDays(string rawDays, out int days)
    {
        return int.TryParse(rawDays?.Trim(), out days) && days >= 0;
    }

    private static bool TryParseTop(string rawTop, out int top)
    {
        return int.TryParse(rawTop?.Trim(), out top) && top > 0;
    }

    private void RefreshCommandPreview()
    {
        CommandPreview = _cliRunner.BuildCommandPreview(BuildRunProfile());
    }

    private void EvaluateRunConfigurationState()
    {
        var profileResult = TryBuildRunProfile();
        if (!profileResult.Success || profileResult.Profile is null)
        {
            RunConfigMessage = $"Needs input: {profileResult.ErrorMessage}";
            RefreshCommandPreview();
            return;
        }

        var keywordCount = profileResult.Profile.Keywords.Count;
        var dayLabel = profileResult.Profile.PublishedAfterDays == 1 ? "day" : "days";
        RunConfigMessage =
            $"Ready: {keywordCount} keyword(s), last {profileResult.Profile.PublishedAfterDays} {dayLabel}, top {profileResult.Profile.Top}.";
        CommandPreview = _cliRunner.BuildCommandPreview(profileResult.Profile);
    }
}
