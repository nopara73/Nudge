You are working in my local repo at: C:\Users\user\Desktop\Nudge

Goal:

1. Pick THREE podcasts from my Nudge tracker that are still actionable (NOT already contacted).
2. Draft outreach email text for each using my template style.
3. STOP and ask me for confirmation before sending each email.
4. Only after explicit confirmation, send programmatically via my existing Gmail script.
5. Update each sent show's tracker DB entry to ContactedWaiting.
6. Report back exactly what was sent and what was updated for all 3.

Context:

- Gmail sending is already set up via:
  - scripts/gws-personal-gmail.ps1
- Nudge tracker DB is at:
  - C:\Users\user\AppData\Local\Nudge.Ui\nudge-ui.db
- Use Python sqlite3 for DB reads/updates.
- Do not commit anything.
- Do not print secrets/tokens.

Email template rules (important):

- Keep subject EXACTLY: "Guest for your show?"
- Greeting should use host first name if available (e.g., "Hey Raghav, I'm Adam.")
- If host not available, use show team name.
- Keep copy close to:
Subject: Guest for your show?
Hey _, I'm Adam. I’d love to be a guest on your show and collaborate. I’m the creator of the Longevity World Cup and we just launched our second season. Let me know if you’re open to it.
Best,
Ádám,
Building longevityworldcup.com

Selection rules:

- Pick only shows in TargetStates where State = "New"
- Prefer records with:
  - non-empty contact email
  - host name present in latest RunTargets.PodcastHostsJson
- Do NOT pick shows already in ContactedWaiting / RepliedYes / etc.
- If a chosen show lacks host, fallback greeting to "Hey  team, I'm Adam."
- Choose exactly 3 distinct shows.

Execution steps:

1. Query DB and pick 3 candidates.
2. Draft final subject/body for each from the template with minimal personalization.
3. Present a PREVIEW list for all 3 and ask:
  - "Send these now? (yes/no)"
4. If answer is NOT explicit yes:
  - do not send any
  - do not update DB
  - return "Cancelled before send."
5. If answer is explicit yes:
  - send each email (one by one) with:
  powershell -NoProfile -ExecutionPolicy Bypass -File "scripts/gws-personal-gmail.ps1" -Action send-test -To "" -Subject "Guest for your show?" -Body ""
6. For each send that succeeds, update DB:
  - TargetStates.State = "ContactedWaiting"
  - ContactedAtUtc = now UTC
  - CooldownUntilUtc = now + 90 days UTC
  - SnoozeUntilUtc = NULL
  - SnoozedFromState = NULL
  - UpdatedAtUtc = now UTC
7. For each send that succeeds, insert TargetStateEvents row:
  - EventType = "MarkedContacted"
  - FromState = previous state
  - ToState = "ContactedWaiting"
  - OccurredAtUtc = now UTC
  - Note / Tags can reuse existing values or empty strings
8. Verify updates with SELECT queries.
9. If one send fails, continue with remaining approved sends and clearly report partial success/failure.

Return format:

- Batch confirmation received: yes/no
- Attempt 1:
  - Chosen show:
  - Host used in greeting:
  - Recipient email:
  - Subject:
  - Final body:
  - Gmail send result (id/threadId) [only if sent]:
  - DB state before -> after [only if sent]:
  - CooldownUntilUtc [only if sent]:
  - Event inserted [only if sent]:
  - Any errors/fallbacks used:
- Attempt 2:
  - Chosen show:
  - Host used in greeting:
  - Recipient email:
  - Subject:
  - Final body:
  - Gmail send result (id/threadId) [only if sent]:
  - DB state before -> after [only if sent]:
  - CooldownUntilUtc [only if sent]:
  - Event inserted [only if sent]:
  - Any errors/fallbacks used:
- Attempt 3:
  - Chosen show:
  - Host used in greeting:
  - Recipient email:
  - Subject:
  - Final body:
  - Gmail send result (id/threadId) [only if sent]:
  - DB state before -> after [only if sent]:
  - CooldownUntilUtc [only if sent]:
  - Event inserted [only if sent]:
  - Any errors/fallbacks used:

