param(
    [string]$BatchFile = ".local/ai-outreach/current-batch.json",
    [string]$DbPath = ".local/ai-outreach/ai-outreach.db",
    [string]$BatchName = "ai-outreach-daily"
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

$RepoRoot = Split-Path -Path $PSScriptRoot -Parent
$TrackerScript = Join-Path $PSScriptRoot "ai_outreach_tracker.py"

function Resolve-RepoPath {
    param(
        [Parameter(Mandatory = $true)]
        [string]$PathValue
    )

    if ([System.IO.Path]::IsPathRooted($PathValue)) {
        return [System.IO.Path]::GetFullPath($PathValue)
    }

    return [System.IO.Path]::GetFullPath((Join-Path $RepoRoot $PathValue))
}

function Get-PythonCommand {
    if (Get-Command py -ErrorAction SilentlyContinue) {
        return @{
            Executable = "py"
            BaseArgs = @("-3")
        }
    }

    if (Get-Command python -ErrorAction SilentlyContinue) {
        return @{
            Executable = "python"
            BaseArgs = @()
        }
    }

    throw "Python launcher not found. Install Python or ensure 'py' or 'python' is available."
}

$resolvedBatchFile = Resolve-RepoPath -PathValue $BatchFile
$resolvedDbPath = Resolve-RepoPath -PathValue $DbPath
$pythonCommand = Get-PythonCommand

$output = & $pythonCommand.Executable @($pythonCommand.BaseArgs) $TrackerScript `
    "--db-path" $resolvedDbPath `
    "build-batch-from-pool" `
    "--output-batch-file" $resolvedBatchFile `
    "--batch-name" $BatchName 2>&1

$text = ($output | ForEach-Object { "$_" }) -join [Environment]::NewLine
if ([string]::IsNullOrWhiteSpace($text)) {
    throw "Tracker produced no output."
}

$parsed = $text | ConvertFrom-Json
$parsed | ConvertTo-Json -Depth 8
if ($LASTEXITCODE -ne 0) {
    exit 1
}
