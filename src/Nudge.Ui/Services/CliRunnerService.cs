using System.Diagnostics;
using System.Text.Json;
using Nudge.Ui.Models;

namespace Nudge.Ui.Services;

public sealed class CliRunnerService
{
    public string BuildCommandPreview(RunConfigProfile profile)
    {
        var startInfo = BuildStartInfo(profile);
        return BuildCommandPreview(startInfo);
    }

    public async Task<CliRunResult> RunAsync(RunConfigProfile profile, CancellationToken cancellationToken = default)
    {
        var startInfo = BuildStartInfo(profile);

        using var process = new Process { StartInfo = startInfo };
        process.Start();

        var stdOutTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
        var stdErrTask = process.StandardError.ReadToEndAsync(cancellationToken);
        await process.WaitForExitAsync(cancellationToken);

        var stdOut = await stdOutTask;
        var stdErr = await stdErrTask;

        if (process.ExitCode != 0)
        {
            return CliRunResult.Fail(
                $"CLI exited with code {process.ExitCode}.",
                BuildCommandPreview(startInfo),
                stdOut,
                stdErr);
        }

        if (!TryExtractEnvelope(stdOut, out var envelope, out var parseError))
        {
            return CliRunResult.Fail(
                $"CLI succeeded but JSON output could not be parsed: {parseError}",
                BuildCommandPreview(startInfo),
                stdOut,
                stdErr);
        }

        return CliRunResult.Ok(envelope!, BuildCommandPreview(startInfo), stdOut, stdErr);
    }

    private static ProcessStartInfo BuildStartInfo(RunConfigProfile profile)
    {
        var repoRoot = RepositoryPaths.LocateRepositoryRoot();
        var startInfo = new ProcessStartInfo
        {
            FileName = "dotnet",
            WorkingDirectory = repoRoot,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false
        };

        startInfo.ArgumentList.Add("run");
        startInfo.ArgumentList.Add("--project");
        startInfo.ArgumentList.Add("src/Nudge.Cli");
        startInfo.ArgumentList.Add("--");
        startInfo.ArgumentList.Add("--keywords");
        startInfo.ArgumentList.Add(string.Join(",", profile.Keywords));
        startInfo.ArgumentList.Add("--published-after-days");
        startInfo.ArgumentList.Add(profile.PublishedAfterDays.ToString());
        startInfo.ArgumentList.Add("--top");
        startInfo.ArgumentList.Add(profile.Top.ToString());
        startInfo.ArgumentList.Add("--json");
        startInfo.ArgumentList.Add("--pretty");

        if (profile.UseMock)
        {
            startInfo.ArgumentList.Add("--use-mock");
        }

        if (profile.Verbose)
        {
            startInfo.ArgumentList.Add("--verbose");
        }

        return startInfo;
    }

    private static string BuildCommandPreview(ProcessStartInfo info)
    {
        var parts = new List<string> { info.FileName };
        parts.AddRange(info.ArgumentList.Select(QuoteIfNeeded));
        return string.Join(" ", parts);
    }

    private static string QuoteIfNeeded(string value)
    {
        if (value.Contains(' '))
        {
            return $"\"{value}\"";
        }

        return value;
    }

    private static bool TryExtractEnvelope(string stdout, out CliOutputEnvelope? envelope, out string error)
    {
        var jsonStart = stdout.IndexOf('{');
        if (jsonStart < 0)
        {
            envelope = null;
            error = "No JSON object found in stdout.";
            return false;
        }

        var json = stdout[jsonStart..].Trim();
        try
        {
            envelope = JsonSerializer.Deserialize<CliOutputEnvelope>(
                json,
                new JsonSerializerOptions
                {
                    PropertyNameCaseInsensitive = true
                });

            if (envelope is null)
            {
                error = "Deserialized envelope was null.";
                return false;
            }

            error = string.Empty;
            return true;
        }
        catch (JsonException ex)
        {
            envelope = null;
            error = ex.Message;
            return false;
        }
    }
}

public sealed class CliRunResult
{
    public bool Success { get; private init; }
    public string ErrorMessage { get; private init; } = string.Empty;
    public string CommandPreview { get; private init; } = string.Empty;
    public string StdOut { get; private init; } = string.Empty;
    public string StdErr { get; private init; } = string.Empty;
    public CliOutputEnvelope? Envelope { get; private init; }

    public static CliRunResult Ok(CliOutputEnvelope envelope, string commandPreview, string stdOut, string stdErr) =>
        new()
        {
            Success = true,
            Envelope = envelope,
            CommandPreview = commandPreview,
            StdOut = stdOut,
            StdErr = stdErr
        };

    public static CliRunResult Fail(string errorMessage, string commandPreview, string stdOut, string stdErr) =>
        new()
        {
            Success = false,
            ErrorMessage = errorMessage,
            CommandPreview = commandPreview,
            StdOut = stdOut,
            StdErr = stdErr
        };
}
