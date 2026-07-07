namespace BackupApp.Core;

/// <summary>
/// The Office safety net watches the folders where Word, Excel and PowerPoint write
/// AutoRecover snapshots while a document is being edited. Because BackupApp keeps
/// history for deleted files, the snapshots stay restorable even after Office cleans
/// them up — which is what rescues a document closed with "Don't Save" by mistake.
/// </summary>
public static class OfficeSafetyNet
{
    /// <summary>Well-known AutoRecover locations that exist on this machine.</summary>
    public static IReadOnlyList<string> GetKnownFolders(string? appData = null, string? localAppData = null)
    {
        appData ??= Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        localAppData ??= Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        string[] candidates =
        [
            Path.Combine(appData, "Microsoft", "Word"),
            Path.Combine(appData, "Microsoft", "Excel"),
            Path.Combine(appData, "Microsoft", "PowerPoint"),
            Path.Combine(localAppData, "Microsoft", "Office", "UnsavedFiles"),
        ];
        return candidates.Where(Directory.Exists).ToList();
    }

    /// <summary>
    /// Brings the watched roots in line with the setting: enabling adds any known Office
    /// folder that isn't already watched; disabling stops watching Office-kind roots
    /// (their captured history is kept and stays browsable).
    /// </summary>
    public static void Sync(Database db, WatcherService watcher, bool enabled,
        IReadOnlyList<string>? folders = null)
    {
        if (enabled)
        {
            folders ??= GetKnownFolders();
            var active = db.GetRoots(activeOnly: true);
            foreach (var folder in folders)
            {
                var normalized = Path.TrimEndingDirectorySeparator(Path.GetFullPath(folder));
                if (!active.Any(r => string.Equals(r.Path, normalized, StringComparison.OrdinalIgnoreCase)))
                    watcher.AddRoot(normalized, RootKind.Office);
            }
        }
        else
        {
            foreach (var root in db.GetRoots(activeOnly: true).Where(r => r.Kind == RootKind.Office))
                watcher.RemoveRoot(root.Id);
        }
    }
}
