using System.Text.Json;

namespace Nudge.Ui.Services;

public sealed class SessionStateStore
{
    private readonly string _filePath;

    public SessionStateStore()
    {
        var appDataPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Nudge.Ui");
        Directory.CreateDirectory(appDataPath);
        _filePath = Path.Combine(appDataPath, "session.json");
    }

    public SessionState Load()
    {
        if (!File.Exists(_filePath))
        {
            return new SessionState();
        }

        try
        {
            var json = File.ReadAllText(_filePath);
            return JsonSerializer.Deserialize<SessionState>(json) ?? new SessionState();
        }
        catch
        {
            return new SessionState();
        }
    }

    public void Save(SessionState state)
    {
        var json = JsonSerializer.Serialize(state, new JsonSerializerOptions { WriteIndented = true });
        File.WriteAllText(_filePath, json);
    }
}

public sealed class SessionState
{
    public string LastView { get; init; } = "Run";
    public string LastConfigPath { get; init; } = string.Empty;
}
