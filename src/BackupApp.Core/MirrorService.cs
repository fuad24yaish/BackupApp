using System.Diagnostics;
using System.Text.Json;

namespace BackupApp.Core;

public sealed record MirrorResult(
    bool Success, string Message, int BlobsCopied, int BlobsDeleted, long BytesCopied, TimeSpan Duration);

/// <summary>
/// Mirrors the store to a destination folder so the history survives the machine:
/// blobs are copied incrementally (content-addressed files never change, so "missing
/// at destination" is the whole diff), blobs pruned locally are removed, and the
/// index is snapshotted with SQLite's online backup. Blob copy runs again after the
/// index snapshot so every blob the mirrored index references is present.
/// The destination ends up a complete, standalone replica of the store.
/// </summary>
public sealed class MirrorService
{
    private readonly Database _db;
    private readonly BlobStore _store;
    private readonly AppConfig _cfg;
    private readonly object _gate = new();

    public event Action<string>? Activity;
    public MirrorResult? LastResult { get; private set; }
    public DateTime? LastRunUtc { get; private set; }

    public MirrorService(Database db, BlobStore store, AppConfig cfg)
    {
        _db = db;
        _store = store;
        _cfg = cfg;
    }

    private void Log(string msg) => Activity?.Invoke(msg);

    /// <summary>Empty string if the target is usable, otherwise the reason it isn't.</summary>
    public string ValidateTarget(string targetPath)
    {
        if (string.IsNullOrWhiteSpace(targetPath))
            return "No destination folder is configured.";
        string target = Path.TrimEndingDirectorySeparator(Path.GetFullPath(targetPath));
        string? driveRoot = Path.GetPathRoot(target);
        if (driveRoot is null || !Directory.Exists(driveRoot))
            return $"Drive {driveRoot} is not available — is the USB drive plugged in?";

        string store = _store.StoreDirectory;
        if (IsSameOrUnder(target, store) || IsSameOrUnder(store, target))
            return "The destination must not overlap the backup store itself.";
        foreach (var root in _db.GetRoots(activeOnly: true))
            if (IsSameOrUnder(target, root.Path))
                return $"The destination is inside the watched folder {root.Path} — " +
                       "syncing there would back up the backup. Choose an unwatched folder.";
        return "";
    }

    private static bool IsSameOrUnder(string path, string ancestor) =>
        string.Equals(path, ancestor, StringComparison.OrdinalIgnoreCase) ||
        path.StartsWith(ancestor + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);

    public MirrorResult Sync(CancellationToken ct)
    {
        lock (_gate)
        {
            var sw = Stopwatch.StartNew();
            LastRunUtc = DateTime.UtcNow;

            MirrorResult Fail(string msg)
            {
                var r = new MirrorResult(false, msg, 0, 0, 0, sw.Elapsed);
                LastResult = r;
                Log("Sync failed: " + msg);
                return r;
            }

            var problem = ValidateTarget(_cfg.Mirror.TargetPath);
            if (problem.Length > 0) return Fail(problem);
            string target = Path.TrimEndingDirectorySeparator(Path.GetFullPath(_cfg.Mirror.TargetPath));

            try
            {
                Directory.CreateDirectory(target);
                string destObjects = Path.Combine(target, "objects");
                Directory.CreateDirectory(destObjects);
                Log($"Sync: mirroring store to {target}…");

                int copied = 0, deleted = 0;
                long bytes = 0;
                var localHashes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

                void CopyMissing()
                {
                    foreach (var blob in _store.EnumerateBlobs())
                    {
                        ct.ThrowIfCancellationRequested();
                        localHashes.Add(blob.Hash);
                        string rel = BlobStore.RelativeBlobPath(blob.Hash);
                        string dest = Path.Combine(target, rel);
                        if (File.Exists(dest)) continue;
                        Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
                        string tmp = dest + ".tmp";
                        File.Copy(Path.Combine(_store.StoreDirectory, rel), tmp, overwrite: true);
                        File.Move(tmp, dest, overwrite: true); // never leave half-copied blobs
                        copied++;
                        bytes += blob.SizeBytes;
                    }
                }

                CopyMissing();

                // Blobs pruned locally by retention leave the mirror too.
                foreach (var sub in new DirectoryInfo(destObjects).EnumerateDirectories())
                {
                    if (sub.Name.Length != 2) continue;
                    foreach (var f in sub.EnumerateFiles())
                    {
                        ct.ThrowIfCancellationRequested();
                        if (f.Extension == ".tmp") { f.Delete(); continue; } // stray from an interrupted sync
                        var hash = sub.Name + Path.GetFileNameWithoutExtension(f.Name);
                        if (!localHashes.Contains(hash))
                        {
                            f.Delete();
                            deleted++;
                        }
                    }
                }

                _db.BackupTo(Path.Combine(target, "index.db"));
                CopyMissing(); // blobs captured while we were copying, now referenced by the snapshot

                if (File.Exists(AppConfig.ConfigFilePath))
                    File.Copy(AppConfig.ConfigFilePath, Path.Combine(target, "config.json"), overwrite: true);

                File.WriteAllText(Path.Combine(target, "mirror-info.json"), JsonSerializer.Serialize(new
                {
                    lastSyncUtc = DateTime.UtcNow,
                    machine = Environment.MachineName,
                    sourceStore = _store.StoreDirectory,
                    blobCount = localHashes.Count
                }, new JsonSerializerOptions { WriteIndented = true }));

                var result = new MirrorResult(true,
                    $"copied {copied} blobs ({BackupEngine.FormatSize(bytes)}), removed {deleted}, " +
                    $"{localHashes.Count} blobs mirrored",
                    copied, deleted, bytes, sw.Elapsed);
                LastResult = result;
                Log($"Sync finished in {sw.Elapsed.TotalSeconds:0.#}s: {result.Message}");
                return result;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                return Fail(ex.Message);
            }
        }
    }
}
