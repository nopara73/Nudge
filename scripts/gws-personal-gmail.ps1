param(
    [ValidateSet("login", "send-test", "raw")]
    [string]$Action = "raw",

    [string]$To,
    [string]$Subject = "Nudge test email",
    [string]$Body = "Sent from gws in this repo.",
    [string]$BodyFile,
    [string[]]$PassThruArgs
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"
$GmailScopes = @(
    "https://www.googleapis.com/auth/gmail.send",
    "https://www.googleapis.com/auth/gmail.settings.basic"
)

$RepoRoot = Split-Path -Path $PSScriptRoot -Parent
$ConfigDir = Join-Path $RepoRoot ".local\gws"
New-Item -ItemType Directory -Path $ConfigDir -Force | Out-Null

# Keep gws credentials/config local to this repo.
$env:GOOGLE_WORKSPACE_CLI_CONFIG_DIR = $ConfigDir
$LocalClientSecretPath = Join-Path $ConfigDir "client_secret.json"
$DefaultClientSecretPath = Join-Path $env:USERPROFILE ".config\gws\client_secret.json"

if (-not (Test-Path -LiteralPath $LocalClientSecretPath) -and (Test-Path -LiteralPath $DefaultClientSecretPath)) {
    Copy-Item -LiteralPath $DefaultClientSecretPath -Destination $LocalClientSecretPath -Force
}

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

function ConvertTo-Base64Url {
    param(
        [Parameter(Mandatory = $true)]
        [string]$InputText
    )

    $bytes = [System.Text.Encoding]::UTF8.GetBytes($InputText)
    $b64 = [Convert]::ToBase64String($bytes)
    return $b64.TrimEnd("=").Replace("+", "-").Replace("/", "_")
}

switch ($Action) {
    "login" {
        if (
            -not (Test-Path -LiteralPath $LocalClientSecretPath) -and
            [string]::IsNullOrWhiteSpace($env:GOOGLE_WORKSPACE_CLI_CLIENT_ID) -and
            [string]::IsNullOrWhiteSpace($env:GOOGLE_WORKSPACE_CLI_CLIENT_SECRET)
        ) {
            throw @"
Missing OAuth client configuration.

Because this script uses repo-local config, place your OAuth Desktop client file at:
  $LocalClientSecretPath

Alternative:
  Set GOOGLE_WORKSPACE_CLI_CLIENT_ID and GOOGLE_WORKSPACE_CLI_CLIENT_SECRET.
"@
        }

        # Use explicit OAuth scope URIs because shorthand names like "gmail"
        # can be rejected as invalid_scope for personal accounts.
        Invoke-Gws -CommandArgs @("auth", "login", "--scopes", ($GmailScopes -join ","))
        break
    }
    "send-test" {
        if ([string]::IsNullOrWhiteSpace($To)) {
            throw "Provide -To for send-test."
        }

        if (-not [string]::IsNullOrWhiteSpace($BodyFile)) {
            if (-not (Test-Path -LiteralPath $BodyFile)) {
                throw "BodyFile not found: $BodyFile"
            }
            $Body = Get-Content -LiteralPath $BodyFile -Raw
        }

        $mime = @"
To: $To
Subject: $Subject
Content-Type: text/plain; charset=UTF-8

$Body
"@.Replace("`r`n", "`n")

        $raw = ConvertTo-Base64Url -InputText $mime
        # PowerShell native argument passing can strip unescaped JSON quotes.
        # Keep quotes escaped so gws receives valid JSON text.
        $paramsJson = '{\"userId\":\"me\"}'
        $bodyJson = '{\"raw\":\"' + $raw + '\"}'

        & npx -y @googleworkspace/cli gmail users messages send --params $paramsJson --json $bodyJson
        if ($LASTEXITCODE -ne 0) {
            throw "gws command failed with exit code $LASTEXITCODE"
        }
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
