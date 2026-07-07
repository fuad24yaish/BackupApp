using System.Text.RegularExpressions;

namespace BackupApp.Core;

/// <summary>
/// Turns filesystem observations into history: captures file versions into the blob
/// store, records deletions, reconciles whole directories, and restores versions.
/// All methods are called from the watcher's single worker thread (plus restore
/// calls from the UI, which only read).
/// </summary>
public sealed class BackupEngine
{
    private readonly Database _db;
    private readonly BlobStore _store;
    private readonly AppConfig _cfg;
    private readonly List<Regex> _excludePatterns;
    private readonly HashSet<string> _excludeDirs;

    public event Action<string>? Activity;

    public BackupEngine(Database db, BlobStore store, AppConfig cfg)
    {
        _db = db;
        _store = store;
        _cfg = cfg;
        _excludePatterns = cfg.ExcludeFilePatterns.Select(WildcardToRegex).ToList();
        _excludeDirs = new HashSet<string>(cfg.ExcludeDirectoryNames, StringComparer.OrdinalIgnoreCase);
    }

    private static Regex WildcardToRegex(string pattern) => new(
        "^" + Regex.Escape(pattern).Replace(@"\*", ".*").Replace(@"\?", ".") + "$",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private void Log(string msg) => Activity?.Invoke(msg);

    public bool IsExcludedFileName(string fileName) =>
        _excludePatterns.Any(rx => rx.IsMatch(fileName));

    public bool IsExcludedDirName(string dirName) => _excludeDirs.Contains(dirName);

    /// <summary>True if the path lies inside the blob store or hits an excluded name anywhere in it.</summary>
    public bool IsExcludedPath(WatchedRoot root, string fullPath)
    {
        if (fullPath.StartsWith(_store.StoreDirectory + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) ||
            string.Equals(fullPath, _store.StoreDirectory, StringComparison.OrdinalIgnoreCase))
            return true;

        // Office AutoRecover folders are full of files that look temporary (.asd, .tmp) but
        // are exactly what the safety net exists to keep — only skip owner-lock files there.
        if (root.Kind == RootKind.Office)
            return Path.GetFileName(fullPath).StartsWith("~$", StringComparison.Ordinal);

        var parts = fullPath.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        for (int i = 0; i < parts.Length - 1; i++)
            if (_excludeDirs.Contains(parts[i])) return true;
        return IsExcludedFileName(parts[^1]);
    }

    private static string RelPath(WatchedRoot root, string fullPath) =>
        Path.GetRelativePath(root.Path, fullPath);

    /// <summary>
    /// Captures the current content of a file if it differs from the last recorded version.
    /// Throws IOException/UnauthorizedAccessException if the file is locked (caller retries).
    /// </summary>
    public void CaptureFile(WatchedRoot root, string fullPath)
    {
        if (IsExcludedPath(root, fullPath)) return;
        var fi = new FileInfo(fullPath);
        if (!fi.Exists)
        {
            HandleMissingPath(root, fullPath);
            return;
        }
        if (fi.Length > _cfg.MaxFileSizeBytes)
        {
            Log($"Skipped (over {_cfg.MaxFileSizeMB} MB): {fullPath}");
            return;
        }

        string rel = RelPath(root, fullPath);
        long fileId = _db.GetOrCreateFile(root.Id, rel);
        var last = _db.GetLastVersion(fileId);

        // Cheap skip: same size + mtime as the last live version means unchanged.
        if (last is { Change: not ChangeType.Deleted } &&
            last.Size == fi.Length && last.MtimeUtc == fi.LastWriteTimeUtc)
            return;

        string hash;
        long size;
        using (var fs = new FileStream(fullPath, FileMode.Open, FileAccess.Read,
                   FileShare.ReadWrite | FileShare.Delete))
        {
            (hash, size) = _store.Store(fs);
        }

        if (last is { Change: not ChangeType.Deleted } && last.Hash == hash)
            return; // content identical (e.g. touch without change)

        var change = last is null or { Change: ChangeType.Deleted } ? ChangeType.Created : ChangeType.Modified;
        _db.AddVersion(fileId, hash, size, fi.LastWriteTimeUtc, change);
        Log($"{change}: {root.Path}{Path.DirectorySeparatorChar}{rel} ({FormatSize(size)})");
    }

    /// <summary>A path no longer exists on disk: record deletion of the file and/or everything under it.</summary>
    public void HandleMissingPath(WatchedRoot root, string fullPath)
    {
        string rel = RelPath(root, fullPath);

        var file = _db.FindFile(root.Id, rel);
        if (file is not null)
            RecordDeleted(root, file);

        // The path may have been a directory: mark every live tracked file under it deleted.
        foreach (var child in _db.GetLiveFiles(root.Id, rel).Where(f => f.Id != file?.Id))
            RecordDeleted(root, child);
    }

    private void RecordDeleted(WatchedRoot root, FileEntry file)
    {
        var last = _db.GetLastVersion(file.Id);
        if (last is null or { Change: ChangeType.Deleted }) return;
        _db.AddVersion(file.Id, null, null, null, ChangeType.Deleted);
        Log($"Deleted: {root.Path}{Path.DirectorySeparatorChar}{file.RelPath}");
    }

    /// <summary>
    /// Reconciles a directory subtree against the index: captures new/changed files and
    /// records deletions for tracked files that are gone. relPrefix "." means the whole root.
    /// </summary>
    public void ScanDirectory(WatchedRoot root, string relPrefix, CancellationToken ct)
    {
        string dir = relPrefix is "." or "" ? root.Path : Path.Combine(root.Path, relPrefix);
        if (!Directory.Exists(dir)) { HandleMissingPath(root, dir); return; }

        var onDisk = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var file in EnumerateFilesSafe(dir))
        {
            if (ct.IsCancellationRequested) return;
            onDisk.Add(RelPath(root, file));
            try
            {
                CaptureFile(root, file);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                Log($"Scan could not read {file}: {ex.Message}");
            }
        }

        string pfx = relPrefix is "." ? "" : relPrefix;
        foreach (var tracked in _db.GetLiveFiles(root.Id, pfx))
        {
            if (ct.IsCancellationRequested) return;
            if (!onDisk.Contains(tracked.RelPath))
                RecordDeleted(root, tracked);
        }
    }

    public void ScanRoot(WatchedRoot root, CancellationToken ct)
    {
        Log($"Scanning {root.Path}…");
        ScanDirectory(root, ".", ct);
        Log($"Scan finished: {root.Path}");
    }

    /// <summary>Recursive enumeration that skips excluded directories and survives access-denied subtrees.</summary>
    private IEnumerable<string> EnumerateFilesSafe(string dir)
    {
        var stack = new Stack<string>();
        stack.Push(dir);
        while (stack.Count > 0)
        {
            string current = stack.Pop();
            string[] files, dirs;
            try
            {
                files = Directory.GetFiles(current);
                dirs = Directory.GetDirectories(current);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                continue;
            }
            foreach (var f in files)
                yield return f;
            foreach (var d in dirs)
            {
                var name = Path.GetFileName(d);
                if (IsExcludedDirName(name)) continue;
                if (d.StartsWith(_store.StoreDirectory, StringComparison.OrdinalIgnoreCase)) continue;
                stack.Push(d);
            }
        }
    }

    // ---- Restore ----

    public void RestoreTo(long versionId, string targetPath)
    {
        var version = _db.GetVersion(versionId)
                      ?? throw new InvalidOperationException($"Version {versionId} not found.");
        if (version.Hash is null)
            throw new InvalidOperationException("This entry records a deletion; pick an earlier version to restore.");
        _store.ExtractTo(version.Hash, targetPath);
        if (version.MtimeUtc.HasValue)
            File.SetLastWriteTimeUtc(targetPath, version.MtimeUtc.Value);
        Log($"Restored version {versionId} to {targetPath}");
    }

    /// <summary>Extracts a version to a temp folder and returns the path (for "open a copy").</summary>
    public string RestoreToTemp(long versionId)
    {
        var version = _db.GetVersion(versionId)
                      ?? throw new InvalidOperationException($"Version {versionId} not found.");
        var file = _db.GetFileForVersion(versionId)!;
        string name = Path.GetFileName(file.RelPath);
        string dir = Path.Combine(Path.GetTempPath(), "BackupApp", versionId.ToString());
        string target = Path.Combine(dir, name);
        RestoreTo(versionId, target);
        return target;
    }

    public static string FormatSize(long bytes) => bytes switch
    {
        < 1024 => $"{bytes} B",
        < 1024 * 1024 => $"{bytes / 1024.0:0.#} KB",
        < 1024L * 1024 * 1024 => $"{bytes / (1024.0 * 1024):0.#} MB",
        _ => $"{bytes / (1024.0 * 1024 * 1024):0.##} GB"
    };
}
