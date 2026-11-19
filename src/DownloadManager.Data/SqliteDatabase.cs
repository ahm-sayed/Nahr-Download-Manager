using System.Globalization;
using System.Runtime.InteropServices;

namespace DownloadManager.Data;

public sealed class SqliteException(int code, string message) : Exception($"SQLite error {code}: {message}")
{
    public int Code { get; } = code;
}

/// <summary>One SQLite connection guarded by a lock. Every public call is atomic with respect to the others.</summary>
public sealed class SqliteDatabase : IDisposable
{
    public const int CurrentSchemaVersion = 2;

    private readonly object _gate = new();
    private IntPtr _db;

    private SqliteDatabase(IntPtr db) => _db = db;

    /// <summary>Opens (creating if needed) the database file, enables WAL and applies migrations.</summary>
    public static SqliteDatabase Open(string path)
    {
        var dir = Path.GetDirectoryName(Path.GetFullPath(path));
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

        var rc = SqliteNative.sqlite3_open_v2(path, out var handle,
            SqliteNative.OpenReadWrite | SqliteNative.OpenCreate | SqliteNative.OpenFullMutex, IntPtr.Zero);
        if (rc != SqliteNative.Ok)
        {
            var message = handle == IntPtr.Zero ? "could not open database" : ErrorMessage(handle);
            if (handle != IntPtr.Zero) SqliteNative.sqlite3_close_v2(handle);
            throw new SqliteException(rc, message);
        }

        var db = new SqliteDatabase(handle);
        try
        {
            SqliteNative.sqlite3_busy_timeout(handle, 5000);
            db.Execute("PRAGMA journal_mode=WAL");
            db.Execute("PRAGMA synchronous=NORMAL");
            db.Execute("PRAGMA foreign_keys=ON");
            db.Migrate();
            return db;
        }
        catch
        {
            db.Dispose();
            throw;
        }
    }

    private void Migrate()
    {
        var version = (int)(QueryScalar<long?>("PRAGMA user_version") ?? 0);
        if (version > CurrentSchemaVersion)
            throw new InvalidOperationException(
                $"The database was created by a newer version of the application (schema {version}, this build understands {CurrentSchemaVersion}).");

        if (version < 1)
        {
            Transaction(() =>
            {
                Execute("""
                    CREATE TABLE IF NOT EXISTS downloads (
                        id TEXT PRIMARY KEY, url TEXT NOT NULL, final_url TEXT NOT NULL, file_name TEXT NOT NULL,
                        file_path TEXT NOT NULL, category TEXT NOT NULL, total_bytes INTEGER NULL,
                        supports_ranges INTEGER NOT NULL, etag TEXT NULL, last_modified TEXT NULL, content_type TEXT NULL,
                        status INTEGER NOT NULL, error TEXT NULL, connections INTEGER NOT NULL, speed_limit INTEGER NOT NULL,
                        single_stream_bytes INTEGER NOT NULL, created_at TEXT NOT NULL, completed_at TEXT NULL)
                    """);
                Execute("""
                    CREATE TABLE IF NOT EXISTS segments (
                        download_id TEXT NOT NULL REFERENCES downloads(id) ON DELETE CASCADE,
                        idx INTEGER NOT NULL, start_byte INTEGER NOT NULL, end_byte INTEGER NOT NULL,
                        downloaded INTEGER NOT NULL, PRIMARY KEY (download_id, idx))
                    """);
                Execute("CREATE TABLE IF NOT EXISTS settings (key TEXT PRIMARY KEY, value TEXT NOT NULL)");
                Execute("PRAGMA user_version = 1");
            });
            version = 1;
        }

        if (version < 2)
        {
            // v2: playlist / group membership so grouped downloads are shown nested after a restart.
            Transaction(() =>
            {
                Execute("ALTER TABLE downloads ADD COLUMN group_id TEXT NULL");
                Execute("ALTER TABLE downloads ADD COLUMN group_title TEXT NULL");
                Execute("PRAGMA user_version = 2");
            });
        }
        // Future schema versions: add "if (version < 3) { ... }" blocks here.
    }

    public int Execute(string sql, params object?[] args)
    {
        lock (_gate)
        {
            var stmt = Prepare(sql, args);
            try
            {
                var rc = SqliteNative.sqlite3_step(stmt);
                while (rc == SqliteNative.Row) rc = SqliteNative.sqlite3_step(stmt);
                if (rc != SqliteNative.Done) throw Error(rc);
                return 0;
            }
            finally
            {
                SqliteNative.sqlite3_finalize(stmt);
            }
        }
    }

    public List<T> Query<T>(string sql, Func<SqliteRow, T> map, params object?[] args)
    {
        lock (_gate)
        {
            var stmt = Prepare(sql, args);
            try
            {
                var result = new List<T>();
                int rc;
                while ((rc = SqliteNative.sqlite3_step(stmt)) == SqliteNative.Row) result.Add(map(new SqliteRow(stmt)));
                if (rc != SqliteNative.Done) throw Error(rc);
                return result;
            }
            finally
            {
                SqliteNative.sqlite3_finalize(stmt);
            }
        }
    }

    public T? QueryScalar<T>(string sql, params object?[] args)
    {
        var rows = Query(sql, r => r.IsNull(0) ? default : (T)Convert.ChangeType(r.GetInt64(0), Nullable.GetUnderlyingType(typeof(T)) ?? typeof(T)), args);
        return rows.Count == 0 ? default : rows[0];
    }

    /// <summary>Runs <paramref name="action"/> in one transaction; the lock is held throughout, so it is atomic for other threads.</summary>
    public void Transaction(Action action)
    {
        lock (_gate)
        {
            Execute("BEGIN IMMEDIATE");
            try
            {
                action();
                Execute("COMMIT");
            }
            catch
            {
                try { Execute("ROLLBACK"); } catch (SqliteException) { /* connection already rolled back */ }
                throw;
            }
        }
    }

    private IntPtr Prepare(string sql, object?[] args)
    {
        ObjectDisposedException.ThrowIf(_db == IntPtr.Zero, this);
        var rc = SqliteNative.sqlite3_prepare_v2(_db, sql, -1, out var stmt, IntPtr.Zero);
        if (rc != SqliteNative.Ok) throw Error(rc);
        try
        {
            for (var i = 0; i < args.Length; i++) Bind(stmt, i + 1, args[i]);
            return stmt;
        }
        catch
        {
            SqliteNative.sqlite3_finalize(stmt);
            throw;
        }
    }

    private void Bind(IntPtr stmt, int index, object? value)
    {
        var rc = value switch
        {
            null => SqliteNative.sqlite3_bind_null(stmt, index),
            string s => SqliteNative.sqlite3_bind_text(stmt, index, s, -1, SqliteNative.Transient),
            bool b => SqliteNative.sqlite3_bind_int64(stmt, index, b ? 1 : 0),
            int i => SqliteNative.sqlite3_bind_int64(stmt, index, i),
            long l => SqliteNative.sqlite3_bind_int64(stmt, index, l),
            Enum e => SqliteNative.sqlite3_bind_int64(stmt, index, Convert.ToInt64(e, CultureInfo.InvariantCulture)),
            Guid g => SqliteNative.sqlite3_bind_text(stmt, index, g.ToString("D"), -1, SqliteNative.Transient),
            DateTimeOffset d => SqliteNative.sqlite3_bind_text(stmt, index, d.ToString("O", CultureInfo.InvariantCulture), -1, SqliteNative.Transient),
            _ => throw new ArgumentException($"Unsupported SQLite parameter type {value.GetType().Name}."),
        };
        if (rc != SqliteNative.Ok) throw Error(rc);
    }

    private SqliteException Error(int rc) => new(rc, ErrorMessage(_db));

    private static string ErrorMessage(IntPtr db) => Marshal.PtrToStringUTF8(SqliteNative.sqlite3_errmsg(db)) ?? "unknown error";

    public void Dispose()
    {
        lock (_gate)
        {
            if (_db == IntPtr.Zero) return;
            SqliteNative.sqlite3_close_v2(_db);
            _db = IntPtr.Zero;
        }
    }
}

public readonly struct SqliteRow
{
    private readonly IntPtr _stmt;
    internal SqliteRow(IntPtr stmt) => _stmt = stmt;

    public bool IsNull(int col) => SqliteNative.sqlite3_column_type(_stmt, col) == SqliteNative.TypeNull;
    public long GetInt64(int col) => SqliteNative.sqlite3_column_int64(_stmt, col);
    public long? GetNullableInt64(int col) => IsNull(col) ? null : GetInt64(col);
    public string GetString(int col) => Marshal.PtrToStringUTF8(SqliteNative.sqlite3_column_text(_stmt, col)) ?? "";
    public string? GetNullableString(int col) => IsNull(col) ? null : GetString(col);
}
