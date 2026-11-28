using Microsoft.Data.Sqlite;

namespace FF.AgentCollaboration.Infrastructure.Storage;

// P0 与工作台共用投递的认领条件和短事务。各自拥有业务表和数据库文件。
internal abstract class LocalState : IDisposable
{
    private readonly SqliteConnection database;

    protected LocalState(string path, bool create)
    {
        if (create && !OperatingSystem.IsWindows() && !File.Exists(path))
        {
            try
            {
                using var privateFile = new FileStream(path, new FileStreamOptions
                {
                    Mode = FileMode.CreateNew, Access = FileAccess.Write,
                    UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite
                });
            }
            catch (IOException) when (File.Exists(path)) { /* SQLite coordinates existing database access. */ }
        }
        database = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = path,
            Mode = create ? SqliteOpenMode.ReadWriteCreate : SqliteOpenMode.ReadWrite,
            Pooling = false,
            DefaultTimeout = 10
        }.ToString());
        database.Open();
    }

    private SqliteCommand Command(string sql, object?[] values)
    {
        var command = database.CreateCommand();
        command.CommandText = sql;
        for (int index = 0; index < values.Length; index++)
            command.Parameters.AddWithValue("$p" + index, values[index] ?? DBNull.Value);
        return command;
    }

    protected int Exec(string sql, params object?[] values)
    {
        using var command = Command(sql, values);
        return command.ExecuteNonQuery();
    }

    protected List<T> Read<T>(string sql, Func<SqliteDataReader, T> map, params object?[] values)
    {
        using var command = Command(sql, values);
        using var rows = command.ExecuteReader();
        var result = new List<T>();
        while (rows.Read()) result.Add(map(rows));
        return result;
    }

    protected void Atomic(Action action)
    {
        Exec("BEGIN IMMEDIATE");
        try
        {
            action();
            Exec("COMMIT");
        }
        catch
        {
            Exec("ROLLBACK");
            throw;
        }
    }

    protected static string? Optional(SqliteDataReader row, int column) =>
        row.IsDBNull(column) ? null : row.GetString(column);

    internal bool Claim(string eventId) =>
        Exec("UPDATE events SET state='in_flight', attempts=attempts+1 WHERE id=$p0 AND state='pending'", eventId) == 1;

    internal void Started(string eventId, string? turn) =>
        Exec("""
            UPDATE events SET started=coalesce(started,CURRENT_TIMESTAMP), turn=coalesce(turn,$p1)
            WHERE id=$p0 AND state='in_flight'
            """, eventId, turn);

    public void Dispose() => database.Dispose();
}
