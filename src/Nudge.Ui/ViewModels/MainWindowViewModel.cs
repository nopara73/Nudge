using System.Collections.ObjectModel;
using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Nudge.Ui.Models;
using Nudge.Ui.Services;

namespace Nudge.Ui.ViewModels;

public partial class MainWindowViewModel : ViewModelBase
{
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

    private readonly RunConfigParser _runConfigParser;
    private readonly CliRunnerService _cliRunner;
    private readonly OutreachRepository _repository;
    private readonly SessionStateStore _sessionStateStore;
    private readonly TimeProvider _timeProvider;

    public MainWindowViewModel(
        RunConfigParser runConfigParser,
        CliRunnerService cliRunner,
        OutreachRepository repository,
        SessionStateStore sessionStateStore,
        TimeProvider timeProvider)
    {
        _runConfigParser = runConfigParser;
        _cliRunner = cliRunner;
        _repository = repository;
        _sessionStateStore = sessionStateStore;
        _timeProvider = timeProvider;

        QueueItems = [];
        HistoryItems = [];
        FilteredHistoryItems = [];

        var session = _sessionStateStore.Load();
        ConfigPath = ResolveInitialConfigPath(session.LastConfigPath);
        CurrentViewIndex = ParseViewIndex(session.LastView);
        RunStatus = "Config defaults to nudge.local.json. Run when ready.";
    }

    public ObservableCollection<QueueItem> QueueItems { get; }
    public ObservableCollection<HistoryEvent> HistoryItems { get; }
    public ObservableCollection<HistoryEvent> FilteredHistoryItems { get; }

    [ObservableProperty]
    private int currentViewIndex;

    [ObservableProperty]
    private string configPath = string.Empty;

    [ObservableProperty]
    private string runStatus = string.Empty;

    [ObservableProperty]
    private string commandPreview = string.Empty;

    [ObservableProperty]
    private string warningsText = string.Empty;

    [ObservableProperty]
    private bool isBusy;

    [ObservableProperty]
    private QueueItem? selectedQueueItem;

    [ObservableProperty]
    private string queueTags = string.Empty;

    [ObservableProperty]
    private string queueNote = string.Empty;

    [ObservableProperty]
    private string manualContactEmail = string.Empty;

    [ObservableProperty]
    private DateTimeOffset? snoozeUntilUtc;

    [ObservableProperty]
    private string historyFilterText = string.Empty;

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

    partial void OnCurrentViewIndexChanged(int value)
    {
        PersistSession();
    }

    partial void OnConfigPathChanged(string value)
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
            OnPropertyChanged(nameof(SelectedItemSummary));
            return;
        }

        QueueTags = value.Tags;
        QueueNote = value.Note;
        ManualContactEmail = value.ManualContactEmail ?? value.ContactEmail ?? string.Empty;
        OnPropertyChanged(nameof(SelectedItemSummary));
        _ = RefreshHistoryAsync(value.IdentityKey);
    }

    partial void OnHistoryFilterTextChanged(string value)
    {
        ApplyHistoryFilter();
    }

    public void SetConfigPathFromUi(string configPath)
    {
        ConfigPath = configPath;
    }

    [RelayCommand]
    private async Task RunFromConfigAsync()
    {
        if (string.IsNullOrWhiteSpace(ConfigPath))
        {
            RunStatus = "Select a config JSON file first.";
            return;
        }

        IsBusy = true;
        try
        {
            var profile = BuildRunProfile();
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
            RunStatus = $"Saved tags/note for {SelectedQueueItem.ShowName}.";
            await RefreshQueueAsync();
            await RefreshHistoryAsync(SelectedQueueItem.IdentityKey);
        }
        finally
        {
            IsBusy = false;
        }
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
            LastConfigPath = ConfigPath
        });
    }

    private static string ResolveInitialConfigPath(string persistedPath)
    {
        if (!string.IsNullOrWhiteSpace(persistedPath) && File.Exists(persistedPath))
        {
            return persistedPath;
        }

        try
        {
            var repoRoot = RepositoryPaths.LocateRepositoryRoot();
            var nudgeLocalPath = Path.Combine(repoRoot, "nudge.local.json");
            if (File.Exists(nudgeLocalPath))
            {
                return nudgeLocalPath;
            }

            var outputPath = Path.Combine(repoRoot, "output.json");
            if (File.Exists(outputPath))
            {
                return outputPath;
            }
        }
        catch
        {
            // Ignore repo discovery issues and return empty path.
        }

        return string.Empty;
    }

    private RunConfigProfile BuildRunProfile()
    {
        if (string.IsNullOrWhiteSpace(ConfigPath))
        {
            throw new InvalidOperationException("Select a config JSON file first.");
        }

        var fileName = Path.GetFileName(ConfigPath);
        if (fileName.Equals("nudge.local.json", StringComparison.OrdinalIgnoreCase))
        {
            return new RunConfigProfile(
                DefaultRunKeywords,
                60,
                30,
                false,
                false);
        }

        return _runConfigParser.Parse(ConfigPath);
    }
}
