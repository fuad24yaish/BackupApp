using System.Text.Json;
using System.Text.Json.Serialization;

namespace BackupApp.Core;

/// <summary>
/// How long history is kept. The default is conservative: within the keep-all window
/// nothing is touched; older history is only thinned to one version per file per day,
/// and nothing is ever hard-deleted unless MaxAgeDays is set. The newest versions of
/// a file are always kept regardless of age.
/// </summary>
public sealed class RetentionPolicy
{
    public bool Enabled { get; set; } = true;

    /// <summary>The newest N versions of every file are never pruned, whatever their age.</summary>
    public int MinVersionsPerFile { get; set; } = 3;

    /// <summary>Every version captured within this many days is kept.</summary>
    public int KeepAllDays { get; set; } = 30;

    /// <summary>Older than the keep-all window: keep only the newest version per file per day.</summary>
    public bool ThinToDaily { get; set; } = true;

    /// <summary>Versions older than this are deleted (except the newest N). 0 = keep forever.</summary>
    public int MaxAgeDays { get; set; } = 0;
}

/// <summary>
/// Off-machine sync: mirrors the whole store (blobs + index + config) to a destination
/// folder — a USB drive, network share, or a cloud-synced folder like Google Drive for
/// Desktop. The mirror is a complete, standalone replica of the backup history.
/// </summary>
public sealed class MirrorSettings
{
    public bool Enabled { get; set; }

    /// <summary>Destination folder, e.g. E:\BackupApp or C:\Users\me\Google Drive\BackupApp.</summary>
    public string TargetPath { get; set; } = "";

    /// <summary>How often the scheduled sync runs.</summary>
    public int IntervalHours { get; set; } = 24;
}

public sealed class AppConfig
{
    public string StorePath { get; set; } =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "BackupApp", "store");

    /// <summary>Quiet period after the last change to a file before it is captured.</summary>
    public int DebounceSeconds { get; set; } = 2;

    /// <summary>Files larger than this are not versioned (they would bloat the store on every save).</summary>
    public long MaxFileSizeMB { get; set; } = 1024;

    public List<string> ExcludeFilePatterns { get; set; } =
        ["*.tmp", "~$*", "*.crdownload", "*.partial", "Thumbs.db", "desktop.ini"];

    public List<string> ExcludeDirectoryNames { get; set; } =
        [".git", "node_modules", "$RECYCLE.BIN", "System Volume Information"];

    public RetentionPolicy Retention { get; set; } = new();

    /// <summary>
    /// Automatically watch the Office AutoRecover folders (Word/Excel/PowerPoint and
    /// UnsavedFiles), so autosave snapshots are versioned and survive Office deleting
    /// them — e.g. when "Don't Save" is clicked by mistake.
    /// </summary>
    public bool OfficeSafetyNetEnabled { get; set; } = true;

    public MirrorSettings Mirror { get; set; } = new();

    [JsonIgnore]
    public long MaxFileSizeBytes => MaxFileSizeMB * 1024L * 1024L;

    public static string ConfigDirectory =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "BackupApp");

    public static string ConfigFilePath => Path.Combine(ConfigDirectory, "config.json");

    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    public static AppConfig Load()
    {
        try
        {
            if (File.Exists(ConfigFilePath))
                return JsonSerializer.Deserialize<AppConfig>(File.ReadAllText(ConfigFilePath)) ?? new AppConfig();
        }
        catch
        {
            // Corrupt config falls back to defaults; it will be rewritten on next Save.
        }
        var cfg = new AppConfig();
        cfg.Save();
        return cfg;
    }

    public void Save()
    {
        Directory.CreateDirectory(ConfigDirectory);
        File.WriteAllText(ConfigFilePath, JsonSerializer.Serialize(this, JsonOptions));
    }
}
