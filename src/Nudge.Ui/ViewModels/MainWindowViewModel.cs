using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.Versioning;
using System.Text;
using System.Text.Json;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Win32;
using Nudge.Ui.Models;
using Nudge.Ui.Services;

namespace Nudge.Ui.ViewModels;

public partial class MainWindowViewModel : ViewModelBase
{
    private const string RssViewerBaseUrl = "https://rssrdr.com/?rss=";
    private const int DefaultPublishedAfterDays = 60;
    private const int DefaultTop = 3;
    private const int RecentEpisodeDisplayLimit = 7;
    private static readonly IReadOnlyList<SnoozePresetOption> DefaultSnoozePresets =
    [
        new SnoozePresetOption("+1 day", 1, SnoozePresetUnit.Days),
        new SnoozePresetOption("+3 days", 3, SnoozePresetUnit.Days),
        new SnoozePresetOption("+7 days", 7, SnoozePresetUnit.Days),
        new SnoozePresetOption("+1 month", 1, SnoozePresetUnit.Months),
        new SnoozePresetOption("+3 months", 3, SnoozePresetUnit.Months),
        new SnoozePresetOption("+6 months", 6, SnoozePresetUnit.Months)
    ];

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
    private static readonly IReadOnlyList<OutreachState> QueueStateDisplayOrder =
    [
        OutreachState.New,
        OutreachState.ContactedWaiting,
        OutreachState.Snoozed,
        OutreachState.RepliedYes,
        OutreachState.RepliedNo
    ];

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
        QueueGroups = [];
        HistoryItems = [];
        FilteredHistoryItems = [];
        SelectedNicheFitHighlights = [];
        SnoozePresetOptions = DefaultSnoozePresets;
        SelectedSnoozePreset = SnoozePresetOptions.FirstOrDefault();

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
    public ObservableCollection<QueueStateGroup> QueueGroups { get; }
    public ObservableCollection<HistoryEvent> HistoryItems { get; }
    public ObservableCollection<HistoryEvent> FilteredHistoryItems { get; }
    public ObservableCollection<string> SelectedNicheFitHighlights { get; }
    public IReadOnlyList<SnoozePresetOption> SnoozePresetOptions { get; }

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
    [NotifyCanExecuteChangedFor(nameof(MarkContactedCommand))]
    [NotifyCanExecuteChangedFor(nameof(MarkRepliedYesCommand))]
    [NotifyCanExecuteChangedFor(nameof(MarkRepliedNoCommand))]
    [NotifyCanExecuteChangedFor(nameof(SnoozeCommand))]
    [NotifyCanExecuteChangedFor(nameof(SaveAnnotationCommand))]
    [NotifyCanExecuteChangedFor(nameof(StartFullResetCommand))]
    [NotifyCanExecuteChangedFor(nameof(ConfirmFullResetCommand))]
    [NotifyCanExecuteChangedFor(nameof(CancelFullResetCommand))]
    [NotifyCanExecuteChangedFor(nameof(OpenFeedUrlCommand))]
    private bool isBusy;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(MarkContactedCommand))]
    [NotifyCanExecuteChangedFor(nameof(MarkRepliedYesCommand))]
    [NotifyCanExecuteChangedFor(nameof(MarkRepliedNoCommand))]
    [NotifyCanExecuteChangedFor(nameof(SnoozeCommand))]
    [NotifyCanExecuteChangedFor(nameof(SaveAnnotationCommand))]
    [NotifyCanExecuteChangedFor(nameof(OpenFeedUrlCommand))]
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
    [NotifyCanExecuteChangedFor(nameof(SnoozeCommand))]
    private SnoozePresetOption? selectedSnoozePreset;

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

    public IReadOnlyList<QueueEpisode> SelectedRecentEpisodes =>
        SelectedQueueItem?.RecentEpisodes.Take(RecentEpisodeDisplayLimit).ToArray() ?? Array.Empty<QueueEpisode>();

    public string SelectedFeedUrl =>
        string.IsNullOrWhiteSpace(SelectedQueueItem?.FeedUrl) ? "-" : SelectedQueueItem!.FeedUrl;

    public string SelectedLanguageDisplay =>
        string.IsNullOrWhiteSpace(SelectedQueueItem?.DetectedLanguage) ? "-" : SelectedQueueItem!.DetectedLanguage;

    public string SelectedPriorityDisplay =>
        string.IsNullOrWhiteSpace(SelectedQueueItem?.OutreachPriority) ? "-" : SelectedQueueItem!.OutreachPriority;

    public string SelectedScoreDisplay =>
        SelectedQueueItem is null ? "-" : SelectedQueueItem.Score.ToString("F3");

    public string SelectedReachDisplay => FormatAsPercent(SelectedQueueItem?.Reach);
    public string SelectedFrequencyDisplay => FormatAsPercent(SelectedQueueItem?.Frequency);
    public string SelectedNicheFitDisplay => FormatAsPercent(SelectedQueueItem?.NicheFit);
    public string SelectedActivityDisplay => FormatAsPercent(SelectedQueueItem?.ActivityScore);

    public bool HasQueueSelection => SelectedQueueItem is not null;

    public string QueueEmptyMessage =>
        QueueItems.Count == 0
            ? "No tracked targets yet. Run the workflow in the Run tab to ingest results."
            : string.Empty;

    public string HistoryEmptyMessage =>
        FilteredHistoryItems.Count == 0
            ? "No history events match this filter."
            : string.Empty;

    public string SelectedEpisodesEmptyMessage =>
        SelectedRecentEpisodes.Count == 0
            ? "No recent episode titles are available for this target."
            : string.Empty;

    public string SelectedNicheFitHighlightsEmptyMessage =>
        SelectedNicheFitHighlights.Count == 0
            ? "No token-level niche-fit details are available for this target."
            : string.Empty;

    public string SnoozeHelperText
    {
        get
        {
            if (SnoozeUntilUtc is null)
            {
                return "Choose a snooze duration from the dropdown.";
            }

            var selectedDate = ToLocalDate(SnoozeUntilUtc.Value);
            var today = GetTodayLocal();
            var deltaDays = (selectedDate - today).Days;

            if (deltaDays <= 0)
            {
                return "Snooze date must be in the future.";
            }

            var dayLabel = deltaDays == 1 ? "day" : "days";
            return $"Will return to queue in {deltaDays} {dayLabel}.";
        }
    }

    public string SnoozeUntilDisplay
    {
        get
        {
            if (SnoozeUntilUtc is null)
            {
                return "Snooze until: not set";
            }

            var selectedDate = ToLocalDate(SnoozeUntilUtc.Value);
            return $"Snooze until: {selectedDate:yyyy-MM-dd}";
        }
    }

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
            RefreshSelectedTargetComputedState();
            return;
        }

        QueueTags = value.Tags;
        QueueNote = value.Note;
        ManualContactEmail = value.ManualContactEmail ?? value.ContactEmail ?? string.Empty;
        SnoozeUntilUtc = value.SnoozeUntilUtc;
        if (SnoozeUntilUtc is null && SelectedSnoozePreset is not null)
        {
            SnoozeUntilUtc = BuildSnoozeDateFromPreset(SelectedSnoozePreset);
        }
        RefreshSelectedTargetComputedState();
        _ = RefreshHistoryAsync(value.IdentityKey);
    }

    partial void OnSnoozeUntilUtcChanged(DateTimeOffset? value)
    {
        OnPropertyChanged(nameof(SnoozeHelperText));
        OnPropertyChanged(nameof(SnoozeUntilDisplay));
    }

    partial void OnSelectedSnoozePresetChanged(SnoozePresetOption? value)
    {
        if (SelectedQueueItem is null || value is null)
        {
            return;
        }

        SnoozeUntilUtc = BuildSnoozeDateFromPreset(value);
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

    private async Task RefreshQueueAsync()
    {
        var previousSelectedIdentity = SelectedQueueItem?.IdentityKey;
        var queue = await _repository.GetTrackerItemsAsync();

        QueueItems.Clear();
        foreach (var item in queue)
        {
            QueueItems.Add(item);
        }

        RebuildQueueGroups();
        SelectedQueueItem = QueueItems.FirstOrDefault(item => item.IdentityKey == previousSelectedIdentity) ??
                            QueueItems.FirstOrDefault();

        OnPropertyChanged(nameof(ContactableCount));
        OnPropertyChanged(nameof(QueueEmptyMessage));
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

        var normalizedSnoozeUtc = NormalizeSnoozeDateToUtc(SnoozeUntilUtc.Value);
        if (!IsSnoozeDateValid(normalizedSnoozeUtc))
        {
            RunStatus = "Snooze date must be in the future.";
            return;
        }

        IsBusy = true;
        try
        {
            await _repository.MarkSnoozedAsync(SelectedQueueItem, normalizedSnoozeUtc, QueueTags, QueueNote);
            RunStatus = $"Snoozed {SelectedQueueItem.ShowName} until {ToLocalDate(normalizedSnoozeUtc):yyyy-MM-dd}.";
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
        return SelectedQueueItem is not null && SnoozeUntilUtc is not null && IsSnoozeDateValid(SnoozeUntilUtc.Value) && !IsBusy;
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

    [RelayCommand(CanExecute = nameof(CanOpenFeedUrl))]
    private void OpenFeedUrl()
    {
        if (string.IsNullOrWhiteSpace(SelectedQueueItem?.FeedUrl))
        {
            RunStatus = "Feed URL is unavailable for this item.";
            return;
        }

        if (!Uri.TryCreate(SelectedQueueItem.FeedUrl, UriKind.Absolute, out var uri))
        {
            RunStatus = $"Feed URL is invalid: {SelectedQueueItem.FeedUrl}";
            return;
        }

        if (!TryBuildRssViewerUri(uri, out var viewerUri))
        {
            RunStatus = $"Feed URL is invalid: {SelectedQueueItem.FeedUrl}";
            return;
        }

        if (TryOpenUrlInNewWindow(viewerUri, out var launchError))
        {
            RunStatus = $"Opened feed for '{SelectedQueueItem.ShowName}' in an RSS viewer.";
            return;
        }

        RunStatus = $"Unable to open feed URL: {launchError}";
    }

    private bool CanOpenFeedUrl()
    {
        return !IsBusy &&
               SelectedQueueItem is not null &&
               !string.IsNullOrWhiteSpace(SelectedQueueItem.FeedUrl);
    }

    [RelayCommand]
    private void OpenEpisode(QueueEpisode? episode)
    {
        if (episode is null || string.IsNullOrWhiteSpace(episode.Url))
        {
            RunStatus = "Episode link is unavailable for this item.";
            return;
        }

        if (!Uri.TryCreate(episode.Url, UriKind.Absolute, out var uri))
        {
            RunStatus = $"Episode URL is invalid: {episode.Url}";
            return;
        }

        if (TryOpenUrlInNewWindow(uri, out var launchError))
        {
            RunStatus = $"Opened episode link for '{episode.Title}' in a new browser window.";
            return;
        }

        RunStatus = $"Unable to open episode link: {launchError}";
    }

    private static bool TryBuildRssViewerUri(Uri feedUri, out Uri viewerUri)
    {
        viewerUri = null!;
        if (!feedUri.IsAbsoluteUri)
        {
            return false;
        }

        var encodedFeedUrl = Uri.EscapeDataString(feedUri.ToString());
        return Uri.TryCreate($"{RssViewerBaseUrl}{encodedFeedUrl}", UriKind.Absolute, out viewerUri);
    }

    private static bool TryOpenUrlInNewWindow(Uri uri, out string error)
    {
        try
        {
            if (OperatingSystem.IsWindows())
            {
                var command = GetDefaultBrowserOpenCommand();
                if (!string.IsNullOrWhiteSpace(command) &&
                    TryExtractExecutablePath(command, out var executablePath) &&
                    TryBuildNewWindowArguments(executablePath, uri, out var arguments))
                {
                    Process.Start(new ProcessStartInfo
                    {
                        FileName = executablePath,
                        Arguments = arguments,
                        UseShellExecute = false
                    });

                    error = string.Empty;
                    return true;
                }

                error = "The default browser does not expose a supported 'new window' launch mode.";
                return false;
            }

            Process.Start(new ProcessStartInfo
            {
                FileName = uri.ToString(),
                UseShellExecute = true
            });

            error = string.Empty;
            return true;
        }
        catch (Exception ex)
        {
            error = ex.Message;
            return false;
        }
    }

    [SupportedOSPlatform("windows")]
    private static string? GetDefaultBrowserOpenCommand()
    {
        const string userChoicePath = @"Software\Microsoft\Windows\Shell\Associations\UrlAssociations\http\UserChoice";
        using var userChoiceKey = Registry.CurrentUser.OpenSubKey(userChoicePath);
        var progId = userChoiceKey?.GetValue("ProgId") as string;
        if (string.IsNullOrWhiteSpace(progId))
        {
            return null;
        }

        using var openCommandKey = Registry.ClassesRoot.OpenSubKey($@"{progId}\shell\open\command");
        return openCommandKey?.GetValue(null) as string;
    }

    private static bool TryExtractExecutablePath(string command, out string executablePath)
    {
        executablePath = string.Empty;
        var trimmed = command.Trim();
        if (string.IsNullOrWhiteSpace(trimmed))
        {
            return false;
        }

        if (trimmed.StartsWith('\"'))
        {
            var closingQuoteIndex = trimmed.IndexOf('\"', 1);
            if (closingQuoteIndex <= 1)
            {
                return false;
            }

            executablePath = trimmed[1..closingQuoteIndex];
            return !string.IsNullOrWhiteSpace(executablePath);
        }

        var tokenBuilder = new StringBuilder();
        foreach (var ch in trimmed)
        {
            if (char.IsWhiteSpace(ch))
            {
                break;
            }

            tokenBuilder.Append(ch);
        }

        executablePath = tokenBuilder.ToString();
        return !string.IsNullOrWhiteSpace(executablePath);
    }

    private static bool TryBuildNewWindowArguments(string executablePath, Uri uri, out string arguments)
    {
        arguments = string.Empty;

        var browser = Path.GetFileNameWithoutExtension(executablePath).ToLowerInvariant();
        var quotedUrl = $"\"{uri}\"";
        arguments = browser switch
        {
            "msedge" => $"--new-window {quotedUrl}",
            "chrome" => $"--new-window {quotedUrl}",
            "brave" => $"--new-window {quotedUrl}",
            "vivaldi" => $"--new-window {quotedUrl}",
            "opera" => $"--new-window {quotedUrl}",
            "firefox" => $"-new-window {quotedUrl}",
            _ => string.Empty
        };

        return !string.IsNullOrWhiteSpace(arguments);
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
                RunStatus = "No tracked targets yet. Configure a run when ready.";
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

    private void RefreshSelectedTargetComputedState()
    {
        PopulateSelectedNicheFitHighlights();

        OnPropertyChanged(nameof(HasQueueSelection));
        OnPropertyChanged(nameof(SelectedItemSummary));
        OnPropertyChanged(nameof(SelectedRecentEpisodes));
        OnPropertyChanged(nameof(SelectedEpisodesEmptyMessage));
        OnPropertyChanged(nameof(SelectedNicheFitHighlightsEmptyMessage));
        OnPropertyChanged(nameof(SelectedFeedUrl));
        OnPropertyChanged(nameof(SelectedLanguageDisplay));
        OnPropertyChanged(nameof(SelectedPriorityDisplay));
        OnPropertyChanged(nameof(SelectedScoreDisplay));
        OnPropertyChanged(nameof(SelectedReachDisplay));
        OnPropertyChanged(nameof(SelectedFrequencyDisplay));
        OnPropertyChanged(nameof(SelectedNicheFitDisplay));
        OnPropertyChanged(nameof(SelectedActivityDisplay));
        OnPropertyChanged(nameof(SnoozeHelperText));
        OnPropertyChanged(nameof(SnoozeUntilDisplay));
    }

    private void RebuildQueueGroups()
    {
        QueueGroups.Clear();
        var groupedByState = QueueItems
            .GroupBy(item => item.State)
            .ToDictionary(group => group.Key, group => group.OrderByDescending(item => item.Score).ToList());

        foreach (var state in QueueStateDisplayOrder)
        {
            if (!groupedByState.TryGetValue(state, out var items) || items.Count == 0)
            {
                continue;
            }

            QueueGroups.Add(new QueueStateGroup(
                BuildQueueStateHeader(state, items.Count),
                new ObservableCollection<QueueItem>(items)));
        }
    }

    private static string BuildQueueStateHeader(OutreachState state, int count)
    {
        var label = state switch
        {
            OutreachState.New => "New",
            OutreachState.ContactedWaiting => "Contacted - waiting",
            OutreachState.Snoozed => "Snoozed",
            OutreachState.RepliedYes => "Replied YES",
            OutreachState.RepliedNo => "Replied NO",
            _ => state.ToString()
        };

        return $"{label} ({count})";
    }

    private void PopulateSelectedNicheFitHighlights()
    {
        SelectedNicheFitHighlights.Clear();
        var rawJson = SelectedQueueItem?.NicheFitBreakdownJson;
        if (string.IsNullOrWhiteSpace(rawJson))
        {
            return;
        }

        try
        {
            using var document = JsonDocument.Parse(rawJson);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                return;
            }

            if (root.TryGetProperty("tokenHits", out var tokenHits) && tokenHits.ValueKind == JsonValueKind.Array)
            {
                foreach (var tokenHit in tokenHits.EnumerateArray().Take(8))
                {
                    if (!tokenHit.TryGetProperty("token", out var tokenNode) || tokenNode.ValueKind != JsonValueKind.String)
                    {
                        continue;
                    }

                    var token = tokenNode.GetString() ?? string.Empty;
                    if (string.IsNullOrWhiteSpace(token))
                    {
                        continue;
                    }

                    var hits = tokenHit.TryGetProperty("hits", out var hitsNode) && hitsNode.TryGetInt32(out var parsedHits)
                        ? parsedHits
                        : 0;
                    var weight = tokenHit.TryGetProperty("weight", out var weightNode) && weightNode.TryGetInt32(out var parsedWeight)
                        ? parsedWeight
                        : 0;
                    var contribution = tokenHit.TryGetProperty("contribution", out var contributionNode) && contributionNode.TryGetInt32(out var parsedContribution)
                        ? parsedContribution
                        : 0;

                    SelectedNicheFitHighlights.Add(
                        $"{token}: {hits} hit(s), weight {weight}, contribution {contribution}");
                }
            }

            if (root.TryGetProperty("businessContextDetected", out var businessContextNode) &&
                businessContextNode.ValueKind == JsonValueKind.True)
            {
                SelectedNicheFitHighlights.Add("Business-context signal detected in show text.");
            }
        }
        catch (JsonException)
        {
            SelectedNicheFitHighlights.Add("Could not parse token-level niche-fit details.");
        }
    }

    private static string FormatAsPercent(double? value)
    {
        if (!value.HasValue)
        {
            return "-";
        }

        return $"{Math.Clamp(value.Value, 0.0, 1.0) * 100:F1}%";
    }

    private DateTimeOffset BuildSnoozeDateFromPreset(SnoozePresetOption preset)
    {
        var targetDate = preset.Unit switch
        {
            SnoozePresetUnit.Months => GetTodayLocal().AddMonths(preset.Amount),
            _ => GetTodayLocal().AddDays(preset.Amount)
        };
        var offset = TimeZoneInfo.Local.GetUtcOffset(targetDate);
        return new DateTimeOffset(targetDate, offset).ToUniversalTime();
    }

    private bool IsSnoozeDateValid(DateTimeOffset value)
    {
        var selectedDate = ToLocalDate(value);
        return selectedDate > GetTodayLocal();
    }

    private static DateTime ToLocalDate(DateTimeOffset value)
    {
        return TimeZoneInfo.ConvertTime(value, TimeZoneInfo.Local).Date;
    }

    private DateTime GetTodayLocal()
    {
        return TimeZoneInfo.ConvertTime(_timeProvider.GetUtcNow(), TimeZoneInfo.Local).Date;
    }

    private DateTimeOffset NormalizeSnoozeDateToUtc(DateTimeOffset value)
    {
        var localDate = ToLocalDate(value);
        var localOffset = TimeZoneInfo.Local.GetUtcOffset(localDate);
        return new DateTimeOffset(localDate, localOffset).ToUniversalTime();
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

    public sealed record SnoozePresetOption(string Label, int Amount, SnoozePresetUnit Unit);

    public sealed class QueueStateGroup(string header, ObservableCollection<QueueItem> items)
    {
        public string Header { get; } = header;
        public ObservableCollection<QueueItem> Items { get; } = items;
    }

    public enum SnoozePresetUnit
    {
        Days = 0,
        Months = 1
    }
}
