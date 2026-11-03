using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Data.Sqlite;
using TurboDL.Core.Model;

namespace TurboDL.Core.Storage;

/// <summary>
/// Saves downloads in SQLite. Each row holds the whole <see cref="DownloadInfo"/> as JSON (segments included),
/// plus a couple of plain columns for sorting and filtering. Batches live in their own table, also as JSON.
/// </summary>
public sealed class DownloadStore : IDisposable
{
    private static readonly JsonSerializerOptions Json = new()
    {
        Converters = { new JsonStringEnumConverter() },
    };

    private readonly SqliteConnection _conn;
    private readonly Lock _gate = new();

    public static string DefaultPath =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "TurboDL", "turbodl.db");

    public DownloadStore(string? path = null)
    {
        path ??= DefaultPath;
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        _conn = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = path,
            Pooling = false,
        }.ToString());
        _conn.Open();
        Execute("PRAGMA journal_mode=WAL;");
        Execute("""
            CREATE TABLE IF NOT EXISTS downloads (
                id          INTEGER PRIMARY KEY AUTOINCREMENT,
                status      TEXT NOT NULL,
                created_utc TEXT NOT NULL,
                data        TEXT NOT NULL
            );
            """);
        Execute("CREATE TABLE IF NOT EXISTS batches (id INTEGER PRIMARY KEY AUTOINCREMENT, data TEXT NOT NULL);");
    }

    public void Add(DownloadInfo info)
    {
        lock (_gate)
        {
            using var cmd = _conn.CreateCommand();
            cmd.CommandText = "INSERT INTO downloads (status, created_utc, data) VALUES ($s, $c, '{}'); SELECT last_insert_rowid();";
            cmd.Parameters.AddWithValue("$s", info.Status.ToString());
            cmd.Parameters.AddWithValue("$c", info.CreatedUtc.ToString("O"));
            info.Id = (long)cmd.ExecuteScalar()!;
            SaveLocked(info);
        }
    }

    public void Save(DownloadInfo info)
    {
        lock (_gate) SaveLocked(info);
    }

    public void Delete(long id)
    {
        lock (_gate)
        {
            using var cmd = _conn.CreateCommand();
            cmd.CommandText = "DELETE FROM downloads WHERE id = $id";
            cmd.Parameters.AddWithValue("$id", id);
            cmd.ExecuteNonQuery();
        }
    }

    public List<DownloadInfo> LoadAll()
    {
        lock (_gate)
        {
            using var cmd = _conn.CreateCommand();
            cmd.CommandText = "SELECT id, data FROM downloads ORDER BY id";
            using var reader = cmd.ExecuteReader();
            var list = new List<DownloadInfo>();
            while (reader.Read())
            {
                var info = JsonSerializer.Deserialize<DownloadInfo>(reader.GetString(1), Json);
                if (info is null) continue;
                info.Id = reader.GetInt64(0);
                list.Add(info);
            }
            return list;
        }
    }

    /// <summary>Stores a new batch and sets its <see cref="BatchInfo.Id"/>.</summary>
    public void AddBatch(BatchInfo batch)
    {
        lock (_gate)
        {
            using var cmd = _conn.CreateCommand();
            cmd.CommandText = "INSERT INTO batches (data) VALUES ('{}'); SELECT last_insert_rowid();";
            batch.Id = (long)cmd.ExecuteScalar()!;
            SaveBatchLocked(batch);
        }
    }

    /// <summary>Writes the batch's current state over its row.</summary>
    public void SaveBatch(BatchInfo batch)
    {
        lock (_gate) SaveBatchLocked(batch);
    }

    /// <summary>Removes the batch's row. Its downloads are left alone.</summary>
    public void DeleteBatch(long id)
    {
        lock (_gate)
        {
            using var cmd = _conn.CreateCommand();
            cmd.CommandText = "DELETE FROM batches WHERE id = $id";
            cmd.Parameters.AddWithValue("$id", id);
            cmd.ExecuteNonQuery();
        }
    }

    /// <summary>Every stored batch, oldest first.</summary>
    public List<BatchInfo> LoadBatches()
    {
        lock (_gate)
        {
            using var cmd = _conn.CreateCommand();
            cmd.CommandText = "SELECT id, data FROM batches ORDER BY id";
            using var reader = cmd.ExecuteReader();
            var list = new List<BatchInfo>();
            while (reader.Read())
            {
                var batch = JsonSerializer.Deserialize<BatchInfo>(reader.GetString(1), Json);
                if (batch is null) continue;
                batch.Id = reader.GetInt64(0);
                list.Add(batch);
            }
            return list;
        }
    }

    public void Dispose() => _conn.Dispose();

    private void SaveBatchLocked(BatchInfo batch)
    {
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = "UPDATE batches SET data = $d WHERE id = $id";
        cmd.Parameters.AddWithValue("$d", JsonSerializer.Serialize(batch, Json));
        cmd.Parameters.AddWithValue("$id", batch.Id);
        cmd.ExecuteNonQuery();
    }

    private void SaveLocked(DownloadInfo info)
    {
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = "UPDATE downloads SET status = $s, data = $d WHERE id = $id";
        cmd.Parameters.AddWithValue("$s", info.Status.ToString());
        cmd.Parameters.AddWithValue("$d", JsonSerializer.Serialize(info, Json));
        cmd.Parameters.AddWithValue("$id", info.Id);
        cmd.ExecuteNonQuery();
    }

    private void Execute(string sql)
    {
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = sql;
        cmd.ExecuteNonQuery();
    }
}
