using Microsoft.Data.Sqlite;

namespace BackupApp.Core;

/// <summary>
/// SQLite index over the blob store: which roots are watched, which files exist,
/// and the full version history of every file. All access is serialized on a lock;
/// the write workload is a single background thread and reads are cheap.
/// </summary>
public sealed class Database : IDisposable
{
    private readonly SqliteConnection _conn;
    private readonly object _gate = new();

    public Database(string dbPath)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(dbPath)!);
        _conn = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = dbPath,
            Mode = SqliteOpenMode.ReadWriteCreate
        }.ToString());
        _conn.Open();
        Exec("PRAGMA journal_mode=WAL;");
        Exec("PRAGMA foreign_keys=ON;");
        Exec("""
            CREATE TABLE IF NOT EXISTS roots(
                id INTEGER PRIMARY KEY,
                path TEXT NOT NULL UNIQUE COLLATE NOCASE,
                added_utc TEXT NOT NULL,
                active INTEGER NOT NULL DEFAULT 1,
                kind TEXT NOT NULL DEFAULT 'User');
            CREATE TABLE IF NOT EXISTS files(
                id INTEGER PRIMARY KEY,
                root_id INTEGER NOT NULL REFERENCES roots(id),
                rel_path TEXT NOT NULL COLLATE NOCASE,
                UNIQUE(root_id, rel_path));
            CREATE TABLE IF NOT EXISTS versions(
                id INTEGER PRIMARY KEY,
                file_id INTEGER NOT NULL REFERENCES files(id),
                hash TEXT NULL,
                size INTEGER NULL,
                mtime_utc TEXT NULL,
                captured_utc TEXT NOT NULL,
                change TEXT NOT NULL);
            CREATE INDEX IF NOT EXISTS idx_versions_file ON versions(file_id, id);
            """);
        MigrateSchema();
    }

    /// <summary>Databases created before v3 lack the roots.kind column.</summary>
    private void MigrateSchema()
    {
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM pragma_table_info('roots') WHERE name='kind'";
        if ((long)cmd.ExecuteScalar()! == 0)
            Exec("ALTER TABLE roots ADD COLUMN kind TEXT NOT NULL DEFAULT 'User';");
    }

    private void Exec(string sql)
    {
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = sql;
        cmd.ExecuteNonQuery();
    }

    private static string Iso(DateTime dt) => dt.ToUniversalTime().ToString("o");
    private static DateTime ParseIso(string s) =>
        DateTime.Parse(s, null, System.Globalization.DateTimeStyles.RoundtripKind);

    // ---- Roots ----

    public List<WatchedRoot> GetRoots(bool activeOnly = false)
    {
        lock (_gate)
        {
            using var cmd = _conn.CreateCommand();
            cmd.CommandText = "SELECT id, path, added_utc, active, kind FROM roots" +
                              (activeOnly ? " WHERE active=1" : "") + " ORDER BY path";
            using var r = cmd.ExecuteReader();
            var list = new List<WatchedRoot>();
            while (r.Read())
                list.Add(ReadRoot(r));
            return list;
        }
    }

    public WatchedRoot? GetRoot(long id)
    {
        lock (_gate)
        {
            using var cmd = _conn.CreateCommand();
            cmd.CommandText = "SELECT id, path, added_utc, active, kind FROM roots WHERE id=@id";
            cmd.Parameters.AddWithValue("@id", id);
            using var r = cmd.ExecuteReader();
            return r.Read() ? ReadRoot(r) : null;
        }
    }

    /// <summary>Adds a root, or reactivates it if it was watched before (history is preserved).</summary>
    public WatchedRoot AddRoot(string path, RootKind kind = RootKind.User)
    {
        path = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
        lock (_gate)
        {
            using (var up = _conn.CreateCommand())
            {
                up.CommandText = "UPDATE roots SET active=1, kind=@k WHERE path=@p";
                up.Parameters.AddWithValue("@p", path);
                up.Parameters.AddWithValue("@k", kind.ToString());
                up.ExecuteNonQuery();
            }
            using var cmd = _conn.CreateCommand();
            cmd.CommandText = """
                INSERT INTO roots(path, added_utc, active, kind) VALUES(@p, @a, 1, @k)
                ON CONFLICT(path) DO NOTHING;
                SELECT id, path, added_utc, active, kind FROM roots WHERE path=@p;
                """;
            cmd.Parameters.AddWithValue("@p", path);
            cmd.Parameters.AddWithValue("@a", Iso(DateTime.UtcNow));
            cmd.Parameters.AddWithValue("@k", kind.ToString());
            using var r = cmd.ExecuteReader();
            r.Read();
            return ReadRoot(r);
        }
    }

    private static WatchedRoot ReadRoot(SqliteDataReader r) => new(
        r.GetInt64(0), r.GetString(1), ParseIso(r.GetString(2)), r.GetInt64(3) != 0,
        Enum.Parse<RootKind>(r.GetString(4)));

    public void SetRootActive(long id, bool active)
    {
        lock (_gate)
        {
            using var cmd = _conn.CreateCommand();
            cmd.CommandText = "UPDATE roots SET active=@a WHERE id=@id";
            cmd.Parameters.AddWithValue("@a", active ? 1 : 0);
            cmd.Parameters.AddWithValue("@id", id);
            cmd.ExecuteNonQuery();
        }
    }

    // ---- Files ----

    public long GetOrCreateFile(long rootId, string relPath)
    {
        lock (_gate)
        {
            using var cmd = _conn.CreateCommand();
            cmd.CommandText = """
                INSERT INTO files(root_id, rel_path) VALUES(@r, @p)
                ON CONFLICT(root_id, rel_path) DO NOTHING;
                SELECT id FROM files WHERE root_id=@r AND rel_path=@p;
                """;
            cmd.Parameters.AddWithValue("@r", rootId);
            cmd.Parameters.AddWithValue("@p", relPath);
            return (long)cmd.ExecuteScalar()!;
        }
    }

    public FileEntry? FindFile(long rootId, string relPath)
    {
        lock (_gate)
        {
            using var cmd = _conn.CreateCommand();
            cmd.CommandText = "SELECT id, root_id, rel_path FROM files WHERE root_id=@r AND rel_path=@p";
            cmd.Parameters.AddWithValue("@r", rootId);
            cmd.Parameters.AddWithValue("@p", relPath);
            using var rd = cmd.ExecuteReader();
            return rd.Read() ? new FileEntry(rd.GetInt64(0), rd.GetInt64(1), rd.GetString(2)) : null;
        }
    }

    /// <summary>Tracked files whose latest version is not a deletion, under an optional relative prefix.</summary>
    public List<FileEntry> GetLiveFiles(long rootId, string? relPrefix = null)
    {
        lock (_gate)
        {
            using var cmd = _conn.CreateCommand();
            var sql = """
                SELECT f.id, f.root_id, f.rel_path FROM files f
                WHERE f.root_id=@r
                  AND (SELECT v.change FROM versions v WHERE v.file_id=f.id ORDER BY v.id DESC LIMIT 1) <> 'Deleted'
                """;
            if (!string.IsNullOrEmpty(relPrefix))
            {
                sql += " AND (f.rel_path = @pfx OR f.rel_path LIKE @like ESCAPE '!')";
                var escaped = relPrefix.Replace("!", "!!").Replace("%", "!%").Replace("_", "!_");
                cmd.Parameters.AddWithValue("@pfx", relPrefix);
                cmd.Parameters.AddWithValue("@like", escaped + Path.DirectorySeparatorChar + "%");
            }
            cmd.CommandText = sql;
            cmd.Parameters.AddWithValue("@r", rootId);
            using var rd = cmd.ExecuteReader();
            var list = new List<FileEntry>();
            while (rd.Read())
                list.Add(new FileEntry(rd.GetInt64(0), rd.GetInt64(1), rd.GetString(2)));
            return list;
        }
    }

    /// <summary>
    /// Tracked files with their latest state, newest change first. rootId null = all roots;
    /// the optional UTC range keeps only files that had a version captured inside it.
    /// </summary>
    public List<FileListItem> GetFileList(long? rootId = null, string? search = null,
        DateTime? changedFromUtc = null, DateTime? changedToUtc = null)
    {
        lock (_gate)
        {
            using var cmd = _conn.CreateCommand();
            var sql = """
                SELECT f.id, f.root_id, f.rel_path,
                       (SELECT COUNT(*) FROM versions v WHERE v.file_id=f.id),
                       (SELECT v.captured_utc FROM versions v WHERE v.file_id=f.id ORDER BY v.id DESC LIMIT 1) AS last_captured,
                       (SELECT v.change FROM versions v WHERE v.file_id=f.id ORDER BY v.id DESC LIMIT 1)
                FROM files f WHERE 1=1
                """;
            if (rootId.HasValue)
            {
                sql += " AND f.root_id=@r";
                cmd.Parameters.AddWithValue("@r", rootId.Value);
            }
            if (!string.IsNullOrWhiteSpace(search))
            {
                sql += " AND f.rel_path LIKE @s ESCAPE '!'";
                var escaped = search.Trim().Replace("!", "!!").Replace("%", "!%").Replace("_", "!_");
                cmd.Parameters.AddWithValue("@s", "%" + escaped + "%");
            }
            if (changedFromUtc.HasValue || changedToUtc.HasValue)
            {
                // ISO-8601 "o" strings compare lexicographically in chronological order.
                sql += " AND EXISTS(SELECT 1 FROM versions v WHERE v.file_id=f.id";
                if (changedFromUtc.HasValue)
                {
                    sql += " AND v.captured_utc >= @from";
                    cmd.Parameters.AddWithValue("@from", Iso(changedFromUtc.Value));
                }
                if (changedToUtc.HasValue)
                {
                    sql += " AND v.captured_utc < @to";
                    cmd.Parameters.AddWithValue("@to", Iso(changedToUtc.Value));
                }
                sql += ")";
            }
            sql += " ORDER BY last_captured DESC";
            cmd.CommandText = sql;
            using var rd = cmd.ExecuteReader();
            var list = new List<FileListItem>();
            while (rd.Read())
            {
                if (rd.IsDBNull(4)) continue; // file row without versions should not happen; skip defensively
                list.Add(new FileListItem(
                    rd.GetInt64(0), rd.GetInt64(1), rd.GetString(2), (int)rd.GetInt64(3),
                    ParseIso(rd.GetString(4)), Enum.Parse<ChangeType>(rd.GetString(5))));
            }
            return list;
        }
    }

    // ---- Versions ----

    public FileVersion? GetLastVersion(long fileId)
    {
        lock (_gate)
        {
            using var cmd = _conn.CreateCommand();
            cmd.CommandText = VersionSelect + " WHERE file_id=@f ORDER BY id DESC LIMIT 1";
            cmd.Parameters.AddWithValue("@f", fileId);
            using var rd = cmd.ExecuteReader();
            return rd.Read() ? ReadVersion(rd) : null;
        }
    }

    public List<FileVersion> GetVersions(long fileId)
    {
        lock (_gate)
        {
            using var cmd = _conn.CreateCommand();
            cmd.CommandText = VersionSelect + " WHERE file_id=@f ORDER BY id DESC";
            cmd.Parameters.AddWithValue("@f", fileId);
            using var rd = cmd.ExecuteReader();
            var list = new List<FileVersion>();
            while (rd.Read()) list.Add(ReadVersion(rd));
            return list;
        }
    }

    public FileVersion? GetVersion(long versionId)
    {
        lock (_gate)
        {
            using var cmd = _conn.CreateCommand();
            cmd.CommandText = VersionSelect + " WHERE id=@id";
            cmd.Parameters.AddWithValue("@id", versionId);
            using var rd = cmd.ExecuteReader();
            return rd.Read() ? ReadVersion(rd) : null;
        }
    }

    public FileEntry? GetFileForVersion(long versionId)
    {
        lock (_gate)
        {
            using var cmd = _conn.CreateCommand();
            cmd.CommandText = """
                SELECT f.id, f.root_id, f.rel_path FROM files f
                JOIN versions v ON v.file_id = f.id WHERE v.id=@id
                """;
            cmd.Parameters.AddWithValue("@id", versionId);
            using var rd = cmd.ExecuteReader();
            return rd.Read() ? new FileEntry(rd.GetInt64(0), rd.GetInt64(1), rd.GetString(2)) : null;
        }
    }

    public void AddVersion(long fileId, string? hash, long? size, DateTime? mtimeUtc, ChangeType change)
    {
        lock (_gate)
        {
            using var cmd = _conn.CreateCommand();
            cmd.CommandText = """
                INSERT INTO versions(file_id, hash, size, mtime_utc, captured_utc, change)
                VALUES(@f, @h, @s, @m, @c, @ch)
                """;
            cmd.Parameters.AddWithValue("@f", fileId);
            cmd.Parameters.AddWithValue("@h", (object?)hash ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@s", (object?)size ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@m", mtimeUtc.HasValue ? Iso(mtimeUtc.Value) : DBNull.Value);
            cmd.Parameters.AddWithValue("@c", Iso(DateTime.UtcNow));
            cmd.Parameters.AddWithValue("@ch", change.ToString());
            cmd.ExecuteNonQuery();
        }
    }

    public void DeleteVersions(IReadOnlyList<long> versionIds)
    {
        lock (_gate)
        {
            // IDs come from our own reads; inline them in chunks (SQLite parameter limit).
            for (int i = 0; i < versionIds.Count; i += 500)
            {
                using var cmd = _conn.CreateCommand();
                cmd.CommandText = "DELETE FROM versions WHERE id IN (" +
                                  string.Join(",", versionIds.Skip(i).Take(500)) + ")";
                cmd.ExecuteNonQuery();
            }
        }
    }

    /// <summary>Removes a file and its entire version history from the index.</summary>
    public void PurgeFile(long fileId)
    {
        lock (_gate)
        {
            using var cmd = _conn.CreateCommand();
            cmd.CommandText = "DELETE FROM versions WHERE file_id=@f; DELETE FROM files WHERE id=@f;";
            cmd.Parameters.AddWithValue("@f", fileId);
            cmd.ExecuteNonQuery();
        }
    }

    public HashSet<string> GetReferencedHashes()
    {
        lock (_gate)
        {
            using var cmd = _conn.CreateCommand();
            cmd.CommandText = "SELECT DISTINCT hash FROM versions WHERE hash IS NOT NULL";
            using var rd = cmd.ExecuteReader();
            var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            while (rd.Read()) set.Add(rd.GetString(0));
            return set;
        }
    }

    public void Vacuum()
    {
        lock (_gate) Exec("VACUUM;");
    }

    /// <summary>
    /// Writes a consistent point-in-time snapshot of the index to another file using
    /// SQLite's online backup — safe while captures are happening, WAL included.
    /// </summary>
    public void BackupTo(string destinationDbPath)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(destinationDbPath))!);
        lock (_gate)
        {
            using var dest = new SqliteConnection(new SqliteConnectionStringBuilder
            {
                DataSource = destinationDbPath,
                Mode = SqliteOpenMode.ReadWriteCreate
            }.ToString());
            dest.Open();
            _conn.BackupDatabase(dest);
        }
    }

    private const string VersionSelect =
        "SELECT id, file_id, hash, size, mtime_utc, captured_utc, change FROM versions";

    private static FileVersion ReadVersion(SqliteDataReader rd) => new(
        rd.GetInt64(0),
        rd.GetInt64(1),
        rd.IsDBNull(2) ? null : rd.GetString(2),
        rd.IsDBNull(3) ? null : rd.GetInt64(3),
        rd.IsDBNull(4) ? null : ParseIso(rd.GetString(4)),
        ParseIso(rd.GetString(5)),
        Enum.Parse<ChangeType>(rd.GetString(6)));

    // ---- Stats ----

    public (long Files, long Versions) GetStats()
    {
        lock (_gate)
        {
            using var cmd = _conn.CreateCommand();
            cmd.CommandText = "SELECT (SELECT COUNT(*) FROM files), (SELECT COUNT(*) FROM versions)";
            using var rd = cmd.ExecuteReader();
            rd.Read();
            return (rd.GetInt64(0), rd.GetInt64(1));
        }
    }

    public void Dispose() => _conn.Dispose();
}
