param(
    [Parameter(Mandatory = $true)]
    [ValidateSet("preview", "send-confirmed")]
    [string]$Mode,

    [Parameter(Mandatory = $true)]
    [string]$BatchFile,

    [string]$ApprovalToken,

    [string]$DbPath = ".local/ai-outreach/ai-outreach.db",

    [switch]$Simulate,

    [string[]]$SimulateFailureEmails = @()
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

$RepoRoot = Split-Path -Path $PSScriptRoot -Parent
$TrackerScript = Join-Path $PSScriptRoot "ai_outreach_tracker.py"
$GmailScript = Join-Path $PSScriptRoot "gws-personal-gmail.ps1"
$ArtifactsDir = Join-Path $RepoRoot ".local/ai-outreach"
$TemplatePath = Join-Path $ArtifactsDir "email-template.md"
$TemplateLockPath = Join-Path $ArtifactsDir "email-template.lock.json"
$SendLedgerPath = Join-Path $ArtifactsDir "send-ledger.json"
New-Item -ItemType Directory -Path $ArtifactsDir -Force | Out-Null

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

function Get-TemplateParts {
    param(
        [Parameter(Mandatory = $true)]
        [string]$TemplateFile
    )

    if (-not (Test-Path -LiteralPath $TemplateFile)) {
        throw "Email template not found: $TemplateFile"
    }

    $content = Get-Content -LiteralPath $TemplateFile -Raw
    if ([string]::IsNullOrWhiteSpace($content)) {
        throw "Email template is empty: $TemplateFile"
    }

    $normalized = $content -replace "`r", ""
    $subjectMatch = [regex]::Match(
        $normalized,
        "(?ms)^## Subject Template\s*(?<subject>.*?)^\s*## Body Template\s*"
    )
    if (-not $subjectMatch.Success) {
        throw "Email template missing expected Subject/Body sections."
    }

    $subjectLines = @($subjectMatch.Groups["subject"].Value -split "`n" | ForEach-Object { $_.Trim() } | Where-Object { $_ -ne "" })
    if ($subjectLines.Count -eq 0) {
        throw "Email template subject section is empty."
    }
    $subjectTemplate = $subjectLines[-1]

    $bodyMatch = [regex]::Match(
        $normalized,
        "(?ms)^## Body Template\s*(?<body>.*)$"
    )
    if (-not $bodyMatch.Success) {
        throw "Email template body section is missing."
    }
    $bodyTemplate = $bodyMatch.Groups["body"].Value.Trim()
    if ([string]::IsNullOrWhiteSpace($bodyTemplate)) {
        throw "Email template body section is empty."
    }

    return @{
        SubjectTemplate = $subjectTemplate
        BodyTemplate = $bodyTemplate
    }
}

function Get-TemplateDigest {
    param(
        [Parameter(Mandatory = $true)]
        [string]$TemplateFile
    )

    if (-not (Test-Path -LiteralPath $TemplateFile)) {
        throw "Email template not found: $TemplateFile"
    }

    $item = Get-Item -LiteralPath $TemplateFile
    $raw = Get-Content -LiteralPath $TemplateFile -Raw
    $lineCount = if ([string]::IsNullOrEmpty($raw)) { 0 } else { ($raw -replace "`r", "" -split "`n").Count }
    $sha256 = (Get-FileHash -LiteralPath $TemplateFile -Algorithm SHA256).Hash.ToLowerInvariant()
    return @{
        sha256 = $sha256
        lineCount = $lineCount
        bytes = [int64]$item.Length
    }
}

function Assert-TemplateIntegrity {
    param(
        [Parameter(Mandatory = $true)]
        [string]$TemplateFile,
        [Parameter(Mandatory = $true)]
        [string]$TemplateLockFile
    )

    if (-not (Test-Path -LiteralPath $TemplateLockFile)) {
        throw "Template lock file missing: $TemplateLockFile. Recreate it intentionally before running preview/send."
    }

    $lockRaw = Get-Content -LiteralPath $TemplateLockFile -Raw
    if ([string]::IsNullOrWhiteSpace($lockRaw)) {
        throw "Template lock file is empty: $TemplateLockFile"
    }

    $lock = $lockRaw | ConvertFrom-Json
    if (-not $lock -or -not $lock.sha256) {
        throw "Template lock file is invalid: $TemplateLockFile"
    }

    $digest = Get-TemplateDigest -TemplateFile $TemplateFile
    if ([string]$lock.sha256 -ne [string]$digest.sha256) {
        throw "Template integrity violation: email-template.md does not match locked hash. Aborting preview/send."
    }

    $templateItem = Get-Item -LiteralPath $TemplateFile
    if (-not ($templateItem.Attributes -band [System.IO.FileAttributes]::ReadOnly)) {
        throw "Template protection violation: email-template.md must be read-only."
    }
}

function Get-PersonFirstName {
    param(
        [string]$PersonName
    )

    if ([string]::IsNullOrWhiteSpace($PersonName)) {
        return "there"
    }

    $firstToken = ($PersonName -split "\s+" | Where-Object { $_ -ne "" } | Select-Object -First 1)
    if ([string]::IsNullOrWhiteSpace($firstToken)) {
        return "there"
    }

    return $firstToken
}

function Test-IsTemplatePendingValue {
    param(
        [string]$Value
    )

    if ([string]::IsNullOrWhiteSpace($Value)) {
        return $true
    }

    return ([string]$Value).Trim().ToUpperInvariant() -eq "TEMPLATE_PENDING"
}

function Normalize-TemplateText {
    param(
        [string]$Value
    )

    if ($null -eq $Value) {
        return ""
    }

    return ([string]$Value) -replace "`r", ""
}

function Apply-EmailTemplateToBatchFile {
    param(
        [Parameter(Mandatory = $true)]
        [string]$BatchPath,
        [Parameter(Mandatory = $true)]
        [string]$TemplateFile
    )

    $template = Get-TemplateParts -TemplateFile $TemplateFile
    $batch = Get-Content -LiteralPath $BatchPath -Raw | ConvertFrom-Json
    if (-not $batch -or -not ($batch.PSObject.Properties.Name -contains "items")) {
        throw "Batch JSON must include an items array."
    }

    $changed = $false
    foreach ($item in $batch.items) {
        $personFirstName = Get-PersonFirstName -PersonName ([string]$item.personName)
        $expectedSubject = ([string]$template.SubjectTemplate).Replace("{companyName}", [string]$item.companyName)
        $expectedBody = ([string]$template.BodyTemplate).
            Replace("{companyName}", [string]$item.companyName).
            Replace("{personFirstName}", [string]$personFirstName)

        $currentSubject = [string]$item.subject
        $currentBody = [string]$item.body
        $subjectNeedsTemplate = Test-IsTemplatePendingValue -Value $currentSubject
        $bodyNeedsTemplate = Test-IsTemplatePendingValue -Value $currentBody

        # Enforce template lock: custom authored copy is rejected.
        if ($subjectNeedsTemplate) {
            $item.subject = $expectedSubject
            $changed = $true
        }
        elseif ((Normalize-TemplateText -Value $currentSubject) -ne (Normalize-TemplateText -Value $expectedSubject)) {
            throw "Template lock violation: subject must stay template-derived for '$([string]$item.companyName)' <$([string]$item.email)>."
        }
        if ($bodyNeedsTemplate) {
            $item.body = $expectedBody
            $changed = $true
        }
        elseif ((Normalize-TemplateText -Value $currentBody) -ne (Normalize-TemplateText -Value $expectedBody)) {
            throw "Template lock violation: body must stay template-derived for '$([string]$item.companyName)' <$([string]$item.email)>."
        }
    }

    if ($changed) {
        $batch.generatedAtUtc = [DateTimeOffset]::UtcNow.ToString("o")
        $batch | ConvertTo-Json -Depth 12 | Set-Content -LiteralPath $BatchPath -Encoding UTF8
    }
}

function Invoke-Tracker {
    param(
        [Parameter(Mandatory = $true)]
        [string[]]$TrackerArgs,

        [switch]$AllowFailure
    )

    $pythonCommand = Get-PythonCommand
    $output = & $pythonCommand.Executable @($pythonCommand.BaseArgs) $TrackerScript "--db-path" $ResolvedDbPath @TrackerArgs 2>&1
    $text = ($output | ForEach-Object { "$_" }) -join [Environment]::NewLine
    if ([string]::IsNullOrWhiteSpace($text)) {
        throw "Tracker produced no output."
    }

    $parsed = $text | ConvertFrom-Json
    if ($LASTEXITCODE -ne 0) {
        if ($AllowFailure) {
            return $parsed
        }

        $message = if ($parsed -and ($parsed.PSObject.Properties.Name -contains "error")) { $parsed.error } else { $text }
        throw "Tracker command failed: $message"
    }

    return $parsed
}

function Invoke-GmailSend {
    param(
        [Parameter(Mandatory = $true)]
        [object]$Item
    )

    if ($Simulate) {
        if ($SimulateFailureEmails -contains [string]$Item.email) {
            return [pscustomobject]@{
                email = $Item.email
                status = "failed"
                error = "Simulated send failure."
            }
        }

        return @{
            email = $Item.email
            status = "sent"
            gmailResult = @{
                id = "simulated-$([guid]::NewGuid().ToString('N'))"
                threadId = "simulated-thread-$([guid]::NewGuid().ToString('N'))"
                simulated = $true
            }
        }
    }

    $tempOut = Join-Path $ArtifactsDir "gmail-send-$([guid]::NewGuid().ToString('N')).out.log"
    $tempErr = Join-Path $ArtifactsDir "gmail-send-$([guid]::NewGuid().ToString('N')).err.log"
    $tempBody = Join-Path $ArtifactsDir "gmail-send-$([guid]::NewGuid().ToString('N')).body.txt"
    $sendExitCode = 1
    $text = ""

    try {
        $rawBody = [string]$Item.body
        # Normalize accidental escaped newlines so outbound email stays readable.
        $normalizedBody = $rawBody -replace "\\r\\n", "`n" -replace "\\n", "`n"
        Set-Content -LiteralPath $tempBody -Value $normalizedBody -Encoding UTF8

        $argList = @(
            "-NoProfile",
            "-ExecutionPolicy", "Bypass",
            "-File", $GmailScript,
            "-Action", "send-test",
            "-To", [string]$Item.email,
            "-Subject", [string]$Item.subject,
            "-BodyFile", $tempBody
        )

        # Invoke directly with splatted args so values like subject lines
        # are preserved as single arguments (no token truncation on spaces).
        & powershell @argList 1> $tempOut 2> $tempErr
        $sendExitCode = [int]$LASTEXITCODE

        $stdout = if (Test-Path -LiteralPath $tempOut) {
            Get-Content -LiteralPath $tempOut -Raw
        } else {
            ""
        }
        $stderr = if (Test-Path -LiteralPath $tempErr) {
            Get-Content -LiteralPath $tempErr -Raw
        } else {
            ""
        }
        $text = (@($stdout, $stderr) | Where-Object { -not [string]::IsNullOrWhiteSpace($_) }) -join [Environment]::NewLine
    }
    finally {
        if (Test-Path -LiteralPath $tempOut) { Remove-Item -LiteralPath $tempOut -Force -ErrorAction SilentlyContinue }
        if (Test-Path -LiteralPath $tempErr) { Remove-Item -LiteralPath $tempErr -Force -ErrorAction SilentlyContinue }
        if (Test-Path -LiteralPath $tempBody) { Remove-Item -LiteralPath $tempBody -Force -ErrorAction SilentlyContinue }
    }

    if ($sendExitCode -ne 0) {
        return @{
            email = $Item.email
            status = "failed"
            error = $text
        }
    }

    try {
        $json = $text | ConvertFrom-Json
    }
    catch {
        $json = [pscustomobject]@{
            rawOutput = $text
        }
    }

    return [pscustomobject]@{
        email = $Item.email
        status = "sent"
        gmailResult = $json
    }
}

function Get-HashSha256Hex {
    param(
        [Parameter(Mandatory = $true)]
        [string]$InputText
    )

    $sha = [System.Security.Cryptography.SHA256]::Create()
    try {
        $bytes = [System.Text.Encoding]::UTF8.GetBytes($InputText)
        $hash = $sha.ComputeHash($bytes)
        return ([System.BitConverter]::ToString($hash)).Replace("-", "").ToLowerInvariant()
    }
    finally {
        $sha.Dispose()
    }
}

function Get-ItemFingerprint {
    param(
        [Parameter(Mandatory = $true)]
        [object]$Item
    )

    $email = ([string]$Item.email).Trim().ToLowerInvariant()
    $subject = [string]$Item.subject
    $body = [string]$Item.body
    return Get-HashSha256Hex -InputText ($email + "`n---`n" + $subject + "`n---`n" + $body)
}

function Load-SendLedger {
    param(
        [Parameter(Mandatory = $true)]
        [string]$LedgerPath
    )

    if (-not (Test-Path -LiteralPath $LedgerPath)) {
        return @{
            version = 1
            entries = @()
        }
    }

    $raw = Get-Content -LiteralPath $LedgerPath -Raw
    if ([string]::IsNullOrWhiteSpace($raw)) {
        return @{
            version = 1
            entries = @()
        }
    }

    $parsed = $raw | ConvertFrom-Json
    if (-not $parsed.PSObject.Properties.Name.Contains("entries") -or -not $parsed.entries) {
        $parsed | Add-Member -NotePropertyName entries -NotePropertyValue @() -Force
    }
    return $parsed
}

function Save-SendLedger {
    param(
        [Parameter(Mandatory = $true)]
        [object]$Ledger,
        [Parameter(Mandatory = $true)]
        [string]$LedgerPath
    )

    $Ledger | ConvertTo-Json -Depth 10 | Set-Content -LiteralPath $LedgerPath -Encoding UTF8
}

function New-LedgerEntryFromItem {
    param(
        [Parameter(Mandatory = $true)]
        [object]$Item,
        [Parameter(Mandatory = $true)]
        [string]$BatchFilePath,
        [Parameter(Mandatory = $true)]
        [string]$BatchGeneratedAtUtc
    )

    $fingerprint = Get-ItemFingerprint -Item $Item
    return [pscustomobject]@{
        createdAtUtc = [DateTimeOffset]::UtcNow.ToString("o")
        updatedAtUtc = [DateTimeOffset]::UtcNow.ToString("o")
        status = "pending"
        email = ([string]$Item.email).Trim().ToLowerInvariant()
        companyName = [string]$Item.companyName
        subject = [string]$Item.subject
        fingerprint = $fingerprint
        batchFile = $BatchFilePath
        batchGeneratedAtUtc = $BatchGeneratedAtUtc
        gmailResult = $null
        error = $null
    }
}

$ResolvedBatchFile = Resolve-RepoPath -PathValue $BatchFile
$ResolvedDbPath = Resolve-RepoPath -PathValue $DbPath

if (-not (Test-Path -LiteralPath $ResolvedBatchFile)) {
    throw "Batch file not found: $ResolvedBatchFile"
}

Assert-TemplateIntegrity -TemplateFile $TemplatePath -TemplateLockFile $TemplateLockPath
Apply-EmailTemplateToBatchFile -BatchPath $ResolvedBatchFile -TemplateFile $TemplatePath

switch ($Mode) {
    "preview" {
        $preview = Invoke-Tracker -TrackerArgs @("preview", "--batch-file", $ResolvedBatchFile) -AllowFailure
        $preview | ConvertTo-Json -Depth 8
        if (-not $preview.eligible) {
            exit 1
        }
        break
    }
    "send-confirmed" {
        if ([string]::IsNullOrWhiteSpace($ApprovalToken)) {
            throw "ApprovalToken is required for send-confirmed mode."
        }

        $preview = Invoke-Tracker -TrackerArgs @("preview", "--batch-file", $ResolvedBatchFile) -AllowFailure
        if (-not $preview.eligible) {
            $preview | ConvertTo-Json -Depth 8
            exit 1
        }

        $ledger = Load-SendLedger -LedgerPath $SendLedgerPath
        $batchGeneratedAtUtc = [string]$preview.generatedAtUtc
        $sendResults = @()
        foreach ($item in $preview.items) {
            $itemEmail = ([string]$item.email).Trim().ToLowerInvariant()
            $fingerprint = Get-ItemFingerprint -Item $item

            $existingSentForFingerprint = @($ledger.entries | Where-Object {
                $_.status -eq "sent" -and
                ([string]$_.fingerprint) -eq $fingerprint
            } | Select-Object -Last 1)

            if ($existingSentForFingerprint.Count -gt 0) {
                $sentEntry = $existingSentForFingerprint[0]
                $sendResults += [pscustomobject]@{
                    email = $item.email
                    status = "sent"
                    gmailResult = $sentEntry.gmailResult
                }
                continue
            }

            $existingSentForEmail = @($ledger.entries | Where-Object {
                $_.status -eq "sent" -and
                ([string]$_.email) -eq $itemEmail
            } | Select-Object -Last 1)

            if ($existingSentForEmail.Count -gt 0) {
                $sendResults += [pscustomobject]@{
                    email = $item.email
                    status = "sent"
                    gmailResult = [pscustomobject]@{
                        reusedFromLedger = $true
                        previouslySentAtUtc = $existingSentForEmail[0].updatedAtUtc
                        previousSubject = $existingSentForEmail[0].subject
                        reason = "Idempotency guard reused prior send for same recipient."
                    }
                }
                continue
            }

            $pendingEntry = New-LedgerEntryFromItem -Item $item -BatchFilePath $ResolvedBatchFile -BatchGeneratedAtUtc $batchGeneratedAtUtc
            $ledger.entries = @($ledger.entries + $pendingEntry)
            Save-SendLedger -Ledger $ledger -LedgerPath $SendLedgerPath

            $sendResult = Invoke-GmailSend -Item $item

            $matchedPending = @($ledger.entries | Where-Object {
                $_.status -eq "pending" -and
                ([string]$_.fingerprint) -eq $fingerprint -and
                ([string]$_.email) -eq $itemEmail
            } | Select-Object -Last 1)

            if ($matchedPending.Count -gt 0) {
                $entry = $matchedPending[0]
                $entry.status = [string]$sendResult.status
                $entry.updatedAtUtc = [DateTimeOffset]::UtcNow.ToString("o")
                if ($sendResult.PSObject.Properties.Name -contains "gmailResult") {
                    $entry.gmailResult = $sendResult.gmailResult
                }
                if ($sendResult.PSObject.Properties.Name -contains "error") {
                    $entry.error = $sendResult.error
                }
            }
            Save-SendLedger -Ledger $ledger -LedgerPath $SendLedgerPath
            $sendResults += $sendResult
        }

        $resultsPayload = @{
            attemptedAtUtc = [DateTimeOffset]::UtcNow.ToString("o")
            simulated = [bool]$Simulate
            items = $sendResults
        }

        $resultsPath = Join-Path $ArtifactsDir "last-send-results.json"
        $resultsPayload | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath $resultsPath -Encoding UTF8

        $commit = Invoke-Tracker -TrackerArgs @(
            "commit-send-results",
            "--batch-file", $ResolvedBatchFile,
            "--results-file", $resultsPath,
            "--approval-token", $ApprovalToken
        )

        $commit | ConvertTo-Json -Depth 8
        break
    }
}
