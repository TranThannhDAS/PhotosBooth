using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Unicode;

namespace Photobooth.Infrastructure.Storage;

/// <summary>
/// Các lựa chọn JSON dùng chung cho file template, marker sự kiện và metadata tương thích.
/// Không đọc/ghi app-state.json; lựa chọn máy in/camera và vùng hướng dẫn nằm trong SQLite.
/// </summary>
public static class PhotoboothJsonOptions
{
    public static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
        Encoder = JavaScriptEncoder.Create(UnicodeRanges.All),
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
    };
}
