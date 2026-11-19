using System.Reflection;
using System.Runtime.InteropServices;

namespace DownloadManager.Data;

/// <summary>
/// Minimal P/Invoke surface over the SQLite C API. No NuGet package is needed: Windows 10 1809+ ships
/// winsqlite3.dll (supported for application use), Linux/macOS use the system libsqlite3.
/// </summary>
internal static class SqliteNative
{
    private const string Lib = "sqlite3";

    public const int Ok = 0, Row = 100, Done = 101;
    public const int OpenReadWrite = 0x2, OpenCreate = 0x4, OpenFullMutex = 0x10000;
    public const int TypeNull = 5;

    static SqliteNative() => NativeLibrary.SetDllImportResolver(typeof(SqliteNative).Assembly, Resolve);

    private static IntPtr Resolve(string name, Assembly assembly, DllImportSearchPath? searchPath)
    {
        if (name != Lib) return IntPtr.Zero;
        string[] candidates = OperatingSystem.IsWindows() ? ["winsqlite3", "sqlite3", "e_sqlite3"]
            : OperatingSystem.IsMacOS() ? ["libsqlite3.dylib", "libe_sqlite3.dylib"]
            : ["libsqlite3.so.0", "libsqlite3.so", "libe_sqlite3.so"];
        foreach (var candidate in candidates)
            if (NativeLibrary.TryLoad(candidate, assembly, searchPath, out var handle)) return handle;
        throw new DllNotFoundException("No SQLite library was found (tried: " + string.Join(", ", candidates) + ").");
    }

    [DllImport(Lib)] public static extern int sqlite3_open_v2([MarshalAs(UnmanagedType.LPUTF8Str)] string filename, out IntPtr db, int flags, IntPtr vfs);
    [DllImport(Lib)] public static extern int sqlite3_close_v2(IntPtr db);
    [DllImport(Lib)] public static extern int sqlite3_prepare_v2(IntPtr db, [MarshalAs(UnmanagedType.LPUTF8Str)] string sql, int nByte, out IntPtr stmt, IntPtr tail);
    [DllImport(Lib)] public static extern int sqlite3_step(IntPtr stmt);
    [DllImport(Lib)] public static extern int sqlite3_finalize(IntPtr stmt);
    [DllImport(Lib)] public static extern int sqlite3_bind_text(IntPtr stmt, int index, [MarshalAs(UnmanagedType.LPUTF8Str)] string value, int n, IntPtr destructor);
    [DllImport(Lib)] public static extern int sqlite3_bind_int64(IntPtr stmt, int index, long value);
    [DllImport(Lib)] public static extern int sqlite3_bind_null(IntPtr stmt, int index);
    [DllImport(Lib)] public static extern IntPtr sqlite3_column_text(IntPtr stmt, int col);
    [DllImport(Lib)] public static extern long sqlite3_column_int64(IntPtr stmt, int col);
    [DllImport(Lib)] public static extern int sqlite3_column_type(IntPtr stmt, int col);
    [DllImport(Lib)] public static extern IntPtr sqlite3_errmsg(IntPtr db);
    [DllImport(Lib)] public static extern int sqlite3_busy_timeout(IntPtr db, int milliseconds);

    public static readonly IntPtr Transient = new(-1);   // SQLITE_TRANSIENT: SQLite copies the bound text
}
