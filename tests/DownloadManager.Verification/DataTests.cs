using DownloadManager.Core.Models;
using DownloadManager.Data;
using DownloadManager.TestServer;

namespace DownloadManager.Verification;

public static class DataTests
{
    private static DownloadItem Sample(string name = "file.bin") => new()
    {
        Url = "https://example.com/a/b/" + name + "?x=1",
        FinalUrl = "https://cdn.example.com/z/" + name,
        FileName = name,
        FilePath = Path.Combine(Path.GetTempPath(), name),
        Category = "Compressed",
        TotalBytes = 123_456_789_012,
        SupportsRanges = true,
        ETag = "\"abc-123\"",
        LastModified = "Tue, 01 Jan 2030 00:00:00 GMT",
        ContentType = "application/zip",
        Status = DownloadStatus.Paused,
        ErrorMessage = "boom",
        Connections = 12,
        SpeedLimitBytesPerSecond = 777,
        SingleStreamBytes = 42,
        CreatedAt = new DateTimeOffset(2026, 11, 14, 10, 20, 30, 123, TimeSpan.FromHours(2)),
        CompletedAt = new DateTimeOffset(2026, 11, 14, 11, 0, 0, TimeSpan.Zero),
        GroupId = "pl:PLabc123",
        GroupTitle = "دورة كاملة — Full Course",
        Segments =
        [
            new SegmentInfo { Index = 0, Start = 0, End = 99, Downloaded = 50 },
            new SegmentInfo { Index = 1, Start = 100, End = 199, Downloaded = 100 },
        ],
    };

    private static void SameItem(DownloadItem a, DownloadItem b)
    {
        Assert.Equal(a.Id, b.Id, "Id"); Assert.Equal(a.Url, b.Url, "Url"); Assert.Equal(a.FinalUrl, b.FinalUrl, "FinalUrl");
        Assert.Equal(a.FileName, b.FileName, "FileName"); Assert.Equal(a.FilePath, b.FilePath, "FilePath"); Assert.Equal(a.Category, b.Category, "Category");
        Assert.Equal(a.TotalBytes, b.TotalBytes, "TotalBytes"); Assert.Equal(a.SupportsRanges, b.SupportsRanges, "SupportsRanges");
        Assert.Equal(a.ETag, b.ETag, "ETag"); Assert.Equal(a.LastModified, b.LastModified, "LastModified"); Assert.Equal(a.ContentType, b.ContentType, "ContentType");
        Assert.Equal(a.Status, b.Status, "Status"); Assert.Equal(a.ErrorMessage, b.ErrorMessage, "ErrorMessage"); Assert.Equal(a.Connections, b.Connections, "Connections");
        Assert.Equal(a.SpeedLimitBytesPerSecond, b.SpeedLimitBytesPerSecond, "SpeedLimit"); Assert.Equal(a.SingleStreamBytes, b.SingleStreamBytes, "SingleStreamBytes");
        Assert.Equal(a.CreatedAt, b.CreatedAt, "CreatedAt"); Assert.Equal(a.CompletedAt, b.CompletedAt, "CompletedAt");
        Assert.Equal(a.GroupId, b.GroupId, "GroupId"); Assert.Equal(a.GroupTitle, b.GroupTitle, "GroupTitle");
        Assert.Equal(a.Segments.Count, b.Segments.Count, "segment count");
        for (var i = 0; i < a.Segments.Count; i++)
        {
            Assert.Equal(a.Segments[i].Index, b.Segments[i].Index, $"seg[{i}].Index"); Assert.Equal(a.Segments[i].Start, b.Segments[i].Start, $"seg[{i}].Start");
            Assert.Equal(a.Segments[i].End, b.Segments[i].End, $"seg[{i}].End"); Assert.Equal(a.Segments[i].Downloaded, b.Segments[i].Downloaded, $"seg[{i}].Downloaded");
        }
    }

    public static IEnumerable<TestCase> All()
    {
        yield return new TestCase("sqlite: every field round-trips, including 64-bit sizes and dates", async env =>
        {
            using var db = SqliteDatabase.Open(Path.Combine(env.Dir, "t.db"));
            var store = new SqliteDownloadStore(db);
            var item = Sample();
            await store.SaveAsync(item);
            SameItem(item, (await store.GetAsync(item.Id))!);
            Assert.True(await store.GetAsync(Guid.NewGuid()) is null, "unknown id returns null");

            var bare = new DownloadItem { Url = "http://x/y", FinalUrl = "http://x/y", FileName = "y", FilePath = "y", TotalBytes = null, CompletedAt = null };
            await store.SaveAsync(bare);
            SameItem(bare, (await store.GetAsync(bare.Id))!);
        });

        yield return new TestCase("sqlite: saving again replaces the segments; delete cascades", async env =>
        {
            using var db = SqliteDatabase.Open(Path.Combine(env.Dir, "t.db"));
            var store = new SqliteDownloadStore(db);
            var item = Sample();
            await store.SaveAsync(item);
            item.Segments = [new SegmentInfo { Index = 0, Start = 0, End = 199, Downloaded = 10 }];
            item.Status = DownloadStatus.Downloading;
            await store.SaveAsync(item);
            SameItem(item, (await store.GetAsync(item.Id))!);
            Assert.Equal(1L, db.QueryScalar<long>("SELECT COUNT(*) FROM segments") , "old segments were replaced");
            Assert.Equal(1, (await store.ListAsync()).Count, "one download");

            await store.DeleteAsync(item.Id);
            Assert.Equal(0L, db.QueryScalar<long>("SELECT COUNT(*) FROM segments"), "segments deleted with the download");
            Assert.Equal(0, (await store.ListAsync()).Count, "list empty");
        });

        yield return new TestCase("sqlite: data survives closing and reopening the file", async env =>
        {
            var path = Path.Combine(env.Dir, "t.db");
            var item = Sample();
            using (var db = SqliteDatabase.Open(path))
            {
                await new SqliteDownloadStore(db).SaveAsync(item);
                new SqliteSettingsStore(db).Set("language", "ar");
            }
            using var again = SqliteDatabase.Open(path);
            SameItem(item, (await new SqliteDownloadStore(again).GetAsync(item.Id))!);
            Assert.Equal("ar", new SqliteSettingsStore(again).Get("language"), "setting persisted");
        });

        yield return new TestCase("sqlite: hostile and non-ASCII text is stored verbatim (no SQL injection)", async env =>
        {
            using var db = SqliteDatabase.Open(Path.Combine(env.Dir, "t.db"));
            var store = new SqliteDownloadStore(db);
            var item = Sample();
            item.FileName = "تقرير'; DROP TABLE downloads;--😀 \"q\" ‏.pdf";
            item.Url = "https://example.com/?q=' OR '1'='1";
            await store.SaveAsync(item);
            var loaded = (await store.GetAsync(item.Id))!;
            Assert.Equal(item.FileName, loaded.FileName, "file name");
            Assert.Equal(item.Url, loaded.Url, "url");
            Assert.Equal(1, (await store.ListAsync()).Count, "table still exists and holds the row");
        });

        yield return new TestCase("sqlite: concurrent saves from many threads are serialised safely", async env =>
        {
            using var db = SqliteDatabase.Open(Path.Combine(env.Dir, "t.db"));
            var store = new SqliteDownloadStore(db);
            var items = Enumerable.Range(0, 8).Select(i => Sample($"f{i}.bin")).ToList();
            await Task.WhenAll(items.Select(item => Task.Run(async () =>
            {
                for (var n = 0; n < 150; n++)
                {
                    item.Segments[0].Downloaded = n;
                    await store.SaveAsync(item.Snapshot());
                }
            })));
            var all = await store.ListAsync();
            Assert.Equal(8, all.Count, "rows");
            Assert.True(all.All(a => a.Segments.Count == 2 && a.Segments[0].Downloaded == 149), "last write wins, segments intact");
        });

        yield return new TestCase("sqlite: settings store overwrites and lists values", _ =>
        {
            using var db = SqliteDatabase.Open(Path.Combine(_.Dir, "t.db"));
            var s = new SqliteSettingsStore(db);
            Assert.True(s.Get("k") is null, "missing key");
            s.Set("k", "1"); s.Set("k", "2"); s.Set("theme", "dark");
            Assert.Equal("2", s.Get("k"), "overwritten");
            Assert.Equal(2, s.GetAll().Count, "all settings");
            return Task.CompletedTask;
        });

        yield return new TestCase("sqlite: a v1 database is migrated to v2 (playlist group columns) without losing rows", async env =>
        {
            var path = Path.Combine(env.Dir, "v1.db");
            Guid id;
            using (var db = SqliteDatabase.Open(path))
            {
                // Simulate an old v1 file: drop the v2 columns by rebuilding the table as it was in v1.
                db.Execute("DROP TABLE segments");
                db.Execute("DROP TABLE downloads");
                db.Execute("""
                    CREATE TABLE downloads (
                        id TEXT PRIMARY KEY, url TEXT NOT NULL, final_url TEXT NOT NULL, file_name TEXT NOT NULL,
                        file_path TEXT NOT NULL, category TEXT NOT NULL, total_bytes INTEGER NULL,
                        supports_ranges INTEGER NOT NULL, etag TEXT NULL, last_modified TEXT NULL, content_type TEXT NULL,
                        status INTEGER NOT NULL, error TEXT NULL, connections INTEGER NOT NULL, speed_limit INTEGER NOT NULL,
                        single_stream_bytes INTEGER NOT NULL, created_at TEXT NOT NULL, completed_at TEXT NULL)
                    """);
                db.Execute("""
                    CREATE TABLE segments (
                        download_id TEXT NOT NULL REFERENCES downloads(id) ON DELETE CASCADE,
                        idx INTEGER NOT NULL, start_byte INTEGER NOT NULL, end_byte INTEGER NOT NULL,
                        downloaded INTEGER NOT NULL, PRIMARY KEY (download_id, idx))
                    """);
                id = Guid.NewGuid();
                db.Execute("INSERT INTO downloads VALUES (?,?,?,?,?,?,?,?,?,?,?,?,?,?,?,?,?,?)",
                    id, "http://x/old.bin", "http://x/old.bin", "old.bin", "old.bin", "General", 10L, 1, null, null, null,
                    (int)DownloadStatus.Completed, null, 4, 0L, 0L, DateTimeOffset.UtcNow, null);
                db.Execute("PRAGMA user_version = 1");
            }

            using var migrated = SqliteDatabase.Open(path);
            Assert.Equal(2L, migrated.QueryScalar<long>("PRAGMA user_version"), "schema upgraded to v2");
            var loaded = (await new SqliteDownloadStore(migrated).GetAsync(id))!;
            Assert.True(loaded is not null && loaded.FileName == "old.bin" && loaded.GroupId is null, "old row kept, no group");
        });

        yield return new TestCase("sqlite: a database from a newer version is refused, not corrupted", _ =>
        {
            var path = Path.Combine(_.Dir, "t.db");
            using (var db = SqliteDatabase.Open(path)) db.Execute("PRAGMA user_version = 99");
            try { SqliteDatabase.Open(path).Dispose(); }
            catch (InvalidOperationException ex) when (ex.Message.Contains("newer version")) { return Task.CompletedTask; }
            throw new AssertionException("opening a newer schema should have been refused");
        });

        yield return new TestCase("sqlite+engine: pause, restart the app, resume (SQLite store)", async env =>
        {
            env.Server.State.SlowChunkDelayMs = 10;
            const long size = 6 * 1024 * 1024;
            var path = Path.Combine(env.Dir, "app.db");
            Guid id;
            using (var db = SqliteDatabase.Open(path))
            {
                var a = env.NewEngine(new SqliteDownloadStore(db));
                var item = await a.AddAsync(new AddDownloadRequest(env.Server.Url("range+slow", size).AbsoluteUri, env.Dir) { Connections = 4 }, env.Ct);
                id = item.Id;
                await a.StartAsync(id, env.Ct);
                await env.WaitForFractionAsync(a, id, 0.3, size);
                await a.PauseAsync(id);
                await a.DisposeAsync();
            }

            using var db2 = SqliteDatabase.Open(path);                  // "new process"
            var b = env.NewEngine(new SqliteDownloadStore(db2));
            await b.RecoverAsync();
            var loaded = b.Get(id)!;
            Assert.True(loaded.Status == DownloadStatus.Paused && loaded.DownloadedBytes > 0 && loaded.DownloadedBytes < size, "progress restored from SQLite");
            await b.StartAsync(id, env.Ct);
            await b.WaitAsync(id, env.Ct);
            Assert.FileMatches(b.Get(id)!, size);
        });

        yield return new TestCase("sqlite+engine: killed mid-download, recovers from the last durable snapshot", async env =>
        {
            env.Server.State.SlowChunkDelayMs = 10;
            const long size = 6 * 1024 * 1024;
            var path = Path.Combine(env.Dir, "app.db");
            Guid id;
            using (var db = SqliteDatabase.Open(path))
            {
                var frozen = new FreezableStore(new SqliteDownloadStore(db));
                var a = env.NewEngine(frozen);
                var item = await a.AddAsync(new AddDownloadRequest(env.Server.Url("range+slow", size).AbsoluteUri, env.Dir) { Connections = 4 }, env.Ct);
                id = item.Id;
                await a.StartAsync(id, env.Ct);
                await env.WaitForFractionAsync(a, id, 0.4, size);
                frozen.Freeze();                                        // the process "dies" here
                await a.DisposeAsync();
            }

            using var db2 = SqliteDatabase.Open(path);
            var b = env.NewEngine(new SqliteDownloadStore(db2));
            var recovered = await b.RecoverAsync();
            Assert.Equal(1, recovered.Count, "recovered");
            Assert.True(recovered[0].Status == DownloadStatus.Paused && recovered[0].DownloadedBytes > 0, "Paused with durable progress");
            await b.StartAsync(id, env.Ct);
            await b.WaitAsync(id, env.Ct);
            Assert.FileMatches(b.Get(id)!, size);
        });
    }
}
