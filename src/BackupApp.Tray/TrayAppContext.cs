using BackupApp.Core;

namespace BackupApp.Tray;

/// <summary>
/// Application lifetime: builds the core services, runs the tray icon, and keeps a
/// bounded in-memory activity log so the main form can show recent events whenever
/// it is opened.
/// </summary>
internal sealed class TrayAppContext : ApplicationContext
{
    private const int MaxLogLines = 500;

    public AppConfig Config { get; }
    public Database Db { get; }
    public BlobStore Store { get; }
    public BackupEngine Engine { get; }
    public WatcherService Watcher { get; }
    public RetentionService Retention { get; }
    public MirrorService Mirror { get; }

    private readonly NotifyIcon _trayIcon;
    private readonly System.Threading.Timer _retentionTimer;
    private readonly System.Threading.Timer _mirrorTimer;
    private bool _mirrorFailureNotified;
    private readonly ToolStripMenuItem _pauseItem;
    private readonly List<string> _log = new();
    private MainForm? _mainForm;

    public event Action<string>? LogLineAdded;

    public TrayAppContext()
    {
        Config = AppConfig.Load();
        Store = new BlobStore(Config.StorePath);
        Db = new Database(Path.Combine(Config.StorePath, "index.db"));
        Engine = new BackupEngine(Db, Store, Config);
        Watcher = new WatcherService(Db, Engine, Config);
        Retention = new RetentionService(Db, Store, Config);
        Mirror = new MirrorService(Db, Store, Config);
        Engine.Activity += OnActivity;
        Watcher.Activity += OnActivity;
        Retention.Activity += OnActivity;
        Mirror.Activity += OnActivity;

        // First prune a few minutes after startup (lets the catch-up scans finish), then daily.
        _retentionTimer = new System.Threading.Timer(
            _ => RunScheduledPrune(), null, TimeSpan.FromMinutes(5), TimeSpan.FromHours(24));
        _mirrorTimer = new System.Threading.Timer(
            _ => RunScheduledMirror(), null, TimeSpan.FromMinutes(10),
            TimeSpan.FromHours(Math.Max(1, Config.Mirror.IntervalHours)));

        _pauseItem = new ToolStripMenuItem("Pause watching", null, OnTogglePause);
        var menu = new ContextMenuStrip();
        menu.Items.Add(new ToolStripMenuItem("Open BackupApp", null, (_, _) => ShowMainForm()));
        menu.Items.Add(_pauseItem);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(new ToolStripMenuItem("Exit", null, (_, _) => ExitApp()));

        _trayIcon = new NotifyIcon
        {
            Icon = SystemIcons.Shield,
            Text = "BackupApp — watching for changes",
            ContextMenuStrip = menu,
            Visible = true
        };
        _trayIcon.DoubleClick += (_, _) => ShowMainForm();

        Watcher.Start();
        OfficeSafetyNet.Sync(Db, Watcher, Config.OfficeSafetyNetEnabled);
        OnActivity("BackupApp started.");

        if (!Db.GetRoots(activeOnly: true).Any(r => r.Kind == RootKind.User))
        {
            _trayIcon.ShowBalloonTip(5000, "BackupApp",
                "No folders of yours are being watched yet. Double-click the tray icon to add one.",
                ToolTipIcon.Info);
        }
    }

    private void RunScheduledPrune()
    {
        if (!Config.Retention.Enabled) return;
        try
        {
            Retention.Prune(CancellationToken.None);
        }
        catch (Exception ex)
        {
            OnActivity("Retention run failed: " + ex.Message);
        }
    }

    private void RunScheduledMirror()
    {
        if (!Config.Mirror.Enabled || string.IsNullOrWhiteSpace(Config.Mirror.TargetPath)) return;
        try
        {
            var result = Mirror.Sync(CancellationToken.None);
            if (result.Success)
            {
                _mirrorFailureNotified = false;
            }
            else if (!_mirrorFailureNotified)
            {
                _mirrorFailureNotified = true; // one balloon per failure streak, not one per day
                _trayIcon.ShowBalloonTip(8000, "BackupApp — off-machine sync",
                    result.Message + " Use Sync Now on the Sync tab once it's available.",
                    ToolTipIcon.Warning);
            }
        }
        catch (Exception ex)
        {
            OnActivity("Scheduled sync failed: " + ex.Message);
        }
    }

    private void OnActivity(string line)
    {
        var stamped = $"{DateTime.Now:HH:mm:ss}  {line}";
        lock (_log)
        {
            _log.Add(stamped);
            if (_log.Count > MaxLogLines)
                _log.RemoveRange(0, _log.Count - MaxLogLines);
        }
        LogLineAdded?.Invoke(stamped);
    }

    public string[] GetLogSnapshot()
    {
        lock (_log) return _log.ToArray();
    }

    public void ShowMainForm()
    {
        if (_mainForm is null || _mainForm.IsDisposed)
            _mainForm = new MainForm(this);
        _mainForm.Show();
        if (_mainForm.WindowState == FormWindowState.Minimized)
            _mainForm.WindowState = FormWindowState.Normal;
        _mainForm.Activate();
    }

    private void OnTogglePause(object? sender, EventArgs e)
    {
        if (Watcher.IsPaused)
        {
            Watcher.Resume();
            _pauseItem.Checked = false;
            _trayIcon.Text = "BackupApp — watching for changes";
        }
        else
        {
            Watcher.Pause();
            _pauseItem.Checked = true;
            _trayIcon.Text = "BackupApp — paused";
        }
    }

    private void ExitApp()
    {
        _trayIcon.Visible = false;
        _retentionTimer.Dispose();
        _mirrorTimer.Dispose();
        Watcher.Dispose();
        Db.Dispose();
        ExitThread();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
            _trayIcon.Dispose();
        base.Dispose(disposing);
    }
}
