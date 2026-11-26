using System.Reflection;
using EDSDKLib;

namespace Photobooth.Camera.Canon.Native;

/// <summary>Tên mã lỗi EDSDK để ghi log (lấy từ các hằng số <c>EDS_ERR_*</c> trong EDSDK.cs).</summary>
internal static class EdsErrors
{
    /// <summary>Có trong EDSDKErrors.h 13.21.10 nhưng sample C# chưa khai báo: máy ảnh đang ở chế độ quay phim/đặc biệt.</summary>
    public const uint TakePictureSpecialMovieModeNg = 0x00008D0C;

    private static readonly Dictionary<uint, string> Names = BuildNames();

    public static string Describe(uint error) =>
        Names.TryGetValue(error, out var name) ? $"{name} (0x{error:X8})" : $"0x{error:X8}";

    private static Dictionary<uint, string> BuildNames()
    {
        var names = new Dictionary<uint, string>();
        foreach (var field in typeof(EDSDK).GetFields(BindingFlags.Public | BindingFlags.Static))
        {
            if (field.IsLiteral && field.FieldType == typeof(uint) && field.Name.StartsWith("EDS_ERR_", StringComparison.Ordinal))
            {
                names.TryAdd((uint)field.GetRawConstantValue()!, field.Name);
            }
        }

        names.TryAdd(TakePictureSpecialMovieModeNg, "EDS_ERR_TAKE_PICTURE_SPECIAL_MOVIE_MODE_NG");
        return names;
    }
}
