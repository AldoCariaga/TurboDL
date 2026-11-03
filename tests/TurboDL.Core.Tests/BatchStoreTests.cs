using Microsoft.Data.Sqlite;
using TurboDL.Core.Model;
using TurboDL.Core.Storage;

namespace TurboDL.Core.Tests;

public sealed class BatchStoreTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "TurboDL.BatchStoreTests." + Guid.NewGuid().ToString("N"));
    private string DbPath => Path.Combine(_root, "turbodl.db");

    public BatchStoreTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    [Fact]
    public void AddSaveLoad_RoundTrips()
    {
        var created = new DateTime(2026, 11, 3, 8, 0, 0, DateTimeKind.Utc);
        using (var store = new DownloadStore(DbPath))
        {
            var batch = new BatchInfo { Name = "testdata", Directory = @"C:\dl\testdata ✓", DeleteParts = true, CreatedUtc = created };
            store.AddBatch(batch);
            Assert.True(batch.Id > 0);

            var other = new BatchInfo { Name = "other", Directory = @"C:\dl\other" };
            store.AddBatch(other);
            Assert.NotEqual(batch.Id, other.Id);

            batch.Retries[5] = 2;
            batch.Status = BatchStatus.Extracted;
            batch.Message = "Extracted 1 archive.";
            batch.ExtractedTo.Add(@"C:\dl\testdata ✓\testdata");
            batch.FinishedUtc = created.AddMinutes(5);
            store.SaveBatch(batch);
        }

        using (var store = new DownloadStore(DbPath))
        {
            var loaded = store.LoadBatches();
            Assert.Equal(["testdata", "other"], loaded.Select(b => b.Name));

            var b = loaded[0];
            Assert.Equal(@"C:\dl\testdata ✓", b.Directory);
            Assert.True(b.AutoExtract);
            Assert.True(b.DeleteParts);
            Assert.Equal(BatchStatus.Extracted, b.Status);
            Assert.Equal("Extracted 1 archive.", b.Message);
            Assert.Equal([@"C:\dl\testdata ✓\testdata"], b.ExtractedTo);
            Assert.Equal(new Dictionary<long, int> { [5] = 2 }, b.Retries);
            Assert.Equal(created, b.CreatedUtc);
            Assert.Equal(created.AddMinutes(5), b.FinishedUtc);

            Assert.Equal(BatchStatus.Downloading, loaded[1].Status);
            Assert.Empty(loaded[1].Retries);
        }
    }

    [Fact]
    public void Delete_RemovesOnlyThatBatch()
    {
        using var store = new DownloadStore(DbPath);
        var a = new BatchInfo { Name = "a", Directory = @"C:\a" };
        var b = new BatchInfo { Name = "b", Directory = @"C:\b" };
        store.AddBatch(a);
        store.AddBatch(b);

        store.DeleteBatch(a.Id);

        Assert.Equal([b.Id], store.LoadBatches().Select(x => x.Id));
    }

    [Fact]
    public void OldDatabase_WithoutTheBatchesTable_OpensFine()
    {
        using (var conn = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = DbPath, Pooling = false }.ToString()))
        {
            conn.Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                CREATE TABLE downloads (
                    id          INTEGER PRIMARY KEY AUTOINCREMENT,
                    status      TEXT NOT NULL,
                    created_utc TEXT NOT NULL,
                    data        TEXT NOT NULL
                );
                INSERT INTO downloads (status, created_utc, data)
                VALUES ('Completed', '2026-01-01T00:00:00.0000000Z', '{"Url":"https://example.com/a.zip","FileName":"a.zip","Status":"Completed"}');
                """;
            cmd.ExecuteNonQuery();
        }

        using var store = new DownloadStore(DbPath);

        Assert.Empty(store.LoadBatches());
        var old = Assert.Single(store.LoadAll());
        Assert.Equal("a.zip", old.FileName);

        var batch = new BatchInfo { Name = "new", Directory = @"C:\new" };
        store.AddBatch(batch);
        Assert.Equal(["new"], store.LoadBatches().Select(x => x.Name));
    }
}
