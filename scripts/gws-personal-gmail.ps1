param(
    [ValidateSet("login", "send-test", "raw")]
    [string]$Action = "raw",

    [string]$To,
    [string]$Subject = "Nudge test email",
    [string]$Body = "Sent from gws in this repo.",
    [string[]]$PassThruArgs
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

$RepoRoot = Split-Path -Path $PSScriptRoot -Parent
$ConfigDir = Join-Path $RepoRoot ".local\gws"
New-Item -ItemType Directory -Path $ConfigDir -Force | Out-Null

# Keep gws credentials/config local to this repo.
$env:GOOGLE_WORKSPACE_CLI_CONFIG_DIR = $ConfigDir

function Invoke-Gws {
    param(
        [Parameter(Mandatory = $true)]
        [string[]]$CommandArgs
    )

    & npx -y @googleworkspace/cli @CommandArgs
    if ($LASTEXITCODE -ne 0) {
        throw "gws command failed with exit code $LASTEXITCODE"
    }
}

switch ($Action) {
    "login" {
        # Personal @gmail.com accounts are more reliable with narrow scopes.
        Invoke-Gws -CommandArgs @("auth", "login", "--scopes", "gmail")
        break
    }
    "send-test" {
        if ([string]::IsNullOrWhiteSpace($To)) {
            throw "Provide -To for send-test."
        }

        Invoke-Gws -CommandArgs @(
            "gmail",
            "+send",
            "--to", $To,
            "--subject", $Subject,
            "--body", $Body
        )
        break
    }
    "raw" {
        if (-not $PassThruArgs -or $PassThruArgs.Count -eq 0) {
            Write-Host "Pass gws arguments with -PassThruArgs, e.g."
            Write-Host '  .\scripts\gws-personal-gmail.ps1 -Action raw -PassThruArgs gmail,+triage'
            exit 1
        }

        Invoke-Gws -CommandArgs $PassThruArgs
        break
    }
}
