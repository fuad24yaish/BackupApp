## BackupApp 1.0.0

A Windows tray app that watches folders and keeps a **complete version history** of every file change — browse and restore any past version at any time.

### Install

1. Download **`BackupApp-Setup-1.0.0.exe`** below.
2. Double-click it. No administrator rights and no .NET install are needed.
3. BackupApp starts in the system tray — double-click its icon to add folders to watch.

> Since the installer isn't code-signed, Windows SmartScreen may show a *"Windows protected your PC"* notice the first time. Click **More info → Run anyway**.

### What's inside

- **Full version history** of every create, edit, rename, and delete, in a content-addressed store (identical content is stored once).
- **Browse & restore** — filter by folder, by change date (Today / last 7 / 30 days / a specific day), newest change first.
- **Retention** — keeps recent history in full, thins older history, never loses the newest versions.
- **Office safety net** (on by default) — versions Word/Excel/PowerPoint AutoRecover snapshots, so a document closed with *"Don't Save"* can still be recovered.
- **Off-machine sync** — mirror the whole history to a USB drive or a cloud-synced folder (e.g. Google Drive for Desktop).

### Notes

- Per-user install (no UAC prompt); optionally starts at sign-in.
- Uninstalling keeps your backup history and settings so a reinstall picks up where you left off.
- Self-contained: the target PC needs nothing preinstalled.
