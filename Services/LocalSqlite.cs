using System.Runtime.InteropServices;

namespace CodexAccountBar.Services;

internal sealed class LocalSqlite : IDisposable
{
    #region Fields

    private IntPtr _database;

    #endregion

    #region Initialization

    public LocalSqlite(string path)
    {
        if (sqlite3_open_v2(path, out _database, 1, IntPtr.Zero) != 0)
        {
            Dispose();
            throw new IOException("Could not open the local Codex database for reading.");
        }
        sqlite3_busy_timeout(_database, 1500);
    }

    #endregion

    #region Public Methods

    public List<string?[]> Query(string sql, params string[] parameters)
    {
        if (sqlite3_prepare_v2(_database, sql, -1, out var statement, IntPtr.Zero) != 0)
            throw new InvalidDataException("The local Codex database schema is not supported.");
        try
        {
            for (var index = 0; index < parameters.Length; index++)
                if (sqlite3_bind_text(statement, index + 1, parameters[index], -1, new IntPtr(-1)) != 0)
                    throw new InvalidDataException("Could not prepare the local log query.");
            var rows = new List<string?[]>();
            int result;
            while ((result = sqlite3_step(statement)) == 100)
            {
                var values = new string?[sqlite3_column_count(statement)];
                for (var index = 0; index < values.Length; index++)
                    values[index] = Marshal.PtrToStringUTF8(sqlite3_column_text(statement, index));
                rows.Add(values);
            }
            if (result != 101) throw new IOException("Could not read Codex logs. The database may be busy; try Refresh.");
            return rows;
        }
        finally { sqlite3_finalize(statement); }
    }

    public void Dispose()
    {
        if (_database == IntPtr.Zero) return;
        sqlite3_close(_database);
        _database = IntPtr.Zero;
    }

    #endregion

    #region Native Database Methods

    [DllImport("winsqlite3.dll", CallingConvention = CallingConvention.Cdecl)]
    private static extern int sqlite3_open_v2([MarshalAs(UnmanagedType.LPUTF8Str)] string filename, out IntPtr database, int flags, IntPtr vfs);

    [DllImport("winsqlite3.dll", CallingConvention = CallingConvention.Cdecl)]
    private static extern int sqlite3_close(IntPtr database);

    [DllImport("winsqlite3.dll", CallingConvention = CallingConvention.Cdecl)]
    private static extern int sqlite3_busy_timeout(IntPtr database, int milliseconds);

    [DllImport("winsqlite3.dll", CallingConvention = CallingConvention.Cdecl)]
    private static extern int sqlite3_prepare_v2(IntPtr database, [MarshalAs(UnmanagedType.LPUTF8Str)] string sql, int bytes, out IntPtr statement, IntPtr tail);

    [DllImport("winsqlite3.dll", CallingConvention = CallingConvention.Cdecl)]
    private static extern int sqlite3_bind_text(IntPtr statement, int index, [MarshalAs(UnmanagedType.LPUTF8Str)] string value, int bytes, IntPtr destructor);

    [DllImport("winsqlite3.dll", CallingConvention = CallingConvention.Cdecl)]
    private static extern int sqlite3_step(IntPtr statement);

    [DllImport("winsqlite3.dll", CallingConvention = CallingConvention.Cdecl)]
    private static extern int sqlite3_column_count(IntPtr statement);

    [DllImport("winsqlite3.dll", CallingConvention = CallingConvention.Cdecl)]
    private static extern IntPtr sqlite3_column_text(IntPtr statement, int index);

    [DllImport("winsqlite3.dll", CallingConvention = CallingConvention.Cdecl)]
    private static extern int sqlite3_finalize(IntPtr statement);

    #endregion
}
