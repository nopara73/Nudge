using System.Diagnostics;

namespace Nudge.Ui.Services;

public sealed class LocalEpisodeSttTranscriber(HttpClient httpClient) : ILocalEpisodeSttTranscriber
{
    private readonly HttpClient _httpClient = httpClient;

    public async Task<string?> TranscribeFromAudioUrlAsync(string audioUrl, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(audioUrl))
        {
            return null;
        }

        var commandTemplate = Environment.GetEnvironmentVariable("NUDGE_STT_COMMAND_TEMPLATE");
        if (string.IsNullOrWhiteSpace(commandTemplate))
        {
            return null;
        }

        var tempRoot = Path.Combine(Path.GetTempPath(), "nudge-stt", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempRoot);
        var outputDirectory = Path.Combine(tempRoot, "out");
        Directory.CreateDirectory(outputDirectory);

        var audioPath = Path.Combine(tempRoot, "episode-audio.bin");
        var outputFile = Path.Combine(outputDirectory, "transcript.txt");

        try
        {
            using var audioResponse = await _httpClient.GetAsync(audioUrl, cancellationToken);
            if (!audioResponse.IsSuccessStatusCode)
            {
                return null;
            }

            await using (var stream = await audioResponse.Content.ReadAsStreamAsync(cancellationToken))
            await using (var file = File.Create(audioPath))
            {
                await stream.CopyToAsync(file, cancellationToken);
            }

            var command = commandTemplate
                .Replace("{input}", Quote(audioPath), StringComparison.Ordinal)
                .Replace("{output_dir}", Quote(outputDirectory), StringComparison.Ordinal)
                .Replace("{output_file}", Quote(outputFile), StringComparison.Ordinal);

            var exitCode = await RunShellCommandAsync(command, cancellationToken);
            if (exitCode != 0)
            {
                return null;
            }

            if (File.Exists(outputFile))
            {
                return await File.ReadAllTextAsync(outputFile, cancellationToken);
            }

            var discoveredOutput = Directory.GetFiles(outputDirectory, "*.txt", SearchOption.TopDirectoryOnly).FirstOrDefault();
            if (string.IsNullOrWhiteSpace(discoveredOutput))
            {
                return null;
            }

            return await File.ReadAllTextAsync(discoveredOutput, cancellationToken);
        }
        catch
        {
            return null;
        }
        finally
        {
            try
            {
                if (Directory.Exists(tempRoot))
                {
                    Directory.Delete(tempRoot, recursive: true);
                }
            }
            catch
            {
                // best-effort temp cleanup
            }
        }
    }

    private static async Task<int> RunShellCommandAsync(string command, CancellationToken cancellationToken)
    {
        var (fileName, arguments) = OperatingSystem.IsWindows()
            ? ("cmd.exe", $"/C {command}")
            : ("/bin/bash", $"-lc \"{command.Replace("\"", "\\\"", StringComparison.Ordinal)}\"");

        using var process = new Process();
        process.StartInfo = new ProcessStartInfo
        {
            FileName = fileName,
            Arguments = arguments,
            UseShellExecute = false,
            RedirectStandardError = true,
            RedirectStandardOutput = true,
            CreateNoWindow = true
        };

        process.Start();
        await process.WaitForExitAsync(cancellationToken);
        return process.ExitCode;
    }

    private static string Quote(string value) => $"\"{value}\"";
}
