using System.Runtime.InteropServices;

namespace Bridge;

// Windows 11 ships winsqlite3.dll. No SQLite bundle, writable connection, or migration is needed.
public sealed class ReadOnlyDb : IDisposable
{
    private IntPtr db;
    [DllImport("winsqlite3", CallingConvention = CallingConvention.Cdecl)] private static extern int sqlite3_open_v2([MarshalAs(UnmanagedType.LPUTF8Str)] string path, out IntPtr db, int flags, IntPtr vfs);
    [DllImport("winsqlite3", CallingConvention = CallingConvention.Cdecl)] private static extern int sqlite3_close_v2(IntPtr db);
    [DllImport("winsqlite3", CallingConvention = CallingConvention.Cdecl)] private static extern int sqlite3_busy_timeout(IntPtr db, int ms);
    [DllImport("winsqlite3", CallingConvention = CallingConvention.Cdecl)] private static extern int sqlite3_prepare_v2(IntPtr db, [MarshalAs(UnmanagedType.LPUTF8Str)] string sql, int length, out IntPtr statement, IntPtr tail);
    [DllImport("winsqlite3", CallingConvention = CallingConvention.Cdecl)] private static extern int sqlite3_step(IntPtr statement);
    [DllImport("winsqlite3", CallingConvention = CallingConvention.Cdecl)] private static extern int sqlite3_finalize(IntPtr statement);
    [DllImport("winsqlite3", CallingConvention = CallingConvention.Cdecl)] private static extern int sqlite3_bind_int64(IntPtr statement, int index, long value);
    [DllImport("winsqlite3", CallingConvention = CallingConvention.Cdecl)] private static extern long sqlite3_column_int64(IntPtr statement, int index);
    [DllImport("winsqlite3", CallingConvention = CallingConvention.Cdecl)] private static extern int sqlite3_db_readonly(IntPtr db, [MarshalAs(UnmanagedType.LPUTF8Str)] string name);
    public static ReadOnlyDb Open(BridgeConfig config)
    {
        string path = config.DatabaseFile;
        if (path.Length > 0 && !Path.IsPathRooted(path)) path = Path.Combine(config.DataDirectory, path);
        if (path.Length == 0)
        {
            var found = Directory.EnumerateFiles(config.DataDirectory).Where(p => Path.GetExtension(p).ToLowerInvariant() is ".sqlite" or ".db" or ".sqlite3").ToArray();
            if (found.Length != 1) throw new IOException("数据目录中数据库数量不是 1，请在连接设置指定数据库文件");
            path = found[0];
        }
        if (!File.Exists(path)) throw new IOException("数据库文件不存在");
        var result = new ReadOnlyDb();
        if (sqlite3_open_v2(Path.GetFullPath(path), out result.db, 1 | 0x10000, IntPtr.Zero) != 0) { result.Dispose(); throw new IOException("只读打开 SQLite 失败"); }
        try
        {
            sqlite3_busy_timeout(result.db, 1500);
            result.Execute("PRAGMA query_only=ON");
            if (sqlite3_db_readonly(result.db, "main") != 1 || result.Query("PRAGMA query_only")[0] != 1) throw new IOException("SQLite 只读保护未生效");
            if (result.Query("select count(*) from pragma_table_info('usage_events') where name in ('timestamp_ms','total_tokens','failed')")[0] != 3) throw new IOException("SQLite 请求表版本不兼容");
            return result;
        }
        catch { result.Dispose(); throw; }
    }
    private IntPtr Prepare(string sql, long[] values)
    {
        if (sqlite3_prepare_v2(db, sql, -1, out var statement, IntPtr.Zero) != 0) throw new IOException("SQLite 查询失败：锁定、文件不可用或版本不兼容");
        for (int i = 0; i < values.Length; i++) if (sqlite3_bind_int64(statement, i + 1, values[i]) != 0) { sqlite3_finalize(statement); throw new IOException("SQLite 参数绑定失败"); }
        return statement;
    }
    private void Execute(string sql)
    {
        var s = Prepare(sql, []);
        try { if (sqlite3_step(s) is not (100 or 101)) throw new IOException("SQLite 只读操作失败"); }
        finally { sqlite3_finalize(s); }
    }
    public long[] Query(string sql, int columns = 1, params long[] values)
    {
        var s = Prepare(sql, values);
        try
        {
            if (sqlite3_step(s) != 100) throw new IOException("SQLite 数据暂不可用");
            return Enumerable.Range(0, columns).Select(i => sqlite3_column_int64(s, i)).ToArray();
        }
        finally { sqlite3_finalize(s); }
    }
    public WindowStats Statistics(long from, long to)
    {
        // Fail closed if CPAMP retention archived/deleted raw rows in this interval.
        long hasCoverage = Query("select count(*) from sqlite_master where type='table' and name='usage_archive_deleted_coverage_daily'")[0];
        if (hasCoverage > 0 && Query("select count(*) from usage_archive_deleted_coverage_daily where min_timestamp_ms < ? and max_timestamp_ms >= ?", 1, to, from)[0] > 0)
            throw new IOException("原始历史已归档，需配置 CPAMP API 获取完整统计");
        var r = Query("select coalesce(sum(total_tokens),0),count(*),coalesce(sum(case when failed=0 then 1 else 0 end),0),coalesce(sum(case when failed=1 then 1 else 0 end),0),coalesce(sum(case when failed not in(0,1) or failed is null then 1 else 0 end),0) from usage_events where timestamp_ms>=? and timestamp_ms<?", 5, from, to);
        if (r[4] != 0 || r[1] != r[2] + r[3] || r[0] < 0) throw new InvalidDataException("SQLite 请求状态未知，不能推断成功率");
        return new(r[0], null, r[1], r[2], r[3], from, to, "SQLite usage_events read-only (cost unavailable)");
    }
    public void Dispose() { if (db != IntPtr.Zero) sqlite3_close_v2(db); db = IntPtr.Zero; }
}
