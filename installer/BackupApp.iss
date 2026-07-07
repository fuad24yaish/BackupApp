; Inno Setup script for BackupApp.
; Builds a friendly per-user installer (no admin / UAC prompt) that installs the
; self-contained app, adds Start Menu (and optional desktop) shortcuts, can start
; the app at login, launches it when finished, and registers a proper uninstaller.
;
; Compiled by installer\build.ps1, which publishes the app first and passes:
;   /DAppVersion=x.y.z  /DSourceDir=<publish folder>
; Defaults below let you also open this file directly in the Inno Setup IDE.

#ifndef AppVersion
  #define AppVersion "1.0.0"
#endif
#ifndef SourceDir
  #define SourceDir "..\publish\app"
#endif

#define AppName "BackupApp"
#define AppExeName "BackupApp.Tray.exe"
#define AppPublisher "BackupApp"

[Setup]
; A stable AppId means future installers upgrade in place instead of stacking.
AppId={{B4C2E1A7-5F3D-4E8A-9C21-7A6B0D9E4F12}
AppName={#AppName}
AppVersion={#AppVersion}
AppVerName={#AppName} {#AppVersion}
AppPublisher={#AppPublisher}
VersionInfoVersion={#AppVersion}

; Per-user install: no administrator rights required, so no UAC prompt.
PrivilegesRequired=lowest
DefaultDirName={autopf}\{#AppName}
DefaultGroupName={#AppName}
DisableProgramGroupPage=yes
DisableDirPage=yes

; Detect the app running (matches the app's single-instance mutex) and offer to
; close it before installing an update.
AppMutex=BackupApp.SingleInstance
CloseApplications=yes
RestartApplications=no

Compression=lzma2/max
SolidCompression=yes
WizardStyle=modern
SetupIconFile=assets\app.ico
UninstallDisplayIcon={app}\{#AppExeName}
UninstallDisplayName={#AppName}
OutputDir=Output
OutputBaseFilename=BackupApp-Setup-{#AppVersion}
; The published payload is x64 self-contained.
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "startup"; Description: "Start {#AppName} automatically when I sign in to Windows"; GroupDescription: "Startup:"
Name: "desktopicon"; Description: "Create a desktop shortcut"; GroupDescription: "Shortcuts:"; Flags: unchecked

[Files]
Source: "{#SourceDir}\{#AppExeName}"; DestDir: "{app}"; Flags: ignoreversion

[Icons]
Name: "{group}\{#AppName}"; Filename: "{app}\{#AppExeName}"
Name: "{group}\Uninstall {#AppName}"; Filename: "{uninstallexe}"
Name: "{autodesktop}\{#AppName}"; Filename: "{app}\{#AppExeName}"; Tasks: desktopicon

[Registry]
; Uses the same HKCU Run value name the app's own "Start with Windows" checkbox
; manages, so the two stay in sync. Removed automatically on uninstall.
Root: HKCU; Subkey: "Software\Microsoft\Windows\CurrentVersion\Run"; \
    ValueType: string; ValueName: "{#AppName}"; ValueData: """{app}\{#AppExeName}"""; \
    Flags: uninsdeletevalue; Tasks: startup

[Run]
Filename: "{app}\{#AppExeName}"; Description: "Start {#AppName} now"; \
    Flags: nowait postinstall skipifsilent

[UninstallRun]
; Make sure the app isn't running so its files can be removed.
Filename: "{cmd}"; Parameters: "/C taskkill /IM {#AppExeName} /F"; \
    Flags: runhidden; RunOnceId: "StopBackupApp"

; Note: the backup history (%LOCALAPPDATA%\BackupApp) and settings
; (%APPDATA%\BackupApp) are intentionally left in place on uninstall so a
; reinstall keeps your history. They can be deleted by hand if truly no longer needed.
