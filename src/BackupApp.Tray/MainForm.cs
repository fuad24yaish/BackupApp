using System.Diagnostics;
using BackupApp.Core;
using Microsoft.Win32;

namespace BackupApp.Tray;

/// <summary>
/// Main window: manage watched folders, browse the version history of any file,
/// restore versions, and follow live activity. Closing the window hides it; the
/// app keeps running in the tray.
/// </summary>
internal sealed class MainForm : Form
{
    private const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string RunValueName = "BackupApp";

    private readonly TrayAppContext _app;

    // Folders tab
    private readonly ListView _rootsList = new();
    private readonly CheckBox _startWithWindows = new();
    private readonly CheckBox _officeSafetyNet = new();
    private readonly Label _storeInfo = new();

    // History tab
    private readonly ComboBox _historyRoot = new();
    private readonly ComboBox _historyDate = new();
    private readonly DateTimePicker _historyDay = new();
    private readonly TextBox _search = new();
    private Dictionary<long, WatchedRoot> _rootsById = new();
    private readonly ListView _filesList = new();
    private readonly ListView _versionsList = new();
    private readonly Button _restoreOriginal = new();
    private readonly Button _saveCopyAs = new();
    private readonly Button _openCopy = new();

    // Retention tab
    private readonly CheckBox _retentionEnabled = new();
    private readonly NumericUpDown _keepAllDays = new();
    private readonly CheckBox _thinToDaily = new();
    private readonly NumericUpDown _maxAgeDays = new();
    private readonly NumericUpDown _minVersions = new();
    private readonly Button _pruneNow = new();
    private readonly Label _pruneResult = new();
    private bool _loadingRetentionUi;

    // Sync tab
    private readonly TextBox _mirrorTarget = new();
    private readonly CheckBox _mirrorEnabled = new();
    private readonly Button _syncNow = new();
    private readonly Label _syncResult = new();
    private bool _loadingSyncUi;

    // Activity tab
    private readonly ListBox _activityList = new();

    public MainForm(TrayAppContext app)
    {
        _app = app;
        Text = "BackupApp";
        Icon = AppIcon.Value;
        StartPosition = FormStartPosition.CenterScreen;
        ClientSize = new Size(900, 560);
        MinimumSize = new Size(700, 450);

        var tabs = new TabControl { Dock = DockStyle.Fill };
        tabs.TabPages.Add(BuildFoldersTab());
        tabs.TabPages.Add(BuildHistoryTab());
        tabs.TabPages.Add(BuildRetentionTab());
        tabs.TabPages.Add(BuildSyncTab());
        tabs.TabPages.Add(BuildActivityTab());
        Controls.Add(tabs);

        _app.LogLineAdded += OnLogLine;
        FormClosing += (_, e) =>
        {
            if (e.CloseReason == CloseReason.UserClosing)
            {
                e.Cancel = true;
                Hide();
            }
            else
            {
                _app.LogLineAdded -= OnLogLine;
            }
        };

        RefreshRoots();
        RefreshHistoryRoots();
        _activityList.Items.AddRange(_app.GetLogSnapshot());
        ScrollActivityToEnd();
    }

    // ---------- Folders tab ----------

    private TabPage BuildFoldersTab()
    {
        var page = new TabPage("Watched Folders");

        _rootsList.View = View.Details;
        _rootsList.FullRowSelect = true;
        _rootsList.Dock = DockStyle.Fill;
        _rootsList.Columns.Add("Folder", 520);
        _rootsList.Columns.Add("Status", 120);

        var addBtn = new Button { Text = "Add Folder…", AutoSize = true };
        addBtn.Click += (_, _) => AddFolder();
        var removeBtn = new Button { Text = "Stop Watching", AutoSize = true };
        removeBtn.Click += (_, _) => RemoveSelectedRoot();
        var scanBtn = new Button { Text = "Scan Now", AutoSize = true };
        scanBtn.Click += (_, _) => ScanSelectedRoot();

        _startWithWindows.Text = "Start BackupApp when I sign in to Windows";
        _startWithWindows.AutoSize = true;
        _startWithWindows.Checked = IsStartupEnabled();
        _startWithWindows.CheckedChanged += (_, _) => SetStartupEnabled(_startWithWindows.Checked);

        _officeSafetyNet.Text = "Office safety net — keep Word/Excel/PowerPoint AutoRecover snapshots " +
                                "(rescues documents closed with \"Don't Save\")";
        _officeSafetyNet.AutoSize = true;
        _officeSafetyNet.Checked = _app.Config.OfficeSafetyNetEnabled; // set before wiring the handler
        _officeSafetyNet.CheckedChanged += (_, _) => ToggleOfficeSafetyNet();

        _storeInfo.AutoSize = true;
        _storeInfo.ForeColor = SystemColors.GrayText;

        var buttons = new FlowLayoutPanel { Dock = DockStyle.Top, AutoSize = true, Padding = new Padding(4) };
        buttons.Controls.AddRange([addBtn, removeBtn, scanBtn]);

        var bottom = new FlowLayoutPanel
        {
            Dock = DockStyle.Bottom, AutoSize = true, FlowDirection = FlowDirection.TopDown,
            Padding = new Padding(6)
        };
        bottom.Controls.AddRange([_officeSafetyNet, _startWithWindows, _storeInfo]);

        page.Controls.Add(_rootsList);
        page.Controls.Add(buttons);
        page.Controls.Add(bottom);
        return page;
    }

    private void RefreshRoots()
    {
        _rootsList.Items.Clear();
        foreach (var root in _app.Db.GetRoots())
        {
            var item = new ListViewItem(root.Path) { Tag = root };
            item.SubItems.Add(root.Active
                ? !Directory.Exists(root.Path) ? "Folder missing"
                    : root.Kind == RootKind.Office ? "Office safety net"
                    : "Watching"
                : "Stopped");
            if (root.Kind == RootKind.Office)
                item.ForeColor = SystemColors.GrayText;
            _rootsList.Items.Add(item);
        }
        var (files, versions) = _app.Db.GetStats();
        _storeInfo.Text =
            $"Store: {_app.Config.StorePath}   •   {files} files tracked, {versions} versions, " +
            $"{BackupEngine.FormatSize(_app.Store.GetStoreSizeBytes())} on disk";
    }

    private void AddFolder()
    {
        using var dlg = new FolderBrowserDialog
        {
            Description = "Choose a folder to watch. Every change to files inside it will be kept in history.",
            UseDescriptionForTitle = true
        };
        if (dlg.ShowDialog(this) != DialogResult.OK) return;

        var path = dlg.SelectedPath;
        foreach (var existing in _app.Db.GetRoots(activeOnly: true))
        {
            if (path.StartsWith(existing.Path + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(path, existing.Path, StringComparison.OrdinalIgnoreCase))
            {
                MessageBox.Show(this, $"That folder is already covered by the watched folder:\n{existing.Path}",
                    "BackupApp", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }
        }
        _app.Watcher.AddRoot(path);
        RefreshRoots();
        RefreshHistoryRoots();
    }

    private WatchedRoot? SelectedRoot() =>
        _rootsList.SelectedItems.Count > 0 ? (WatchedRoot)_rootsList.SelectedItems[0].Tag! : null;

    private void ToggleOfficeSafetyNet()
    {
        _app.Config.OfficeSafetyNetEnabled = _officeSafetyNet.Checked;
        _app.Config.Save();
        OfficeSafetyNet.Sync(_app.Db, _app.Watcher, _officeSafetyNet.Checked);
        RefreshRoots();
        RefreshHistoryRoots();
    }

    private void RemoveSelectedRoot()
    {
        if (SelectedRoot() is not { } root) return;
        if (root.Kind == RootKind.Office)
        {
            MessageBox.Show(this,
                "This folder is managed by the Office safety net — use its checkbox below to turn it off.",
                "BackupApp", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }
        var answer = MessageBox.Show(this,
            $"Stop watching {root.Path}?\n\nIts existing history is kept and stays browsable; new changes just won't be recorded.",
            "BackupApp", MessageBoxButtons.YesNo, MessageBoxIcon.Question);
        if (answer != DialogResult.Yes) return;
        _app.Watcher.RemoveRoot(root.Id);
        RefreshRoots();
        RefreshHistoryRoots();
    }

    private void ScanSelectedRoot()
    {
        if (SelectedRoot() is not { } root) return;
        _app.Watcher.RequestRescan(root.Id);
        MessageBox.Show(this, "Scan queued — progress appears on the Activity tab.",
            "BackupApp", MessageBoxButtons.OK, MessageBoxIcon.Information);
    }

    private static bool IsStartupEnabled()
    {
        using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath);
        return key?.GetValue(RunValueName) is not null;
    }

    private static void SetStartupEnabled(bool enabled)
    {
        using var key = Registry.CurrentUser.CreateSubKey(RunKeyPath);
        if (enabled)
            key.SetValue(RunValueName, $"\"{Application.ExecutablePath}\"");
        else
            key.DeleteValue(RunValueName, throwOnMissingValue: false);
    }

    // ---------- History tab ----------

    private TabPage BuildHistoryTab()
    {
        var page = new TabPage("History");

        _historyRoot.DropDownStyle = ComboBoxStyle.DropDownList;
        _historyRoot.Width = 300;
        _historyRoot.SelectedIndexChanged += (_, _) => RefreshFileList();

        _historyDate.DropDownStyle = ComboBoxStyle.DropDownList;
        _historyDate.Width = 110;
        _historyDate.Items.AddRange(["Any time", "Today", "Yesterday", "Last 7 days", "Last 30 days", "On day…"]);
        _historyDate.SelectedIndex = 0;
        _historyDate.SelectedIndexChanged += (_, _) =>
        {
            _historyDay.Enabled = _historyDate.SelectedIndex == 5;
            RefreshFileList();
        };

        _historyDay.Format = DateTimePickerFormat.Short;
        _historyDay.Width = 110;
        _historyDay.Enabled = false;
        _historyDay.MaxDate = DateTime.Today;
        _historyDay.ValueChanged += (_, _) => { if (_historyDate.SelectedIndex == 5) RefreshFileList(); };

        _search.Width = 180;
        _search.PlaceholderText = "Filter by path…";
        _search.TextChanged += (_, _) => RefreshFileList();

        var refreshBtn = new Button { Text = "Refresh", AutoSize = true };
        refreshBtn.Click += (_, _) => RefreshFileList();

        var top = new FlowLayoutPanel { Dock = DockStyle.Top, AutoSize = true, Padding = new Padding(4) };
        top.Controls.AddRange([new Label { Text = "Folder:", AutoSize = true, Margin = new Padding(3, 8, 3, 3) },
            _historyRoot,
            new Label { Text = "Changed:", AutoSize = true, Margin = new Padding(9, 8, 3, 3) },
            _historyDate, _historyDay, _search, refreshBtn]);

        _filesList.View = View.Details;
        _filesList.FullRowSelect = true;
        _filesList.Dock = DockStyle.Fill;
        _filesList.Columns.Add("File", 260);
        _filesList.Columns.Add("Folder", 180);
        _filesList.Columns.Add("Versions", 60);
        _filesList.Columns.Add("Last change", 130);
        _filesList.Columns.Add("State", 80);
        _filesList.SelectedIndexChanged += (_, _) => RefreshVersions();

        _versionsList.View = View.Details;
        _versionsList.FullRowSelect = true;
        _versionsList.Dock = DockStyle.Fill;
        _versionsList.Columns.Add("Captured", 140);
        _versionsList.Columns.Add("Change", 80);
        _versionsList.Columns.Add("Size", 90);
        _versionsList.SelectedIndexChanged += (_, _) => UpdateRestoreButtons();

        _restoreOriginal.Text = "Restore to original location";
        _restoreOriginal.AutoSize = true;
        _restoreOriginal.Click += (_, _) => RestoreSelected(toOriginal: true);
        _saveCopyAs.Text = "Save a copy as…";
        _saveCopyAs.AutoSize = true;
        _saveCopyAs.Click += (_, _) => RestoreSelected(toOriginal: false);
        _openCopy.Text = "Open a copy";
        _openCopy.AutoSize = true;
        _openCopy.Click += (_, _) => OpenSelectedCopy();
        UpdateRestoreButtons();

        var versionButtons = new FlowLayoutPanel { Dock = DockStyle.Bottom, AutoSize = true, Padding = new Padding(4) };
        versionButtons.Controls.AddRange([_restoreOriginal, _saveCopyAs, _openCopy]);

        var rightPanel = new Panel { Dock = DockStyle.Fill };
        rightPanel.Controls.Add(_versionsList);
        rightPanel.Controls.Add(versionButtons);

        var split = new SplitContainer { Dock = DockStyle.Fill, SplitterDistance = 500 };
        split.Panel1.Controls.Add(_filesList);
        split.Panel2.Controls.Add(rightPanel);

        page.Controls.Add(split);
        page.Controls.Add(top);
        return page;
    }

    private void RefreshHistoryRoots()
    {
        var selected = (_historyRoot.SelectedItem as RootComboItem)?.Root?.Id;
        _historyRoot.Items.Clear();
        _historyRoot.Items.Add(new RootComboItem(null));
        foreach (var root in _app.Db.GetRoots())
            _historyRoot.Items.Add(new RootComboItem(root));
        var idx = 0;
        for (int i = 1; i < _historyRoot.Items.Count; i++)
            if (((RootComboItem)_historyRoot.Items[i]!).Root!.Id == selected) idx = i;
        _historyRoot.SelectedIndex = idx;
        RefreshFileList();
    }

    private sealed record RootComboItem(WatchedRoot? Root)
    {
        public override string ToString()
        {
            if (Root is null) return "All folders";
            var label = Root.Kind == RootKind.Office ? Root.Path + "  (Office safety net)" : Root.Path;
            return Root.Active ? label : label + "  (stopped)";
        }
    }

    /// <summary>The UTC capture-time range for the selected date filter (local calendar days).</summary>
    private (DateTime? FromUtc, DateTime? ToUtc) GetHistoryDateRange()
    {
        var today = DateTime.Today; // local midnight
        return _historyDate.SelectedIndex switch
        {
            1 => (today.ToUniversalTime(), null),                                       // Today
            2 => (today.AddDays(-1).ToUniversalTime(), today.ToUniversalTime()),        // Yesterday
            3 => (today.AddDays(-6).ToUniversalTime(), null),                           // Last 7 days
            4 => (today.AddDays(-29).ToUniversalTime(), null),                          // Last 30 days
            5 => (_historyDay.Value.Date.ToUniversalTime(),
                  _historyDay.Value.Date.AddDays(1).ToUniversalTime()),                 // On day…
            _ => (null, null)                                                           // Any time
        };
    }

    private void RefreshFileList()
    {
        _filesList.Items.Clear();
        _versionsList.Items.Clear();
        UpdateRestoreButtons();
        if (_historyRoot.SelectedItem is not RootComboItem item) return;

        _rootsById = _app.Db.GetRoots().ToDictionary(r => r.Id);
        var (fromUtc, toUtc) = GetHistoryDateRange();

        foreach (var f in _app.Db.GetFileList(item.Root?.Id, _search.Text, fromUtc, toUtc))
        {
            var row = new ListViewItem(f.RelPath) { Tag = f };
            row.SubItems.Add(_rootsById.TryGetValue(f.RootId, out var root) ? root.Path : "?");
            row.SubItems.Add(f.VersionCount.ToString());
            row.SubItems.Add(f.LastCapturedUtc.ToLocalTime().ToString("g"));
            row.SubItems.Add(f.LastChange == ChangeType.Deleted ? "Deleted" : "Present");
            if (f.LastChange == ChangeType.Deleted)
                row.ForeColor = SystemColors.GrayText;
            _filesList.Items.Add(row);
        }
    }

    private void RefreshVersions()
    {
        _versionsList.Items.Clear();
        UpdateRestoreButtons();
        if (_filesList.SelectedItems.Count == 0) return;
        var file = (FileListItem)_filesList.SelectedItems[0].Tag!;

        foreach (var v in _app.Db.GetVersions(file.FileId))
        {
            var row = new ListViewItem(v.CapturedUtc.ToLocalTime().ToString("g")) { Tag = v };
            row.SubItems.Add(v.Change.ToString());
            row.SubItems.Add(v.Size.HasValue ? BackupEngine.FormatSize(v.Size.Value) : "—");
            if (v.Change == ChangeType.Deleted)
                row.ForeColor = SystemColors.GrayText;
            _versionsList.Items.Add(row);
        }
        if (_versionsList.Items.Count > 0)
            _versionsList.Items[0].Selected = true;
    }

    private FileVersion? SelectedVersion() =>
        _versionsList.SelectedItems.Count > 0 ? (FileVersion)_versionsList.SelectedItems[0].Tag! : null;

    private void UpdateRestoreButtons()
    {
        bool restorable = SelectedVersion() is { Hash: not null };
        _restoreOriginal.Enabled = restorable;
        _saveCopyAs.Enabled = restorable;
        _openCopy.Enabled = restorable;
    }

    private void RestoreSelected(bool toOriginal)
    {
        if (SelectedVersion() is not { } version || _filesList.SelectedItems.Count == 0)
            return;
        var file = (FileListItem)_filesList.SelectedItems[0].Tag!;
        var root = _rootsById.TryGetValue(file.RootId, out var r) ? r : _app.Db.GetRoot(file.RootId);
        if (root is null) return;

        try
        {
            string target;
            if (toOriginal)
            {
                target = Path.Combine(root.Path, file.RelPath);
                if (File.Exists(target))
                {
                    var answer = MessageBox.Show(this,
                        $"{target} already exists.\n\nOverwrite it with the selected version? " +
                        "(The current content is captured into history first, so nothing is lost.)",
                        "BackupApp", MessageBoxButtons.YesNo, MessageBoxIcon.Warning);
                    if (answer != DialogResult.Yes) return;
                    _app.Engine.CaptureFile(root, target);
                }
            }
            else
            {
                using var dlg = new SaveFileDialog
                {
                    FileName = Path.GetFileName(file.RelPath),
                    Title = "Save a copy of this version"
                };
                if (dlg.ShowDialog(this) != DialogResult.OK) return;
                target = dlg.FileName;
            }

            _app.Engine.RestoreTo(version.Id, target);
            MessageBox.Show(this, $"Restored to:\n{target}", "BackupApp",
                MessageBoxButtons.OK, MessageBoxIcon.Information);
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, "Restore failed: " + ex.Message, "BackupApp",
                MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    private void OpenSelectedCopy()
    {
        if (SelectedVersion() is not { } version) return;
        try
        {
            var path = _app.Engine.RestoreToTemp(version.Id);
            Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, "Could not open a copy: " + ex.Message, "BackupApp",
                MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    // ---------- Retention tab ----------

    private TabPage BuildRetentionTab()
    {
        var page = new TabPage("Retention");
        var policy = _app.Config.Retention;
        _loadingRetentionUi = true;

        _retentionEnabled.Text = "Automatically prune old history (runs daily and at startup)";
        _retentionEnabled.AutoSize = true;
        _retentionEnabled.Checked = policy.Enabled;
        _retentionEnabled.CheckedChanged += (_, _) => SaveRetentionSettings();

        _keepAllDays.Minimum = 1;
        _keepAllDays.Maximum = 3650;
        _keepAllDays.Value = Math.Clamp(policy.KeepAllDays, 1, 3650);
        _keepAllDays.Width = 70;
        _keepAllDays.ValueChanged += (_, _) => SaveRetentionSettings();

        _thinToDaily.Text = "Older than that, keep only the newest version per file per day";
        _thinToDaily.AutoSize = true;
        _thinToDaily.Checked = policy.ThinToDaily;
        _thinToDaily.CheckedChanged += (_, _) => SaveRetentionSettings();

        _maxAgeDays.Minimum = 0;
        _maxAgeDays.Maximum = 36500;
        _maxAgeDays.Value = Math.Clamp(policy.MaxAgeDays, 0, 36500);
        _maxAgeDays.Width = 70;
        _maxAgeDays.ValueChanged += (_, _) => SaveRetentionSettings();

        _minVersions.Minimum = 1;
        _minVersions.Maximum = 1000;
        _minVersions.Value = Math.Clamp(policy.MinVersionsPerFile, 1, 1000);
        _minVersions.Width = 70;
        _minVersions.ValueChanged += (_, _) => SaveRetentionSettings();

        _loadingRetentionUi = false;

        _pruneNow.Text = "Prune Now";
        _pruneNow.AutoSize = true;
        _pruneNow.Click += (_, _) => PruneNow();

        _pruneResult.AutoSize = true;
        _pruneResult.ForeColor = SystemColors.GrayText;
        _pruneResult.Text = _app.Retention.LastResult is { } last
            ? $"Last prune: {last}"
            : "No prune has run yet this session.";

        static FlowLayoutPanel Row(params Control[] controls)
        {
            var row = new FlowLayoutPanel { AutoSize = true, WrapContents = false, Margin = new Padding(0, 4, 0, 4) };
            row.Controls.AddRange(controls);
            return row;
        }
        static Label Text(string text) => new()
            { Text = text, AutoSize = true, Margin = new Padding(3, 6, 3, 3) };

        var layout = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            FlowDirection = FlowDirection.TopDown,
            WrapContents = false,
            Padding = new Padding(12)
        };
        layout.Controls.AddRange(
        [
            Row(_retentionEnabled),
            Row(Text("Keep every version from the last"), _keepAllDays, Text("days")),
            Row(_thinToDaily),
            Row(Text("Delete versions older than"), _maxAgeDays, Text("days   (0 = keep forever)")),
            Row(Text("But always keep the newest"), _minVersions, Text("versions of each file")),
            Row(Text("Files whose deletion is older than the hard limit have their whole history removed.")),
            Row(_pruneNow, _pruneResult)
        ]);
        page.Controls.Add(layout);
        return page;
    }

    private void SaveRetentionSettings()
    {
        if (_loadingRetentionUi) return;
        var policy = _app.Config.Retention;
        policy.Enabled = _retentionEnabled.Checked;
        policy.KeepAllDays = (int)_keepAllDays.Value;
        policy.ThinToDaily = _thinToDaily.Checked;
        policy.MaxAgeDays = (int)_maxAgeDays.Value;
        policy.MinVersionsPerFile = (int)_minVersions.Value;
        _app.Config.Save();
    }

    private void PruneNow()
    {
        var answer = MessageBox.Show(this,
            "Prune history now using the settings above?\n\n" +
            "Versions outside the retention policy are permanently deleted.",
            "BackupApp", MessageBoxButtons.YesNo, MessageBoxIcon.Warning);
        if (answer != DialogResult.Yes) return;

        _pruneNow.Enabled = false;
        _pruneResult.Text = "Pruning…";
        Task.Run(() =>
        {
            try
            {
                var result = _app.Retention.Prune(CancellationToken.None);
                BeginInvoke(() =>
                {
                    _pruneResult.Text = "Last prune: " + result;
                    _pruneNow.Enabled = true;
                    RefreshRoots();     // store size changed
                    RefreshFileList();  // version counts changed
                });
            }
            catch (Exception ex)
            {
                BeginInvoke(() =>
                {
                    _pruneResult.Text = "Prune failed: " + ex.Message;
                    _pruneNow.Enabled = true;
                });
            }
        });
    }

    // ---------- Sync tab ----------

    private TabPage BuildSyncTab()
    {
        var page = new TabPage("Sync");
        _loadingSyncUi = true;

        var intro = new Label
        {
            AutoSize = true,
            MaximumSize = new Size(820, 0),
            Margin = new Padding(0, 0, 0, 10),
            Text = "Mirror the entire backup store to another place so your history survives this " +
                   "computer: a USB flash drive, a network share, or a cloud-synced folder such as " +
                   "Google Drive for Desktop or OneDrive (the cloud client uploads it from there). " +
                   "The copy is incremental and the destination is a complete, standalone replica."
        };

        _mirrorTarget.Width = 460;
        _mirrorTarget.PlaceholderText = @"e.g. E:\BackupApp  or  C:\Users\me\Google Drive\BackupApp";
        _mirrorTarget.Text = _app.Config.Mirror.TargetPath;
        _mirrorTarget.TextChanged += (_, _) => SaveSyncSettings();

        var browse = new Button { Text = "Browse…", AutoSize = true };
        browse.Click += (_, _) =>
        {
            using var dlg = new FolderBrowserDialog
            {
                Description = "Choose the destination folder for the off-machine copy.",
                UseDescriptionForTitle = true,
                ShowNewFolderButton = true
            };
            if (dlg.ShowDialog(this) == DialogResult.OK)
                _mirrorTarget.Text = dlg.SelectedPath;
        };

        _mirrorEnabled.Text = "Sync automatically every day (and shortly after BackupApp starts)";
        _mirrorEnabled.AutoSize = true;
        _mirrorEnabled.Checked = _app.Config.Mirror.Enabled;
        _mirrorEnabled.CheckedChanged += (_, _) => SaveSyncSettings();

        _syncNow.Text = "Sync Now";
        _syncNow.AutoSize = true;
        _syncNow.Click += (_, _) => SyncNow();

        _syncResult.AutoSize = true;
        _syncResult.ForeColor = SystemColors.GrayText;
        _syncResult.Text = _app.Mirror.LastResult is { } last
            ? (last.Success ? "Last sync: " + last.Message : "Last sync failed: " + last.Message)
            : "No sync has run yet this session.";

        _loadingSyncUi = false;

        var targetRow = new FlowLayoutPanel { AutoSize = true, WrapContents = false, Margin = new Padding(0, 4, 0, 4) };
        targetRow.Controls.AddRange([
            new Label { Text = "Destination:", AutoSize = true, Margin = new Padding(3, 8, 3, 3) },
            _mirrorTarget, browse]);

        var actionRow = new FlowLayoutPanel { AutoSize = true, WrapContents = false, Margin = new Padding(0, 8, 0, 4) };
        actionRow.Controls.AddRange([_syncNow, _syncResult]);

        var layout = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            FlowDirection = FlowDirection.TopDown,
            WrapContents = false,
            Padding = new Padding(12)
        };
        layout.Controls.AddRange([intro, targetRow, _mirrorEnabled, actionRow]);
        page.Controls.Add(layout);
        return page;
    }

    private void SaveSyncSettings()
    {
        if (_loadingSyncUi) return;
        _app.Config.Mirror.TargetPath = _mirrorTarget.Text.Trim();
        _app.Config.Mirror.Enabled = _mirrorEnabled.Checked;
        _app.Config.Save();
    }

    private void SyncNow()
    {
        var problem = _app.Mirror.ValidateTarget(_mirrorTarget.Text.Trim());
        if (problem.Length > 0)
        {
            MessageBox.Show(this, problem, "BackupApp", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }

        _syncNow.Enabled = false;
        _syncResult.Text = "Syncing…";
        Task.Run(() =>
        {
            var result = _app.Mirror.Sync(CancellationToken.None);
            BeginInvoke(() =>
            {
                _syncResult.Text = result.Success
                    ? $"Last sync: {result.Message} ({result.Duration.TotalSeconds:0.#}s)"
                    : "Last sync failed: " + result.Message;
                _syncNow.Enabled = true;
            });
        });
    }

    // ---------- Activity tab ----------

    private TabPage BuildActivityTab()
    {
        var page = new TabPage("Activity");
        _activityList.Dock = DockStyle.Fill;
        _activityList.IntegralHeight = false;
        page.Controls.Add(_activityList);
        return page;
    }

    private void OnLogLine(string line)
    {
        if (IsDisposed) return;
        try
        {
            BeginInvoke(() =>
            {
                _activityList.Items.Add(line);
                while (_activityList.Items.Count > 500)
                    _activityList.Items.RemoveAt(0);
                ScrollActivityToEnd();
            });
        }
        catch (ObjectDisposedException)
        {
            // form closed between the check and the invoke; nothing to do
        }
    }

    private void ScrollActivityToEnd()
    {
        if (_activityList.Items.Count > 0)
            _activityList.TopIndex = _activityList.Items.Count - 1;
    }
}
