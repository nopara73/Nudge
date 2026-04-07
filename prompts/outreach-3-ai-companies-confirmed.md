You are working in my local repo at: C:\Users\user\Desktop\Nudge

Goal:

1. Find THREE AI companies that seem to be printing money right now.
2. Pick ONE person per company with a public email address.
3. Draft outreach email text for each.
4. STOP and ask me for confirmation before sending any email.
5. Only after explicit confirmation, send programmatically via my existing Gmail script.
6. Update the local AI outreach tracker DB for each successful send.
7. Report back exactly what was sent and what was tracked for all 3.

Context:

- Gmail sending is already set up via:
  - scripts/gws-personal-gmail.ps1
- AI outreach runner is:
  - scripts/ai-outreach-run.ps1
- Template guard utility is:
  - scripts/ai-outreach-template-guard.ps1
- AI outreach tracker utility is:
  - scripts/ai_outreach_tracker.py
- AI outreach pool/batch prep helper is:
  - scripts/ai-outreach-prepare.ps1
- Use a repo-local batch file at:
  - .local/ai-outreach/current-batch.json
- Use the local email template at:
  - .local/ai-outreach/email-template.md
- Template lock file is:
  - .local/ai-outreach/email-template.lock.json
- Tracker DB is repo-local and should stay uncommitted:
  - .local/ai-outreach/ai-outreach.db
- Do not commit anything.
- Do not print secrets/tokens.
- NEVER modify or replace the template file.
- NEVER clear the template file.
- NEVER write custom email copy directly into batch subject/body.

Selection rules:

- Choose exactly 3 candidates.
- Each candidate must be from a different company.
- Hard skip any company contacted within the last 7 days.
- Hard skip any recipient email that has already been contacted before.
- Prefer startup-ish or newer AI companies over large old incumbents.
- Subjective confidence is acceptable, but each company should have at least one strong current signal that it is making serious money now.
- "Right now" does not require a fixed time window, but the evidence should feel current and not stale.
- Prioritize contacts in this order when possible:
  - founder
  - CTO
  - Head of Product / product leader
- Do NOT guess email patterns.
- Publicly discoverable emails are acceptable, including:
  - company website
  - personal website
  - interviews / podcasts
  - conference pages
  - other public web pages
- Global companies are allowed; language does not matter if confidence is high.

Required evidence per candidate:

- company name
- why this company appears to be printing money now
- at least one source URL backing that judgment
- contact full name if available
- contact role/title if available
- recipient email
- source URL showing the email or clearly supporting it

Batch JSON requirements:

- Before previewing, write .local/ai-outreach/current-batch.json
- The file must be valid JSON with this shape:
{
"batchName": "ai-outreach-daily",
"generatedAtUtc": "",
"items": [
{
"companyName": "Example AI",
"companyWebsite": "[https://example.com](https://example.com)",
"personName": "Jane Doe",
"roleTitle": "CTO",
"email": "[jane@example.com](mailto:jane@example.com)",
"subject": "",
"body": "",
"whySelected": "",
"companyEvidenceUrl": "https://...",
"emailSourceUrl": "https://..."
}
]
}

Execution steps:

1. First attempt to reuse existing lead pool and avoid fresh web research:
  - powershell -NoProfile -ExecutionPolicy Bypass -File "scripts/ai-outreach-template-guard.ps1" -Mode verify
  - If verification fails, STOP and report "Template integrity check failed." Do not proceed.
  - powershell -NoProfile -ExecutionPolicy Bypass -File "scripts/ai-outreach-prepare.ps1" -BatchFile ".local/ai-outreach/current-batch.json"
  - If output says eligible=true with 3 selected, skip directly to preview step below.
  - If output says needsResearch=true (or selectedCount < 3), only then do fresh web research.
2. Research live on the web and identify additional candidate companies only when needed.
3. Gather a public email for one contact at each chosen company.
4. Subject/body must be template-only:
  - Set each item's `subject` and `body` in batch JSON to `TEMPLATE_PENDING`.
  - Let `scripts/ai-outreach-run.ps1` apply template placeholders (`{companyName}`, `{personFirstName}`) during preview/send.
  - No fallback template and no hand-authored rewrites are allowed.
5. Write the batch JSON file to .local/ai-outreach/current-batch.json
6. Add newly discovered leads to the persistent pool so future runs can reuse them:
  - py -3 "scripts/ai_outreach_tracker.py" --db-path ".local/ai-outreach/ai-outreach.db" add-leads --leads-file ".local/ai-outreach/current-batch.json"
7. Run:
  - powershell -NoProfile -ExecutionPolicy Bypass -File "scripts/ai-outreach-template-guard.ps1" -Mode verify
  - If verification fails, STOP and report "Template integrity check failed." Do not proceed.
  - powershell -NoProfile -ExecutionPolicy Bypass -File "scripts/ai-outreach-run.ps1" -Mode preview -BatchFile ".local/ai-outreach/current-batch.json"
8. If preview says the batch is invalid or ineligible:
  - fix the candidates
  - rewrite the batch JSON
  - rerun preview
9. Once preview passes, present a PREVIEW list for all 3 and ask:
  - "Send these now? (yes/no)"
10. If answer is NOT explicit yes:
  - do not send any
  - do not update tracker state
  - return "Cancelled before send."
11. If answer is explicit yes:
  - run:
  powershell -NoProfile -ExecutionPolicy Bypass -File "scripts/ai-outreach-template-guard.ps1" -Mode verify
  - If verification fails, STOP and report "Template integrity check failed." Do not proceed.
  powershell -NoProfile -ExecutionPolicy Bypass -File "scripts/ai-outreach-run.ps1" -Mode send-confirmed -BatchFile ".local/ai-outreach/current-batch.json" -ApprovalToken "yes"
12. If one send fails, continue with the remaining sends and clearly report partial success/failure.

Preview format:

- Candidate 1:
  - Company:
  - Why selected:
  - Evidence URL:
  - Contact:
  - Role:
  - Recipient email:
  - Email source URL:
  - Subject:
  - Body:
- Candidate 2:
  - Company:
  - Why selected:
  - Evidence URL:
  - Contact:
  - Role:
  - Recipient email:
  - Email source URL:
  - Subject:
  - Body:
- Candidate 3:
  - Company:
  - Why selected:
  - Evidence URL:
  - Contact:
  - Role:
  - Recipient email:
  - Email source URL:
  - Subject:
  - Body:

Return format after send attempt:

- Batch confirmation received: yes/no
- Attempt 1:
  - Company:
  - Contact:
  - Role:
  - Recipient email:
  - Why selected:
  - Subject:
  - Final body:
  - Gmail send result (id/threadId) [only if sent]:
  - Tracker result [only if sent]:
  - Any errors/fallbacks used:
- Attempt 2:
  - Company:
  - Contact:
  - Role:
  - Recipient email:
  - Why selected:
  - Subject:
  - Final body:
  - Gmail send result (id/threadId) [only if sent]:
  - Tracker result [only if sent]:
  - Any errors/fallbacks used:
- Attempt 3:
  - Company:
  - Contact:
  - Role:
  - Recipient email:
  - Why selected:
  - Subject:
  - Final body:
  - Gmail send result (id/threadId) [only if sent]:
  - Tracker result [only if sent]:
  - Any errors/fallbacks used: