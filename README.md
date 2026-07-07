# BackupApp

A Windows tray application that watches chosen root folders and keeps a **complete
version history** of every file change inside them — every create, edit, rename and
delete is recorded, and any past version can be browsed and restored.

## Install (for end users)

Download **`BackupApp-Setup-<version>.exe`** and double-click it. The installer:

- needs **no administrator rights** and shows no UAC prompt (it installs just for
  the current user),
- requires **no .NET install** — the runtime is bundled inside the app,
- adds a Start Menu shortcut (and, if you tick the box, a desktop shortcut),
- can **start BackupApp automatically when you sign in** (ticked by default),
- registers a normal entry in *Settings → Apps* so it can be uninstalled later.

After it finishes, BackupApp starts and sits in the system tray (look for its icon
near the clock). Double-click the tray icon to open it and add folders to watch.

> Because the installer isn't code-signed, Windows SmartScreen may show a blue
> *"Windows protected your PC"* notice the first time. Click **More info →
> Run anyway**. (Signing it with a code-signing certificate removes this warning.)

Uninstalling leaves your backup history and settings in place, so reinstalling
keeps everything; delete `%LOCALAPPDATA%\BackupApp` by hand if you truly want it gone.

## How it works

```
src/
  BackupApp.Core/    engine library (no UI)
    Models.cs          records: WatchedRoot, FileEntry, FileVersion, ChangeType
    AppConfig.cs       JSON config at %APPDATA%\BackupApp\config.json
    Database.cs        SQLite index (roots / files / versions)
    BlobStore.cs       content-addressed storage (SHA-256, gzip)
    BackupEngine.cs    capture, directory reconcile, restore
    WatcherService.cs  FileSystemWatcher + debounced worker queue
    RetentionService.cs retention policy + blob garbage collection
    OfficeSafetyNet.cs  auto-watch Office AutoRecover folders
    MirrorService.cs   off-machine sync: mirror the store to USB/cloud folder
  BackupApp.Tray/    WinForms tray app
    TrayAppContext.cs  tray icon, lifetime, activity log buffer
    MainForm.cs        folders / history / activity UI
```

- **Content-addressed store** — each unique file content is stored once, as a
  gzip-compressed blob named by the SHA-256 of its content
  (`store\objects\ab\cdef….gz`), like git. Saving the same content twice (or the
  same file in two places) costs no extra space.
- **SQLite index** (`store\index.db`) maps every watched root → tracked files →
  ordered version rows (hash, size, mtime, capture time, change type). A deletion
  is a version row with no hash, so "the file existed, then was deleted, then
  recreated" is all visible history.
- **Watcher** — one `FileSystemWatcher` per root feeds a debounced queue (default
  2 s quiet period, so a file being saved repeatedly is captured once). A single
  worker thread reconciles each dirty path against reality: file exists → capture
  if content changed; directory appeared → scan it; path gone → record deletions.
  Renames arrive as delete-old + capture-new. Locked files are retried with
  exponential backoff; watcher buffer overflows trigger a full rescan.
- **Catch-up scans** — on startup (and on resume/demand) every root is rescanned,
  so changes made while the app wasn't running are still captured as versions.

## Using it

Run `BackupApp.Tray`. It lives in the system tray:

- **Watched Folders** tab — add/remove root folders, trigger a manual scan, and
  enable *Start with Windows* (HKCU Run key). Removing a folder stops watching but
  keeps its history browsable.
- **History** tab — browse *All folders* or a single root, filter by change date
  (Today, Yesterday, last 7/30 days, or a specific day) and by path, newest change
  first; select a file to see every version. Restore to the original location (current content is captured first, so
  a restore is never destructive), save a copy elsewhere, or open a temp copy.
- **Retention** tab — control how long history is kept and prune on demand.
- **Sync** tab — mirror the store to a USB drive / network share / cloud folder,
  daily and on demand.
- **Activity** tab — live log of captures, deletions, scans, skips, prunes and syncs.

## Off-machine sync (Sync tab)

The store can be mirrored to a destination folder so history survives the computer:

- a **USB flash drive** (e.g. `E:\BackupApp`),
- a **network share**,
- a **cloud-synced folder** — point it at your *Google Drive for Desktop* or
  OneDrive folder and the cloud client uploads the mirror for you. No API keys or
  sign-ins are needed.

Pick a destination on the Sync tab and enable the daily schedule (it also runs
~10 minutes after the app starts, and always via **Sync Now**). The sync is
incremental: content-addressed blobs never change, so only new blobs are copied
and blobs pruned by retention are removed; the SQLite index is snapshotted with
the online backup API, so it is consistent even while captures are running.
If the drive isn't plugged in, the scheduled sync notifies once and simply
succeeds the next time the target is reachable.

The destination is a **complete standalone replica** (`objects\` + `index.db` +
`config.json` + `mirror-info.json`). To recover on a new machine: install
BackupApp, copy the mirror folder to `%LOCALAPPDATA%\BackupApp\store` (or set
`StorePath` to it), start the app, and the full history is browsable again.

Safety guards: the destination may not overlap the store or live inside a watched
folder (that would back up the backup recursively).

## Office safety net (on by default)

Closing a Word/Excel/PowerPoint document and clicking **"Don't Save"** by mistake
normally loses the edits — they were never written to the document, so no file
watcher can capture them. But Office continuously writes **AutoRecover snapshots**
(e.g. `.asd` files) while you edit. The safety net auto-watches those folders:

- `%APPDATA%\Microsoft\Word`, `…\Excel`, `…\PowerPoint`
- `%LOCALAPPDATA%\Microsoft\Office\UnsavedFiles`

Every autosave becomes a version — and because BackupApp keeps history for deleted
files, the snapshots remain restorable **after Office deletes them on "Don't
Save"**. To rescue a document: History tab → the Office folder → find the `.asd`
for your document → *Save a copy as…* → open it in Word.

These folders appear as "Office safety net" entries on the Watched Folders tab and
are managed by its checkbox rather than per-folder. Inside them the usual temp-file
exclusions are relaxed (AutoRecover files look temporary); only `~$` owner-lock
stubs are skipped. Normal retention applies to the snapshot history.

> Tip: AutoRecover saves every 10 minutes by default. Lowering it (Word → File →
> Options → Save) to 1–2 minutes gives much finer-grained rescue points; the
> content-addressed store keeps the extra snapshots cheap. Edits made between
> autosaves are still unrecoverable — nothing on disk means nothing to back up.

## Retention

History would otherwise grow forever, so a retention policy runs daily (and ~5
minutes after startup). Defaults are conservative:

1. Every version from the last **30 days** (`KeepAllDays`) is kept.
2. Older than that, history is **thinned to the newest version per file per day**
   (`ThinToDaily`) — nothing is fully forgotten, just intermediate same-day saves.
3. Optionally, versions older than `MaxAgeDays` are deleted outright
   (default **0 = keep forever**). Files whose *deletion* is older than this
   cutoff have their whole history purged.
4. Whatever the rules above say, the **newest 3 versions of every file**
   (`MinVersionsPerFile`) are never pruned, so a live file is always restorable.

After pruning the index, blobs no longer referenced by any version are
garbage-collected from the store (blobs younger than an hour are left alone so an
in-flight capture is never raced), and the database is compacted. Retention can be
disabled entirely on the Retention tab.

## Configuration (`%APPDATA%\BackupApp\config.json`)

| Setting | Default | Meaning |
|---|---|---|
| `StorePath` | `%LOCALAPPDATA%\BackupApp\store` | Where blobs + index live |
| `DebounceSeconds` | 2 | Quiet period before capturing a changed file |
| `MaxFileSizeMB` | 1024 | Files larger than this are not versioned |
| `ExcludeFilePatterns` | `*.tmp`, `~$*`, … | File name wildcards to ignore |
| `ExcludeDirectoryNames` | `.git`, `node_modules`, … | Directory names skipped entirely |
| `Retention.Enabled` | `true` | Run the daily retention prune |
| `Retention.KeepAllDays` | 30 | Window in which every version is kept |
| `Retention.ThinToDaily` | `true` | Thin older history to one version/file/day |
| `Retention.MaxAgeDays` | 0 | Hard delete beyond this age (0 = never) |
| `Retention.MinVersionsPerFile` | 3 | Newest versions always kept |
| `OfficeSafetyNetEnabled` | `true` | Auto-watch Office AutoRecover folders |
| `Mirror.Enabled` | `false` | Run the scheduled off-machine sync |
| `Mirror.TargetPath` | — | Destination folder (USB, share, cloud folder) |
| `Mirror.IntervalHours` | 24 | Scheduled sync interval |

## Building

Requires the **.NET 10 SDK**. To build and run during development:

```
dotnet build BackupApp.slnx
dotnet run --project src/BackupApp.Tray
```

### Building the installer

One command publishes a self-contained single-file exe and wraps it in the
Setup.exe:

```
powershell -ExecutionPolicy Bypass -File installer/build.ps1 -Version 1.0.0
```

The result is `installer/Output/BackupApp-Setup-1.0.0.exe` (~46 MB, self-contained
so the target PC needs nothing preinstalled).

This needs **Inno Setup 6** (one-time):

```
winget install --id JRSoftware.InnoSetup -e
```

The packaging lives in `installer/`:

```
installer/
  BackupApp.iss     Inno Setup script (per-user install, shortcuts, startup, uninstaller)
  build.ps1         publish + compile the installer in one step
  make-icon.ps1     regenerates assets/app.ico (already committed; rarely needed)
  assets/app.ico    application icon (embedded in the exe, used by shortcuts)
```

## Known limits

- A file rename is recorded as *deleted* at the old path and *created* at the new
  one (full history is kept on both, but not linked).
- Files that are exclusively locked without a quiet moment (e.g. live Outlook PST)
  are retried five times, then skipped until their next change or a rescan.
