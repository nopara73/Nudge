# Personal Gmail with `gws` (repo-local setup)

This project uses a local PowerShell wrapper so you can run Google Workspace CLI (`gws`) without a global install and keep auth files out of git.

## Why this flow for personal `@gmail.com`

For personal accounts, the broad "recommended" scope set can fail with `invalid_scope` when OAuth is still in testing mode. A narrower login scope is more reliable:

- Use `https://www.googleapis.com/auth/gmail.send,https://www.googleapis.com/auth/gmail.settings.basic` at first.
- Add more scopes later only if needed.

## 1) Prerequisites

- Node.js 18+ installed (`node -v`)
- A Google account (`@gmail.com`)
- A Google Cloud project you control

## 2) Create OAuth Desktop credentials

In Google Cloud Console (in your chosen project):

1. Open **OAuth consent screen**
2. Set app type to **External**
3. Keep it in **Testing** mode
4. Add your Gmail address under **Test users**
5. Open **Credentials** and create **OAuth client ID**
6. Choose **Desktop app**
7. Download the JSON and save it as either:

- `.\.local\gws\client_secret.json` (recommended for this repo setup), or
- `%USERPROFILE%\.config\gws\client_secret.json` (the script will auto-copy it into `.\.local\gws\` on first run)

If auth says "Access blocked", it is usually missing test-user setup.

## 3) Login from this repo

From repo root:

```powershell
.\scripts\gws-personal-gmail.ps1 -Action login
```

This runs:

`gws auth login --scopes https://www.googleapis.com/auth/gmail.send,https://www.googleapis.com/auth/gmail.settings.basic`

and stores auth state under:

`.\.local\gws\`

## 4) Send a test email

```powershell
.\scripts\gws-personal-gmail.ps1 -Action send-test -To "you@example.com"
```

## 5) Run any other `gws` command in repo-local mode

Examples:

```powershell
.\scripts\gws-personal-gmail.ps1 -Action raw -PassThruArgs gmail,+triage
.\scripts\gws-personal-gmail.ps1 -Action raw -PassThruArgs gmail,+watch
```

## Notes

- If Gmail API is not enabled, Google returns `accessNotConfigured`. Enable Gmail API for your project and retry.
- Keep this local setup private; auth credentials and tokens should never be committed.
