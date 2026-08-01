using System.Collections.Concurrent;
using Microsoft.Data.Sqlite;

namespace ImageClassification.VectorStore;

/// <summary>
/// Simple pool of SqliteConnection instances for concurrent read access.
/// SQLite in WAL mode supports multiple simultaneous readers; writes
/// are serialised at the database level.  The pool avoids creating a new
/// connection on every operation while keeping the count bounded.
/// </summary>
internal sealed class SqliteConnectionPool : IDisposable
{
    private readonly string _connectionString;
    private readonly ConcurrentBag<SqliteConnection> _pool = new();
    private int _totalCreated;
    private int _disposed;

    private const int MaxPoolSize = 4;

    public SqliteConnectionPool(string dbPath)
    {
        var dir = Path.GetDirectoryName(dbPath);
        if (!string.IsNullOrEmpty(dir))
            Directory.CreateDirectory(dir);

        _connectionString = $"Data Source={dbPath}";
    }

    public SqliteConnection Rent()
    {
        if (_pool.TryTake(out var conn))
        {
            if (conn.State == System.Data.ConnectionState.Open)
                return conn;
            conn.Dispose();
        }

        var newConn = new SqliteConnection(_connectionString);
        newConn.Open();
        Interlocked.Increment(ref _totalCreated);
        return newConn;
    }

    public void Return(SqliteConnection conn)
    {
        if (_pool.Count < MaxPoolSize && Volatile.Read(ref _disposed) == 0)
            _pool.Add(conn);
        else
            conn.Dispose();
    }

    public void Dispose()
    {
        Interlocked.Exchange(ref _disposed, 1);
        while (_pool.TryTake(out var conn))
            conn.Dispose();
    }
}
