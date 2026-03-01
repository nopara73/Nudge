using System.Globalization;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Nudge.Ui.Models;

namespace Nudge.Ui.Services;

public sealed class OutreachRepository
{
    private const int CooldownDays = 90;
    private readonly string _connectionString;
    private readonly TimeProvider _timeProvider;

    public OutreachRepository(TimeProvider timeProvider)
    {
        _timeProvider = timeProvider;
        var appDataPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Nudge.Ui");
        Directory.CreateDirectory(appDataPath);

        var databasePath = Path.Combine(appDataPath, "nudge-ui.db");
        _connectionString = new SqliteConnectionStringBuilder { DataSource = databasePath }.ToString();
        EnsureInitialized();
    }

    public async Task<long> SaveRunAsync(CliOutputEnvelope envelope, string commandPreview, string stdout, string stderr, CancellationToken cancellationToken = default)
    {
        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken);

        var runId = await InsertRunAsync(connection, transaction, envelope, commandPreview, stdout, stderr, cancellationToken);

        foreach (var result in envelope.Results)
        {
            var identity = TargetIdentityResolver.Resolve(result.ShowId, result.ContactEmail);
            await InsertRunTargetAsync(connection, transaction, runId, identity, result, cancellationToken);
            await EnsureTargetStateRowAsync(connection, transaction, identity, result.ShowName, result.ContactEmail, envelope.GeneratedAtUtc, cancellationToken);
        }

        await transaction.CommitAsync(cancellationToken);
        return runId;
    }

    public async Task<IReadOnlyList<QueueItem>> GetContactableQueueAsync(CancellationToken cancellationToken = default)
    {
        const string sql = """
            WITH LatestRun AS (
                SELECT Id
                FROM Runs
                ORDER BY GeneratedAtUtc DESC
                LIMIT 1
            )
            SELECT
                rt.IdentityKey,
                rt.ShowId,
                rt.ShowName,
                rt.ContactEmail,
                ts.ManualContactEmail,
                rt.DetectedLanguage,
                rt.FeedUrl,
                rt.Score,
                rt.Reach,
                rt.Frequency,
                rt.NicheFit,
                rt.ActivityScore,
                rt.OutreachPriority,
                rt.NewestEpisodePublishedAtUtc,
                rt.RecentEpisodeTitlesJson,
                rt.NicheFitBreakdownJson,
                COALESCE(ts.State, 'New') AS State,
                ts.CooldownUntilUtc,
                ts.SnoozeUntilUtc,
                ts.ContactedAtUtc,
                COALESCE(ts.Tags, '') AS Tags,
                COALESCE(ts.Note, '') AS Note
            FROM RunTargets rt
            JOIN LatestRun lr ON rt.RunId = lr.Id
            LEFT JOIN TargetStates ts ON rt.IdentityKey = ts.IdentityKey
            ORDER BY rt.Score DESC
            """;

        var now = _timeProvider.GetUtcNow();
        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = sql;

        var queue = new List<QueueItem>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            var state = ParseState(reader.GetString(reader.GetOrdinal("State")));
            var cooldownUntil = ReadDateTimeOffset(reader, "CooldownUntilUtc");
            var snoozeUntil = ReadDateTimeOffset(reader, "SnoozeUntilUtc");

            if (!IsContactable(state, cooldownUntil, snoozeUntil, now))
            {
                continue;
            }

            var recentTitlesJson = reader.GetString(reader.GetOrdinal("RecentEpisodeTitlesJson"));
            var recentTitles = JsonSerializer.Deserialize<List<string>>(recentTitlesJson) ?? [];

            var contactEmail = ReadNullableString(reader, "ContactEmail");
            var manualContactEmail = ReadNullableString(reader, "ManualContactEmail");
            var effectiveEmail = !string.IsNullOrWhiteSpace(manualContactEmail)
                ? manualContactEmail!
                : (contactEmail ?? string.Empty);

            queue.Add(new QueueItem
            {
                IdentityKey = reader.GetString(reader.GetOrdinal("IdentityKey")),
                ShowId = reader.GetString(reader.GetOrdinal("ShowId")),
                ShowName = reader.GetString(reader.GetOrdinal("ShowName")),
                ContactEmail = contactEmail,
                ManualContactEmail = manualContactEmail,
                EffectiveContactEmail = effectiveEmail,
                DetectedLanguage = reader.GetString(reader.GetOrdinal("DetectedLanguage")),
                FeedUrl = reader.GetString(reader.GetOrdinal("FeedUrl")),
                Score = reader.GetDouble(reader.GetOrdinal("Score")),
                Reach = reader.GetDouble(reader.GetOrdinal("Reach")),
                Frequency = reader.GetDouble(reader.GetOrdinal("Frequency")),
                NicheFit = reader.GetDouble(reader.GetOrdinal("NicheFit")),
                ActivityScore = reader.GetDouble(reader.GetOrdinal("ActivityScore")),
                OutreachPriority = reader.GetString(reader.GetOrdinal("OutreachPriority")),
                NewestEpisodePublishedAtUtc = ReadDateTimeOffset(reader, "NewestEpisodePublishedAtUtc"),
                RecentEpisodeTitles = recentTitles,
                NicheFitBreakdownJson = reader.GetString(reader.GetOrdinal("NicheFitBreakdownJson")),
                State = state,
                CooldownUntilUtc = cooldownUntil,
                SnoozeUntilUtc = snoozeUntil,
                ContactedAtUtc = ReadDateTimeOffset(reader, "ContactedAtUtc"),
                Tags = reader.GetString(reader.GetOrdinal("Tags")),
                Note = reader.GetString(reader.GetOrdinal("Note"))
            });
        }

        return queue;
    }

    public async Task<IReadOnlyList<HistoryEvent>> GetHistoryAsync(string? identityFilter = null, CancellationToken cancellationToken = default)
    {
        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();

        command.CommandText = """
            SELECT
                e.Id,
                e.IdentityKey,
                e.EventType,
                e.FromState,
                e.ToState,
                e.OccurredAtUtc,
                COALESCE(e.Note, '') AS Note,
                COALESCE(e.Tags, '') AS Tags,
                ts.ShowName,
                COALESCE(ts.ManualContactEmail, ts.ContactEmail, '') AS EffectiveContactEmail
            FROM TargetStateEvents e
            LEFT JOIN TargetStates ts ON ts.IdentityKey = e.IdentityKey
            WHERE (@identityFilter = '' OR e.IdentityKey = @identityFilter)
            ORDER BY e.OccurredAtUtc DESC, e.Id DESC
            """;
        command.Parameters.AddWithValue("@identityFilter", identityFilter ?? string.Empty);

        var events = new List<HistoryEvent>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            events.Add(new HistoryEvent
            {
                Id = reader.GetInt64(reader.GetOrdinal("Id")),
                IdentityKey = reader.GetString(reader.GetOrdinal("IdentityKey")),
                EventType = reader.GetString(reader.GetOrdinal("EventType")),
                FromState = reader.GetString(reader.GetOrdinal("FromState")),
                ToState = reader.GetString(reader.GetOrdinal("ToState")),
                OccurredAtUtc = DateTimeOffset.Parse(reader.GetString(reader.GetOrdinal("OccurredAtUtc")), CultureInfo.InvariantCulture),
                Note = reader.GetString(reader.GetOrdinal("Note")),
                Tags = reader.GetString(reader.GetOrdinal("Tags")),
                ShowName = ReadNullableString(reader, "ShowName"),
                EffectiveContactEmail = ReadNullableString(reader, "EffectiveContactEmail")
            });
        }

        return events;
    }

    public Task MarkContactedAsync(QueueItem item, string tags, string note, CancellationToken cancellationToken = default)
    {
        var now = _timeProvider.GetUtcNow();
        return ApplyStateChangeAsync(
            item.IdentityKey,
            OutreachState.ContactedWaiting,
            "MarkedContacted",
            now,
            tags,
            note,
            cooldownUntilUtc: now.AddDays(CooldownDays),
            snoozeUntilUtc: null,
            contactedAtUtc: now,
            manualContactEmail: item.ManualContactEmail,
            cancellationToken: cancellationToken);
    }

    public Task MarkRepliedYesAsync(QueueItem item, string tags, string note, CancellationToken cancellationToken = default)
    {
        return ApplyStateChangeAsync(
            item.IdentityKey,
            OutreachState.RepliedYes,
            "MarkedRepliedYes",
            _timeProvider.GetUtcNow(),
            tags,
            note,
            cooldownUntilUtc: null,
            snoozeUntilUtc: null,
            contactedAtUtc: item.ContactedAtUtc,
            manualContactEmail: item.ManualContactEmail,
            cancellationToken: cancellationToken);
    }

    public Task MarkRepliedNoAsync(QueueItem item, string tags, string note, CancellationToken cancellationToken = default)
    {
        return ApplyStateChangeAsync(
            item.IdentityKey,
            OutreachState.RepliedNo,
            "MarkedRepliedNo",
            _timeProvider.GetUtcNow(),
            tags,
            note,
            cooldownUntilUtc: null,
            snoozeUntilUtc: null,
            contactedAtUtc: item.ContactedAtUtc,
            manualContactEmail: item.ManualContactEmail,
            cancellationToken: cancellationToken);
    }

    public Task MarkSnoozedAsync(QueueItem item, DateTimeOffset snoozeUntilUtc, string tags, string note, CancellationToken cancellationToken = default)
    {
        return ApplyStateChangeAsync(
            item.IdentityKey,
            OutreachState.Snoozed,
            "MarkedSnoozed",
            _timeProvider.GetUtcNow(),
            tags,
            note,
            cooldownUntilUtc: null,
            snoozeUntilUtc: snoozeUntilUtc,
            contactedAtUtc: item.ContactedAtUtc,
            manualContactEmail: item.ManualContactEmail,
            cancellationToken: cancellationToken);
    }

    public async Task SaveAnnotationAsync(QueueItem item, string tags, string note, string? manualContactEmail, CancellationToken cancellationToken = default)
    {
        var now = _timeProvider.GetUtcNow();
        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken);

        var currentState = await GetCurrentStateAsync(connection, transaction, item.IdentityKey, cancellationToken);
        var stateValue = currentState?.State ?? OutreachState.New;
        var contactEmail = string.IsNullOrWhiteSpace(item.ContactEmail) ? null : item.ContactEmail;
        var normalizedManual = string.IsNullOrWhiteSpace(manualContactEmail) ? null : TargetIdentityResolver.NormalizeEmail(manualContactEmail);

        await UpsertTargetStateAsync(
            connection,
            transaction,
            item.IdentityKey,
            item.ShowName,
            contactEmail,
            stateValue,
            currentState?.CooldownUntilUtc,
            currentState?.SnoozeUntilUtc,
            currentState?.ContactedAtUtc,
            normalizedManual,
            tags,
            note,
            now,
            cancellationToken);

        await InsertStateEventAsync(
            connection,
            transaction,
            item.IdentityKey,
            "AnnotationUpdated",
            stateValue.ToString(),
            stateValue.ToString(),
            now,
            note,
            tags,
            cancellationToken);

        await transaction.CommitAsync(cancellationToken);
    }

    private async Task ApplyStateChangeAsync(
        string identityKey,
        OutreachState toState,
        string eventType,
        DateTimeOffset occurredAtUtc,
        string tags,
        string note,
        DateTimeOffset? cooldownUntilUtc,
        DateTimeOffset? snoozeUntilUtc,
        DateTimeOffset? contactedAtUtc,
        string? manualContactEmail,
        CancellationToken cancellationToken)
    {
        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken);

        var currentState = await GetCurrentStateAsync(connection, transaction, identityKey, cancellationToken);
        var fromState = currentState?.State ?? OutreachState.New;

        await UpsertTargetStateAsync(
            connection,
            transaction,
            identityKey,
            currentState?.ShowName ?? string.Empty,
            currentState?.ContactEmail,
            toState,
            cooldownUntilUtc,
            snoozeUntilUtc,
            contactedAtUtc,
            string.IsNullOrWhiteSpace(manualContactEmail) ? currentState?.ManualContactEmail : TargetIdentityResolver.NormalizeEmail(manualContactEmail),
            tags,
            note,
            occurredAtUtc,
            cancellationToken);

        await InsertStateEventAsync(
            connection,
            transaction,
            identityKey,
            eventType,
            fromState.ToString(),
            toState.ToString(),
            occurredAtUtc,
            note,
            tags,
            cancellationToken);

        await transaction.CommitAsync(cancellationToken);
    }

    private async Task<long> InsertRunAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        CliOutputEnvelope envelope,
        string commandPreview,
        string stdout,
        string stderr,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO Runs(GeneratedAtUtc, CommandPreview, ArgumentsJson, Total, WarningsJson, StdOut, StdErr)
            VALUES(@generatedAtUtc, @commandPreview, @argumentsJson, @total, @warningsJson, @stdOut, @stdErr);
            SELECT last_insert_rowid();
            """;
        command.Parameters.AddWithValue("@generatedAtUtc", envelope.GeneratedAtUtc.ToString("O"));
        command.Parameters.AddWithValue("@commandPreview", commandPreview);
        command.Parameters.AddWithValue("@argumentsJson", JsonSerializer.Serialize(envelope.Arguments));
        command.Parameters.AddWithValue("@total", envelope.Total);
        command.Parameters.AddWithValue("@warningsJson", JsonSerializer.Serialize(envelope.Warnings));
        command.Parameters.AddWithValue("@stdOut", stdout);
        command.Parameters.AddWithValue("@stdErr", stderr);

        var result = await command.ExecuteScalarAsync(cancellationToken);
        return Convert.ToInt64(result, CultureInfo.InvariantCulture);
    }

    private async Task InsertRunTargetAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        long runId,
        string identity,
        CliOutputResultItem item,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO RunTargets(
                RunId, IdentityKey, ShowId, ShowName, DetectedLanguage, FeedUrl, ContactEmail,
                Reach, Frequency, NicheFit, ActivityScore, OutreachPriority, Score,
                NewestEpisodePublishedAtUtc, RecentEpisodeTitlesJson, NicheFitBreakdownJson)
            VALUES(
                @runId, @identityKey, @showId, @showName, @detectedLanguage, @feedUrl, @contactEmail,
                @reach, @frequency, @nicheFit, @activityScore, @outreachPriority, @score,
                @newestEpisodePublishedAtUtc, @recentEpisodeTitlesJson, @nicheFitBreakdownJson)
            """;

        command.Parameters.AddWithValue("@runId", runId);
        command.Parameters.AddWithValue("@identityKey", identity);
        command.Parameters.AddWithValue("@showId", item.ShowId);
        command.Parameters.AddWithValue("@showName", item.ShowName);
        command.Parameters.AddWithValue("@detectedLanguage", item.DetectedLanguage);
        command.Parameters.AddWithValue("@feedUrl", item.FeedUrl);
        var normalizedContactEmail = ToNullIfWhitespace(TargetIdentityResolver.NormalizeEmail(item.ContactEmail));
        command.Parameters.AddWithValue("@contactEmail", (object?)normalizedContactEmail ?? DBNull.Value);
        command.Parameters.AddWithValue("@reach", item.Reach);
        command.Parameters.AddWithValue("@frequency", item.Frequency);
        command.Parameters.AddWithValue("@nicheFit", item.NicheFit);
        command.Parameters.AddWithValue("@activityScore", item.ActivityScore);
        command.Parameters.AddWithValue("@outreachPriority", item.OutreachPriority);
        command.Parameters.AddWithValue("@score", item.Score);
        command.Parameters.AddWithValue("@newestEpisodePublishedAtUtc", item.NewestEpisodePublishedAtUtc?.ToString("O") ?? (object)DBNull.Value);
        command.Parameters.AddWithValue("@recentEpisodeTitlesJson", JsonSerializer.Serialize(item.RecentEpisodeTitles));
        command.Parameters.AddWithValue("@nicheFitBreakdownJson", JsonSerializer.Serialize(item.NicheFitBreakdown));

        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private async Task EnsureTargetStateRowAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string identity,
        string showName,
        string? contactEmail,
        DateTimeOffset seenAtUtc,
        CancellationToken cancellationToken)
    {
        var current = await GetCurrentStateAsync(connection, transaction, identity, cancellationToken);
        if (current is null)
        {
            await UpsertTargetStateAsync(
                connection,
                transaction,
                identity,
                showName,
                ToNullIfWhitespace(TargetIdentityResolver.NormalizeEmail(contactEmail)),
                OutreachState.New,
                cooldownUntilUtc: null,
                snoozeUntilUtc: null,
                contactedAtUtc: null,
                manualContactEmail: null,
                tags: string.Empty,
                note: string.Empty,
                seenAtUtc,
                cancellationToken);
            return;
        }

        var updatedContactEmail = string.IsNullOrWhiteSpace(current.ContactEmail)
            ? ToNullIfWhitespace(TargetIdentityResolver.NormalizeEmail(contactEmail))
            : current.ContactEmail;
        var updatedShowName = string.IsNullOrWhiteSpace(current.ShowName) ? showName : current.ShowName;

        await UpsertTargetStateAsync(
            connection,
            transaction,
            identity,
            updatedShowName,
            updatedContactEmail,
            current.State,
            current.CooldownUntilUtc,
            current.SnoozeUntilUtc,
            current.ContactedAtUtc,
            current.ManualContactEmail,
            current.Tags,
            current.Note,
            seenAtUtc,
            cancellationToken);
    }

    private async Task<TargetStateRow?> GetCurrentStateAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string identityKey,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT IdentityKey, ShowName, ContactEmail, State, CooldownUntilUtc, SnoozeUntilUtc, ContactedAtUtc,
                   ManualContactEmail, Tags, Note
            FROM TargetStates
            WHERE IdentityKey = @identityKey
            LIMIT 1
            """;
        command.Parameters.AddWithValue("@identityKey", identityKey);

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            return null;
        }

        return new TargetStateRow
        {
            IdentityKey = reader.GetString(reader.GetOrdinal("IdentityKey")),
            ShowName = ReadNullableString(reader, "ShowName"),
            ContactEmail = ReadNullableString(reader, "ContactEmail"),
            State = ParseState(reader.GetString(reader.GetOrdinal("State"))),
            CooldownUntilUtc = ReadDateTimeOffset(reader, "CooldownUntilUtc"),
            SnoozeUntilUtc = ReadDateTimeOffset(reader, "SnoozeUntilUtc"),
            ContactedAtUtc = ReadDateTimeOffset(reader, "ContactedAtUtc"),
            ManualContactEmail = ReadNullableString(reader, "ManualContactEmail"),
            Tags = reader.GetString(reader.GetOrdinal("Tags")),
            Note = reader.GetString(reader.GetOrdinal("Note"))
        };
    }

    private static async Task UpsertTargetStateAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string identityKey,
        string showName,
        string? contactEmail,
        OutreachState state,
        DateTimeOffset? cooldownUntilUtc,
        DateTimeOffset? snoozeUntilUtc,
        DateTimeOffset? contactedAtUtc,
        string? manualContactEmail,
        string tags,
        string note,
        DateTimeOffset lastSeenAtUtc,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO TargetStates(
                IdentityKey, ShowName, ContactEmail, State, CooldownUntilUtc, SnoozeUntilUtc,
                ContactedAtUtc, ManualContactEmail, Tags, Note, LastSeenAtUtc, UpdatedAtUtc)
            VALUES(
                @identityKey, @showName, @contactEmail, @state, @cooldownUntilUtc, @snoozeUntilUtc,
                @contactedAtUtc, @manualContactEmail, @tags, @note, @lastSeenAtUtc, @updatedAtUtc)
            ON CONFLICT(IdentityKey) DO UPDATE SET
                ShowName = excluded.ShowName,
                ContactEmail = COALESCE(TargetStates.ContactEmail, excluded.ContactEmail),
                State = excluded.State,
                CooldownUntilUtc = excluded.CooldownUntilUtc,
                SnoozeUntilUtc = excluded.SnoozeUntilUtc,
                ContactedAtUtc = excluded.ContactedAtUtc,
                ManualContactEmail = excluded.ManualContactEmail,
                Tags = excluded.Tags,
                Note = excluded.Note,
                LastSeenAtUtc = excluded.LastSeenAtUtc,
                UpdatedAtUtc = excluded.UpdatedAtUtc
            """;
        command.Parameters.AddWithValue("@identityKey", identityKey);
        command.Parameters.AddWithValue("@showName", showName);
        command.Parameters.AddWithValue("@contactEmail", (object?)contactEmail ?? DBNull.Value);
        command.Parameters.AddWithValue("@state", state.ToString());
        command.Parameters.AddWithValue("@cooldownUntilUtc", cooldownUntilUtc?.ToString("O") ?? (object)DBNull.Value);
        command.Parameters.AddWithValue("@snoozeUntilUtc", snoozeUntilUtc?.ToString("O") ?? (object)DBNull.Value);
        command.Parameters.AddWithValue("@contactedAtUtc", contactedAtUtc?.ToString("O") ?? (object)DBNull.Value);
        command.Parameters.AddWithValue("@manualContactEmail", (object?)manualContactEmail ?? DBNull.Value);
        command.Parameters.AddWithValue("@tags", tags);
        command.Parameters.AddWithValue("@note", note);
        command.Parameters.AddWithValue("@lastSeenAtUtc", lastSeenAtUtc.ToString("O"));
        command.Parameters.AddWithValue("@updatedAtUtc", lastSeenAtUtc.ToString("O"));

        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task InsertStateEventAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string identityKey,
        string eventType,
        string fromState,
        string toState,
        DateTimeOffset occurredAtUtc,
        string note,
        string tags,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO TargetStateEvents(
                IdentityKey, EventType, FromState, ToState, OccurredAtUtc, Note, Tags)
            VALUES(
                @identityKey, @eventType, @fromState, @toState, @occurredAtUtc, @note, @tags)
            """;
        command.Parameters.AddWithValue("@identityKey", identityKey);
        command.Parameters.AddWithValue("@eventType", eventType);
        command.Parameters.AddWithValue("@fromState", fromState);
        command.Parameters.AddWithValue("@toState", toState);
        command.Parameters.AddWithValue("@occurredAtUtc", occurredAtUtc.ToString("O"));
        command.Parameters.AddWithValue("@note", note);
        command.Parameters.AddWithValue("@tags", tags);

        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private void EnsureInitialized()
    {
        using var connection = new SqliteConnection(_connectionString);
        connection.Open();

        using var command = connection.CreateCommand();
        command.CommandText = """
            CREATE TABLE IF NOT EXISTS Runs (
                Id INTEGER PRIMARY KEY AUTOINCREMENT,
                GeneratedAtUtc TEXT NOT NULL,
                CommandPreview TEXT NOT NULL,
                ArgumentsJson TEXT NOT NULL,
                Total INTEGER NOT NULL,
                WarningsJson TEXT NOT NULL,
                StdOut TEXT NOT NULL,
                StdErr TEXT NOT NULL
            );

            CREATE TABLE IF NOT EXISTS RunTargets (
                Id INTEGER PRIMARY KEY AUTOINCREMENT,
                RunId INTEGER NOT NULL,
                IdentityKey TEXT NOT NULL,
                ShowId TEXT NOT NULL,
                ShowName TEXT NOT NULL,
                DetectedLanguage TEXT NOT NULL,
                FeedUrl TEXT NOT NULL,
                ContactEmail TEXT NULL,
                Reach REAL NOT NULL,
                Frequency REAL NOT NULL,
                NicheFit REAL NOT NULL,
                ActivityScore REAL NOT NULL,
                OutreachPriority TEXT NOT NULL,
                Score REAL NOT NULL,
                NewestEpisodePublishedAtUtc TEXT NULL,
                RecentEpisodeTitlesJson TEXT NOT NULL,
                NicheFitBreakdownJson TEXT NOT NULL,
                FOREIGN KEY(RunId) REFERENCES Runs(Id)
            );

            CREATE TABLE IF NOT EXISTS TargetStates (
                IdentityKey TEXT PRIMARY KEY,
                ShowName TEXT NULL,
                ContactEmail TEXT NULL,
                State TEXT NOT NULL,
                CooldownUntilUtc TEXT NULL,
                SnoozeUntilUtc TEXT NULL,
                ContactedAtUtc TEXT NULL,
                ManualContactEmail TEXT NULL,
                Tags TEXT NOT NULL,
                Note TEXT NOT NULL,
                LastSeenAtUtc TEXT NOT NULL,
                UpdatedAtUtc TEXT NOT NULL
            );

            CREATE TABLE IF NOT EXISTS TargetStateEvents (
                Id INTEGER PRIMARY KEY AUTOINCREMENT,
                IdentityKey TEXT NOT NULL,
                EventType TEXT NOT NULL,
                FromState TEXT NOT NULL,
                ToState TEXT NOT NULL,
                OccurredAtUtc TEXT NOT NULL,
                Note TEXT NOT NULL,
                Tags TEXT NOT NULL
            );

            CREATE INDEX IF NOT EXISTS IX_RunTargets_RunId ON RunTargets(RunId);
            CREATE INDEX IF NOT EXISTS IX_RunTargets_IdentityKey ON RunTargets(IdentityKey);
            CREATE INDEX IF NOT EXISTS IX_TargetStateEvents_IdentityKey ON TargetStateEvents(IdentityKey);
            CREATE INDEX IF NOT EXISTS IX_TargetStateEvents_OccurredAtUtc ON TargetStateEvents(OccurredAtUtc DESC);
            """;
        command.ExecuteNonQuery();
    }

    private static bool IsContactable(OutreachState state, DateTimeOffset? cooldownUntilUtc, DateTimeOffset? snoozeUntilUtc, DateTimeOffset now)
    {
        if (state == OutreachState.RepliedNo || state == OutreachState.RepliedYes)
        {
            return false;
        }

        if (state == OutreachState.ContactedWaiting && cooldownUntilUtc.HasValue && cooldownUntilUtc.Value > now)
        {
            return false;
        }

        if (state == OutreachState.Snoozed && snoozeUntilUtc.HasValue && snoozeUntilUtc.Value > now)
        {
            return false;
        }

        return true;
    }

    private static OutreachState ParseState(string value)
    {
        return Enum.TryParse<OutreachState>(value, out var parsed) ? parsed : OutreachState.New;
    }

    private static DateTimeOffset? ReadDateTimeOffset(SqliteDataReader reader, string columnName)
    {
        var index = reader.GetOrdinal(columnName);
        if (reader.IsDBNull(index))
        {
            return null;
        }

        return DateTimeOffset.Parse(reader.GetString(index), CultureInfo.InvariantCulture);
    }

    private static string? ReadNullableString(SqliteDataReader reader, string columnName)
    {
        var index = reader.GetOrdinal(columnName);
        return reader.IsDBNull(index) ? null : reader.GetString(index);
    }

    private static string? ToNullIfWhitespace(string? value)
    {
        return string.IsNullOrWhiteSpace(value) ? null : value;
    }

    private sealed class TargetStateRow
    {
        public required string IdentityKey { get; init; }
        public string? ShowName { get; init; }
        public string? ContactEmail { get; init; }
        public OutreachState State { get; init; }
        public DateTimeOffset? CooldownUntilUtc { get; init; }
        public DateTimeOffset? SnoozeUntilUtc { get; init; }
        public DateTimeOffset? ContactedAtUtc { get; init; }
        public string? ManualContactEmail { get; init; }
        public string Tags { get; init; } = string.Empty;
        public string Note { get; init; } = string.Empty;
    }
}
