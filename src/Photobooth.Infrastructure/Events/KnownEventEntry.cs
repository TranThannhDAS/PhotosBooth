namespace Photobooth.Infrastructure.Events;

/// <summary>
/// Sự kiện từng mở trên máy, truy vấn từ bảng Events trong SQLite.
/// Kiểu DTO cho thư viện/bộ lọc sự kiện, không thuộc app-state.json.
/// </summary>
public sealed record KnownEventEntry(Guid EventId, string Name, string Folder, DateTimeOffset LastOpenedAt);
