using System.Buffers.Binary;

namespace Photobooth.Application.Photos;

/// <summary>
/// Đọc kích thước ảnh từ header JPEG (marker SOF) để xác thực dữ liệu chụp
/// mà không cần thư viện xử lý ảnh.
/// </summary>
public static class JpegImageInfo
{
    public const string ContentType = "image/jpeg";

    public static bool TryReadDimensions(ReadOnlySpan<byte> data, out int widthPixels, out int heightPixels)
    {
        widthPixels = 0;
        heightPixels = 0;

        // SOI: FF D8
        if (data.Length < 4 || data[0] != 0xFF || data[1] != 0xD8)
        {
            return false;
        }

        var offset = 2;
        while (offset + 4 <= data.Length)
        {
            if (data[offset] != 0xFF)
            {
                return false;
            }

            var marker = data[offset + 1];

            // Byte đệm 0xFF liên tiếp được phép giữa các segment.
            if (marker == 0xFF)
            {
                offset++;
                continue;
            }

            // Các marker đứng một mình, không có trường độ dài.
            if (marker is 0x01 or (>= 0xD0 and <= 0xD7))
            {
                offset += 2;
                continue;
            }

            // EOI hoặc SOS trước khi gặp SOF: dữ liệu không hợp lệ cho mục đích của chúng ta.
            if (marker is 0xD9 or 0xDA)
            {
                return false;
            }

            var segmentLength = BinaryPrimitives.ReadUInt16BigEndian(data.Slice(offset + 2, 2));
            if (segmentLength < 2 || offset + 2 + segmentLength > data.Length)
            {
                return false;
            }

            if (IsStartOfFrame(marker))
            {
                // Cấu trúc SOF: length(2) precision(1) height(2) width(2) ...
                if (segmentLength < 7)
                {
                    return false;
                }

                var height = BinaryPrimitives.ReadUInt16BigEndian(data.Slice(offset + 5, 2));
                var width = BinaryPrimitives.ReadUInt16BigEndian(data.Slice(offset + 7, 2));
                if (width == 0 || height == 0)
                {
                    return false;
                }

                widthPixels = width;
                heightPixels = height;
                return true;
            }

            offset += 2 + segmentLength;
        }

        return false;
    }

    // SOF0..SOF15, trừ DHT (C4), JPG (C8) và DAC (CC).
    private static bool IsStartOfFrame(byte marker) =>
        marker is >= 0xC0 and <= 0xCF and not 0xC4 and not 0xC8 and not 0xCC;
}

