using System.Globalization;
using DownloadManager.Core.Abstractions;
using DownloadManager.Core.Models;

namespace DownloadManager.Data;

/// <summary>Durable download state in SQLite. Each save is one transaction, so a crash never leaves half a snapshot.</summary>
public sealed class SqliteDownloadStore(SqliteDatabase db) : IDownloadStore
{
    private const string Columns =
        "id, url, final_url, file_name, file_path, category, total_bytes, supports_ranges, etag, last_modified, content_type, " +
        "status, error, connections, speed_limit, single_stream_bytes, created_at, completed_at, group_id, group_title";

    public Task SaveAsync(DownloadItem s, CancellationToken ct = default)
    {
        // Use the item's sanitised snapshot — sensitive header values are redacted before persisting.
        var sanitized = s.SanitisedSnapshot();
        db.Transaction(() =>
        {
            db.Execute($"INSERT OR REPLACE INTO downloads ({Columns}) VALUES (?,?,?,?,?,?,?,?,?,?,?,?,?,?,?,?,?,?,?,?)",
                sanitized.Id, sanitized.Url, sanitized.FinalUrl, sanitized.FileName, sanitized.FilePath, sanitized.Category, sanitized.TotalBytes, sanitized.SupportsRanges, sanitized.ETag, sanitized.LastModified,
                sanitized.ContentType, (int)sanitized.Status, sanitized.ErrorMessage, sanitized.Connections, sanitized.SpeedLimitBytesPerSecond, sanitized.SingleStreamBytes,
                sanitized.CreatedAt, sanitized.CompletedAt, sanitized.GroupId, sanitized.GroupTitle);
            db.Execute("DELETE FROM segments WHERE download_id = ?", sanitized.Id);
            foreach (var seg in sanitized.Segments)
                db.Execute("INSERT INTO segments (download_id, idx, start_byte, end_byte, downloaded) VALUES (?,?,?,?,?)",
                    sanitized.Id, seg.Index, seg.Start, seg.End, seg.Downloaded);
        });
        return Task.CompletedTask;
    }

    public Task<DownloadItem?> GetAsync(Guid id, CancellationToken ct = default)
    {
        var item = db.Query($"SELECT {Columns} FROM downloads WHERE id = ?", Map, id).FirstOrDefault();
        if (item is not null) item.Segments = LoadSegments(id);
        return Task.FromResult(item);
    }

    public Task<IReadOnlyList<DownloadItem>> ListAsync(CancellationToken ct = default)
    {
        var items = db.Query($"SELECT {Columns} FROM downloads ORDER BY created_at, id", Map);
        var segments = db.Query("SELECT download_id, idx, start_byte, end_byte, downloaded FROM segments ORDER BY download_id, idx",
            r => (Id: Guid.Parse(r.GetString(0)), Seg: new SegmentInfo { Index = (int)r.GetInt64(1), Start = r.GetInt64(2), End = r.GetInt64(3), Downloaded = r.GetInt64(4) }));
        var byId = segments.GroupBy(x => x.Id).ToDictionary(g => g.Key, g => g.Select(x => x.Seg).ToList());
        foreach (var item in items)
            item.Segments = byId.GetValueOrDefault(item.Id) ?? new List<SegmentInfo>();
        return Task.FromResult<IReadOnlyList<DownloadItem>>(items);
    }

    public Task DeleteAsync(Guid id, CancellationToken ct = default)
    {
        db.Execute("DELETE FROM downloads WHERE id = ?", id);   // segments go away through ON DELETE CASCADE
        return Task.CompletedTask;
    }

    private List<SegmentInfo> LoadSegments(Guid id) =>
        db.Query("SELECT idx, start_byte, end_byte, downloaded FROM segments WHERE download_id = ? ORDER BY idx",
            r => new SegmentInfo { Index = (int)r.GetInt64(0), Start = r.GetInt64(1), End = r.GetInt64(2), Downloaded = r.GetInt64(3) }, id);

    private static DownloadItem Map(SqliteRow r) => new()
    {
        Id = Guid.Parse(r.GetString(0)),
        Url = r.GetString(1),
        FinalUrl = r.GetString(2),
        FileName = r.GetString(3),
        FilePath = r.GetString(4),
        Category = r.GetString(5),
        TotalBytes = r.GetNullableInt64(6),
        SupportsRanges = r.GetInt64(7) != 0,
        ETag = r.GetNullableString(8),
        LastModified = r.GetNullableString(9),
        ContentType = r.GetNullableString(10),
        Status = (DownloadStatus)r.GetInt64(11),
        ErrorMessage = r.GetNullableString(12),
        Connections = (int)r.GetInt64(13),
        SpeedLimitBytesPerSecond = r.GetInt64(14),
        SingleStreamBytes = r.GetInt64(15),
        CreatedAt = DateTimeOffset.Parse(r.GetString(16), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind),
        CompletedAt = r.GetNullableString(17) is { } c ? DateTimeOffset.Parse(c, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind) : null,
        GroupId = r.GetNullableString(18),
        GroupTitle = r.GetNullableString(19),
    };
}

public sealed class SqliteSettingsStore(SqliteDatabase db) : ISettingsStore
{
    public string? Get(string key) => db.Query("SELECT value FROM settings WHERE key = ?", r => r.GetString(0), key).FirstOrDefault();

    public void Set(string key, string value) => db.Execute("INSERT OR REPLACE INTO settings (key, value) VALUES (?, ?)", key, value);

    public IReadOnlyDictionary<string, string> GetAll() =>
        db.Query("SELECT key, value FROM settings", r => (r.GetString(0), r.GetString(1))).ToDictionary(x => x.Item1, x => x.Item2);
}
