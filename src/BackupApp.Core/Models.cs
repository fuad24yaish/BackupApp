namespace BackupApp.Core;

public enum ChangeType
{
    Created,
    Modified,
    Deleted
}

public enum RootKind
{
    /// <summary>A folder the user chose to watch.</summary>
    User,
    /// <summary>An Office AutoRecover folder managed by the Office safety net feature.</summary>
    Office
}

public sealed record WatchedRoot(long Id, string Path, DateTime AddedUtc, bool Active, RootKind Kind);

public sealed record FileEntry(long Id, long RootId, string RelPath);

public sealed record FileVersion(
    long Id,
    long FileId,
    string? Hash,
    long? Size,
    DateTime? MtimeUtc,
    DateTime CapturedUtc,
    ChangeType Change);

/// <summary>One row in the history browser's file list: a tracked file plus its latest state.</summary>
public sealed record FileListItem(
    long FileId,
    long RootId,
    string RelPath,
    int VersionCount,
    DateTime LastCapturedUtc,
    ChangeType LastChange);
