using System.Collections.Concurrent;

namespace BackupApp.Core;

/// <summary>
/// Owns one FileSystemWatcher per active root and a single worker thread that drains
/// a debounced queue of dirty paths. Every event is reduced to "this path is dirty";
/// the worker then reconciles the path against reality (exists as file → capture,
/// exists as directory → scan if newly appeared, gone → record deletions). This one
/// mechanism uniformly handles create, modify, delete and rename.
/// </summary>
public sealed class WatcherService : IDisposable
{
    private sealed class Pending
    {
        public required long RootId;
        public required string FullPath;
        public DateTime DueUtc;
        public int Attempts;
        public bool ScanIfDirectory; // set for Created/Renamed so new folders get scanned
    }

    private readonly Database _db;
    private readonly BackupEngine _engine;
    private readonly AppConfig _cfg;
    private readonly Dictionary<long, FileSystemWatcher> _watchers = new();
    private readonly ConcurrentDictionary<string, Pending> _pending = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentQueue<long> _rescans = new();
    private readonly CancellationTokenSource _cts = new();
    private readonly object _watcherGate = new();
    private Thread? _worker;
    private volatile bool _paused;

    public event Action<string>? Activity;
    public bool IsPaused => _paused;
    public int PendingCount => _pending.Count;

    public WatcherService(Database db, BackupEngine engine, AppConfig cfg)
    {
        _db = db;
        _engine = engine;
        _cfg = cfg;
    }

    private void Log(string msg) => Activity?.Invoke(msg);

    public void Start()
    {
        foreach (var root in _db.GetRoots(activeOnly: true))
        {
            AttachWatcher(root);
            _rescans.Enqueue(root.Id); // catch up on changes made while we weren't running
        }
        _worker = new Thread(WorkerLoop) { IsBackground = true, Name = "BackupApp.Worker" };
        _worker.Start();
    }

    public WatchedRoot AddRoot(string path, RootKind kind = RootKind.User)
    {
        var root = _db.AddRoot(path, kind);
        AttachWatcher(root);
        _rescans.Enqueue(root.Id);
        Log($"Watching {root.Path}");
        return root;
    }

    public void RemoveRoot(long rootId)
    {
        _db.SetRootActive(rootId, false);
        lock (_watcherGate)
        {
            if (_watchers.Remove(rootId, out var w))
            {
                w.EnableRaisingEvents = false;
                w.Dispose();
            }
        }
        Log("Stopped watching root (history kept).");
    }

    public void RequestRescan(long rootId) => _rescans.Enqueue(rootId);

    public void Pause()
    {
        _paused = true;
        lock (_watcherGate)
            foreach (var w in _watchers.Values) w.EnableRaisingEvents = false;
        Log("Watching paused.");
    }

    public void Resume()
    {
        _paused = false;
        lock (_watcherGate)
            foreach (var w in _watchers.Values) w.EnableRaisingEvents = true;
        // Rescan to pick up anything changed while paused.
        foreach (var root in _db.GetRoots(activeOnly: true)) _rescans.Enqueue(root.Id);
        Log("Watching resumed.");
    }

    private void AttachWatcher(WatchedRoot root)
    {
        if (!Directory.Exists(root.Path))
        {
            Log($"Root folder missing, not watching: {root.Path}");
            return;
        }
        var w = new FileSystemWatcher(root.Path)
        {
            IncludeSubdirectories = true,
            InternalBufferSize = 64 * 1024,
            NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName |
                           NotifyFilters.LastWrite | NotifyFilters.Size
        };
        w.Created += (_, e) => Enqueue(root.Id, e.FullPath, scanIfDirectory: true);
        w.Changed += (_, e) => Enqueue(root.Id, e.FullPath, scanIfDirectory: false);
        w.Deleted += (_, e) => Enqueue(root.Id, e.FullPath, scanIfDirectory: false);
        w.Renamed += (_, e) =>
        {
            Enqueue(root.Id, e.OldFullPath, scanIfDirectory: false);
            Enqueue(root.Id, e.FullPath, scanIfDirectory: true);
        };
        w.Error += (_, e) =>
        {
            Log($"Watcher overflow on {root.Path}; scheduling full rescan. ({e.GetException().Message})");
            _rescans.Enqueue(root.Id);
        };
        w.EnableRaisingEvents = !_paused;
        lock (_watcherGate) _watchers[root.Id] = w;
    }

    private void Enqueue(long rootId, string fullPath, bool scanIfDirectory)
    {
        var due = DateTime.UtcNow.AddSeconds(_cfg.DebounceSeconds);
        _pending.AddOrUpdate(rootId + "|" + fullPath,
            _ => new Pending { RootId = rootId, FullPath = fullPath, DueUtc = due, ScanIfDirectory = scanIfDirectory },
            (_, existing) =>
            {
                existing.DueUtc = due; // sliding debounce while writes keep coming
                existing.ScanIfDirectory |= scanIfDirectory;
                return existing;
            });
    }

    private void WorkerLoop()
    {
        var ct = _cts.Token;
        while (!ct.IsCancellationRequested)
        {
            try
            {
                if (!_paused && _rescans.TryDequeue(out var rootId))
                {
                    var root = _db.GetRoot(rootId);
                    if (root is { Active: true })
                        _engine.ScanRoot(root, ct);
                    continue;
                }

                bool didWork = false;
                var now = DateTime.UtcNow;
                foreach (var kv in _pending)
                {
                    if (ct.IsCancellationRequested) break;
                    if (kv.Value.DueUtc > now) continue;
                    if (!_pending.TryRemove(kv.Key, out var item)) continue;
                    didWork = true;
                    Process(item);
                }
                if (!didWork)
                    ct.WaitHandle.WaitOne(300);
            }
            catch (Exception ex)
            {
                Log($"Worker error: {ex.Message}");
            }
        }
    }

    private void Process(Pending item)
    {
        var root = _db.GetRoot(item.RootId);
        if (root is not { Active: true }) return;
        try
        {
            if (File.Exists(item.FullPath))
            {
                _engine.CaptureFile(root, item.FullPath);
            }
            else if (Directory.Exists(item.FullPath))
            {
                // Changed events fire on a directory whenever its contents change; the
                // children produce their own events, so only scan freshly appeared dirs.
                if (item.ScanIfDirectory)
                    _engine.ScanDirectory(root, Path.GetRelativePath(root.Path, item.FullPath), _cts.Token);
            }
            else
            {
                _engine.HandleMissingPath(root, item.FullPath);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            if (item.Attempts < 5)
            {
                item.Attempts++;
                item.DueUtc = DateTime.UtcNow.AddSeconds(Math.Pow(2, item.Attempts));
                _pending.TryAdd(item.RootId + "|" + item.FullPath, item);
            }
            else
            {
                Log($"Giving up on {item.FullPath} after {item.Attempts} attempts: {ex.Message}");
            }
        }
    }

    public void Dispose()
    {
        _cts.Cancel();
        lock (_watcherGate)
        {
            foreach (var w in _watchers.Values)
            {
                w.EnableRaisingEvents = false;
                w.Dispose();
            }
            _watchers.Clear();
        }
        _worker?.Join(TimeSpan.FromSeconds(5));
        _cts.Dispose();
    }
}
