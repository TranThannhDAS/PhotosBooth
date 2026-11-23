namespace Photobooth.Cloud.Storage;

public enum PhotoFormat
{
    Jpeg,
    Png,
}

public static class PhotoFormats
{
    /// <summary>Số byte đầu file cần đọc để nhận dạng định dạng.</summary>
    public const int HeaderLength = 24;

    public static IReadOnlyList<PhotoFormat> All { get; } = [PhotoFormat.Jpeg, PhotoFormat.Png];

    public static string Extension(this PhotoFormat format) => format switch
    {
        PhotoFormat.Jpeg => ".jpg",
        PhotoFormat.Png => ".png",
        _ => throw new ArgumentOutOfRangeException(nameof(format)),
    };

    public static string ContentType(this PhotoFormat format) => format switch
    {
        PhotoFormat.Jpeg => "image/jpeg",
        PhotoFormat.Png => "image/png",
        _ => throw new ArgumentOutOfRangeException(nameof(format)),
    };

    /// <summary>
    /// Nhận dạng định dạng THỰC TẾ từ nội dung đầu file (magic bytes), không tin tên file hay Content-Type client gửi.
    /// JPEG: SOI (FF D8) + một marker hợp lệ. PNG: chữ ký 8 byte + chunk IHDR đầu tiên với kích thước &gt; 0.
    /// </summary>
    public static PhotoFormat? Detect(ReadOnlySpan<byte> header)
    {
        if (header.Length >= 4 && header[0] == 0xFF && header[1] == 0xD8 && header[2] == 0xFF && header[3] >= 0xC0 && header[3] != 0xFF)
        {
            return PhotoFormat.Jpeg;
        }

        ReadOnlySpan<byte> pngSignature = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A];
        ReadOnlySpan<byte> ihdr = "IHDR"u8;
        if (header.Length >= 24
            && header[..8].SequenceEqual(pngSignature)
            && System.Buffers.Binary.BinaryPrimitives.ReadUInt32BigEndian(header[8..12]) == 13
            && header[12..16].SequenceEqual(ihdr)
            && System.Buffers.Binary.BinaryPrimitives.ReadUInt32BigEndian(header[16..20]) > 0
            && System.Buffers.Binary.BinaryPrimitives.ReadUInt32BigEndian(header[20..24]) > 0)
        {
            return PhotoFormat.Png;
        }

        return null;
    }
}
