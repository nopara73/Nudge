param(
    [Parameter(Mandatory = $true)]
    [ValidateSet("verify", "relock")]
    [string]$Mode,

    [string]$TemplatePath = ".local/ai-outreach/email-template.md",
    [string]$LockPath = ".local/ai-outreach/email-template.lock.json",
    [switch]$Force
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

$RepoRoot = Split-Path -Path $PSScriptRoot -Parent

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

function Get-TemplateDigest {
    param(
        [Parameter(Mandatory = $true)]
        [string]$TemplateFile
    )

    if (-not (Test-Path -LiteralPath $TemplateFile)) {
        throw "Template file not found: $TemplateFile"
    }

    $item = Get-Item -LiteralPath $TemplateFile
    $raw = Get-Content -LiteralPath $TemplateFile -Raw
    $lineCount = if ([string]::IsNullOrEmpty($raw)) { 0 } else { ($raw -replace "`r", "" -split "`n").Count }
    $sha256 = (Get-FileHash -LiteralPath $TemplateFile -Algorithm SHA256).Hash.ToLowerInvariant()

    return @{
        sha256 = $sha256
        lineCount = $lineCount
        bytes = [int64]$item.Length
        readOnly = [bool]($item.Attributes -band [System.IO.FileAttributes]::ReadOnly)
    }
}

function Write-LockFile {
    param(
        [Parameter(Mandatory = $true)]
        [string]$LockFile,
        [Parameter(Mandatory = $true)]
        [hashtable]$Digest
    )

    $payload = [ordered]@{
        templateFile = "email-template.md"
        sha256 = $Digest.sha256
        lineCount = $Digest.lineCount
        bytes = $Digest.bytes
        lockedAtUtc = [DateTimeOffset]::UtcNow.ToString("o")
    }
    $payload | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath $LockFile -Encoding UTF8
}

$ResolvedTemplate = Resolve-RepoPath -PathValue $TemplatePath
$ResolvedLock = Resolve-RepoPath -PathValue $LockPath

switch ($Mode) {
    "verify" {
        if (-not (Test-Path -LiteralPath $ResolvedLock)) {
            throw "Template lock file missing: $ResolvedLock"
        }

        $lockRaw = Get-Content -LiteralPath $ResolvedLock -Raw
        if ([string]::IsNullOrWhiteSpace($lockRaw)) {
            throw "Template lock file is empty: $ResolvedLock"
        }

        $lock = $lockRaw | ConvertFrom-Json
        if (-not $lock -or -not $lock.sha256) {
            throw "Template lock file is invalid: $ResolvedLock"
        }

        $digest = Get-TemplateDigest -TemplateFile $ResolvedTemplate
        if ([string]$lock.sha256 -ne [string]$digest.sha256) {
            throw "Template hash mismatch. Expected $($lock.sha256), got $($digest.sha256)."
        }

        if (-not $digest.readOnly) {
            throw "Template file is not read-only: $ResolvedTemplate"
        }

        @{
            ok = $true
            templatePath = $ResolvedTemplate
            lockPath = $ResolvedLock
            sha256 = $digest.sha256
            lineCount = $digest.lineCount
            bytes = $digest.bytes
            readOnly = $digest.readOnly
        } | ConvertTo-Json -Depth 5
        break
    }
    "relock" {
        if (-not $Force) {
            throw "Refusing to relock without -Force."
        }

        $digest = Get-TemplateDigest -TemplateFile $ResolvedTemplate
        Write-LockFile -LockFile $ResolvedLock -Digest $digest

        # Keep lock and template read-only after intentional relock.
        attrib +R $ResolvedTemplate | Out-Null
        attrib +R $ResolvedLock | Out-Null

        @{
            ok = $true
            relocked = $true
            templatePath = $ResolvedTemplate
            lockPath = $ResolvedLock
            sha256 = $digest.sha256
            lineCount = $digest.lineCount
            bytes = $digest.bytes
        } | ConvertTo-Json -Depth 5
        break
    }
}
