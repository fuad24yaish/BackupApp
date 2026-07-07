namespace BackupApp.Core;

public sealed record PruneResult(long VersionsDeleted, long FilesPurged, long BlobsDeleted, long BytesReclaimed)
{
    public override string ToString() =>
        $"{VersionsDeleted} versions deleted, {FilesPurged} file histories purged, " +
        $"{BlobsDeleted} blobs removed, {BackupEngine.FormatSize(BytesReclaimed)} reclaimed";
}

/// <summary>
/// Applies the retention policy: prunes version rows per file according to the policy,
/// purges whole histories of long-deleted files, then garbage-collects blobs that no
/// version references anymore. Safe to run while the watcher is capturing: version
/// selection only deletes rows it has seen, and blob GC skips recently written blobs
/// (a capture writes its blob before inserting the version row that references it).
/// </summary>
public sealed class RetentionService
{
    private readonly Database _db;
    private readonly BlobStore _store;
    private readonly AppConfig _cfg;
    private readonly TimeSpan _blobGrace;
    private readonly object _gate = new();

    public event Action<string>? Activity;
    public PruneResult? LastResult { get; private set; }
    public DateTime? LastRunUtc { get; private set; }

    public RetentionService(Database db, BlobStore store, AppConfig cfg, TimeSpan? blobGrace = null)
    {
        _db = db;
        _store = store;
        _cfg = cfg;
        _blobGrace = blobGrace ?? TimeSpan.FromHours(1);
    }

    private void Log(string msg) => Activity?.Invoke(msg);

    public PruneResult Prune(CancellationToken ct)
    {
        lock (_gate)
        {
            var policy = _cfg.Retention;
            var now = DateTime.UtcNow;
            long versionsDeleted = 0, filesPurged = 0;
            Log("Retention: pruning history…");

            foreach (var root in _db.GetRoots())
            {
                foreach (var file in _db.GetFileList(root.Id))
                {
                    ct.ThrowIfCancellationRequested();
                    var versions = _db.GetVersions(file.FileId);
                    if (versions.Count == 0) continue;

                    // A file deleted longer ago than the hard cutoff: drop its whole history.
                    if (policy.MaxAgeDays > 0 &&
                        versions[0].Change == ChangeType.Deleted &&
                        versions[0].CapturedUtc < now.AddDays(-policy.MaxAgeDays))
                    {
                        _db.PurgeFile(file.FileId);
                        filesPurged++;
                        versionsDeleted += versions.Count;
                        continue;
                    }

                    var prunable = SelectPrunable(policy, versions, now);
                    if (prunable.Count > 0)
                    {
                        _db.DeleteVersions(prunable.Select(v => v.Id).ToList());
                        versionsDeleted += prunable.Count;
                    }
                }
            }

            // Blob GC: anything no longer referenced by a version row can go. The grace
            // window protects blobs written by an in-flight capture whose version row
            // hasn't been inserted yet.
            long blobsDeleted = 0, bytesReclaimed = 0;
            var referenced = _db.GetReferencedHashes();
            foreach (var blob in _store.EnumerateBlobs())
            {
                ct.ThrowIfCancellationRequested();
                if (referenced.Contains(blob.Hash)) continue;
                if (now - blob.WrittenUtc < _blobGrace) continue;
                if (_store.TryDelete(blob.Hash))
                {
                    blobsDeleted++;
                    bytesReclaimed += blob.SizeBytes;
                }
            }

            if (versionsDeleted > 0 || filesPurged > 0)
                _db.Vacuum();

            var result = new PruneResult(versionsDeleted, filesPurged, blobsDeleted, bytesReclaimed);
            LastResult = result;
            LastRunUtc = now;
            Log("Retention: " + result);
            return result;
        }
    }

    /// <summary>
    /// Pure policy: given a file's versions (newest first), returns the ones to delete.
    /// The newest version is always kept, so a live file can always be restored.
    /// </summary>
    public static List<FileVersion> SelectPrunable(
        RetentionPolicy policy, IReadOnlyList<FileVersion> versionsDesc, DateTime nowUtc)
    {
        var keep = new HashSet<long>();
        int minKeep = Math.Max(1, policy.MinVersionsPerFile);
        var keepAllCutoff = nowUtc.AddDays(-Math.Max(0, policy.KeepAllDays));

        for (int i = 0; i < versionsDesc.Count && i < minKeep; i++)
            keep.Add(versionsDesc[i].Id);

        foreach (var v in versionsDesc)
            if (v.CapturedUtc >= keepAllCutoff)
                keep.Add(v.Id);

        if (policy.ThinToDaily)
        {
            // Beyond the keep-all window: newest version per (file, UTC day) survives.
            var seenDays = new HashSet<DateOnly>();
            foreach (var v in versionsDesc) // newest first, so first hit per day wins
            {
                if (v.CapturedUtc >= keepAllCutoff) continue;
                if (seenDays.Add(DateOnly.FromDateTime(v.CapturedUtc)))
                    keep.Add(v.Id);
            }
        }
        else
        {
            foreach (var v in versionsDesc)
                if (v.CapturedUtc < keepAllCutoff)
                    keep.Add(v.Id);
        }

        if (policy.MaxAgeDays > 0)
        {
            var maxAgeCutoff = nowUtc.AddDays(-policy.MaxAgeDays);
            foreach (var v in versionsDesc)
                if (v.CapturedUtc < maxAgeCutoff)
                    keep.Remove(v.Id);
            // The newest-N guarantee overrides the hard cutoff.
            for (int i = 0; i < versionsDesc.Count && i < minKeep; i++)
                keep.Add(versionsDesc[i].Id);
        }

        return versionsDesc.Where(v => !keep.Contains(v.Id)).ToList();
    }
}
