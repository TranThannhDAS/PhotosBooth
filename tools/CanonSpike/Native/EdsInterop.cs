using System.Reflection;
using System.Runtime.InteropServices;
using EDSDKLib;

namespace CanonSpike.Native;

/// <summary>
/// Bổ sung cho EDSDK.cs (file gốc của Canon, giữ nguyên):
/// - <c>EdsGetEvent</c> không được khai báo trong sample C#.
/// - <c>EDSDK.EdsSetPropertyData</c> dùng <c>UnmanagedType.AsAny</c>, kiểu marshal này không được hỗ trợ trên .NET Core/.NET 10,
///   nên cần overload có kiểu cụ thể.
/// </summary>
internal static class EdsInterop
{
    private const string Library = "EDSDK.dll";

    private static readonly Dictionary<uint, string> ErrorNames = BuildErrorNames();

    [DllImport(Library)]
    public static extern uint EdsGetEvent();

    [DllImport(Library, EntryPoint = "EdsSetPropertyData")]
    private static extern uint EdsSetPropertyDataUInt32(
        IntPtr inRef, uint inPropertyID, int inParam, int inPropertySize, ref uint inPropertyData);

    public static uint SetUInt32(IntPtr inRef, uint propertyId, uint value)
    {
        var data = value;
        return EdsSetPropertyDataUInt32(inRef, propertyId, 0, sizeof(uint), ref data);
    }

    public static string Describe(uint err) =>
        ErrorNames.TryGetValue(err, out var name) ? $"{name} (0x{err:X8})" : $"0x{err:X8}";

    private static Dictionary<uint, string> BuildErrorNames()
    {
        var names = new Dictionary<uint, string>();
        foreach (var field in typeof(EDSDK).GetFields(BindingFlags.Public | BindingFlags.Static))
        {
            if (field.IsLiteral && field.FieldType == typeof(uint) && field.Name.StartsWith("EDS_ERR_", StringComparison.Ordinal))
            {
                names.TryAdd((uint)field.GetRawConstantValue()!, field.Name);
            }
        }

        // Mã lỗi có trong EDSDKErrors.h 13.21.10 nhưng sample C# chưa khai báo.
        names.TryAdd(0x00008D0C, "EDS_ERR_TAKE_PICTURE_SPECIAL_MOVIE_MODE_NG");
        return names;
    }
}
