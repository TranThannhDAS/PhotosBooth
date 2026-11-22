namespace Photobooth.Application.Templates.Editing;

/// <summary>Hình chữ nhật theo PIXEL CANVAS của template (không phải pixel màn hình).</summary>
public readonly record struct CanvasRect(int X, int Y, int Width, int Height)
{
    public int Right => X + Width;

    public int Bottom => Y + Height;
}

/// <summary>Điểm điều khiển khi kéo: di chuyển cả khối hoặc một cạnh/góc để đổi kích thước.</summary>
public enum DragHandle
{
    Move,
    North,
    South,
    East,
    West,
    NorthEast,
    NorthWest,
    SouthEast,
    SouthWest,
}

/// <summary>
/// Phép tính kéo/đổi kích thước trong Template Editor. Mọi giá trị đầu ra là tọa độ canvas (số nguyên);
/// tọa độ màn hình chỉ được đổi sang canvas qua <see cref="ToCanvasDelta"/> và không bao giờ được lưu.
/// </summary>
public static class CanvasGeometry
{
    public const int MinElementSize = 1;

    /// <summary>
    /// Đổi khoảng kéo trên màn hình (CSS pixel) sang pixel canvas.
    /// <paramref name="displayScale"/> = CSS pixel hiển thị cho mỗi pixel canvas (ví dụ canvas 1200 px hiện rộng 400 px → 1/3).
    /// </summary>
    public static double ToCanvasDelta(double screenDelta, double displayScale)
    {
        if (!(displayScale > 0) || double.IsInfinity(displayScale))
        {
            throw new ArgumentOutOfRangeException(nameof(displayScale), "Tỉ lệ hiển thị phải là số dương.");
        }

        return screenDelta / displayScale;
    }

    /// <summary>
    /// Áp dụng một thao tác kéo lên khung ban đầu <paramref name="start"/> với tổng độ dời (pixel canvas) tính từ lúc bắt đầu kéo.
    /// Tính lại từ khung ban đầu mỗi lần nên không tích lũy sai số làm tròn.
    /// </summary>
    /// <param name="keepAspect">Giữ tỉ lệ khung khi kéo góc (ví dụ giữ Shift).</param>
    /// <param name="gridSize">Bắt dính lưới (pixel canvas) cho các cạnh đang kéo; 0 = tắt. Chỉ là tiện ích nhập liệu.</param>
    public static CanvasRect Apply(CanvasRect start, DragHandle handle, double deltaX, double deltaY, bool keepAspect = false, int gridSize = 0)
    {
        if (handle == DragHandle.Move)
        {
            var x = Snap(start.X + deltaX, gridSize);
            var y = Snap(start.Y + deltaY, gridSize);
            return start with { X = x, Y = y };
        }

        double left = start.X, top = start.Y, right = start.Right, bottom = start.Bottom;
        var movesLeft = handle is DragHandle.West or DragHandle.NorthWest or DragHandle.SouthWest;
        var movesRight = handle is DragHandle.East or DragHandle.NorthEast or DragHandle.SouthEast;
        var movesTop = handle is DragHandle.North or DragHandle.NorthEast or DragHandle.NorthWest;
        var movesBottom = handle is DragHandle.South or DragHandle.SouthEast or DragHandle.SouthWest;

        if (movesLeft)
        {
            left = Math.Min(Snap(left + deltaX, gridSize), right - MinElementSize);
        }

        if (movesRight)
        {
            right = Math.Max(Snap(right + deltaX, gridSize), left + MinElementSize);
        }

        if (movesTop)
        {
            top = Math.Min(Snap(top + deltaY, gridSize), bottom - MinElementSize);
        }

        if (movesBottom)
        {
            bottom = Math.Max(Snap(bottom + deltaY, gridSize), top + MinElementSize);
        }

        var isCorner = (movesLeft || movesRight) && (movesTop || movesBottom);
        if (keepAspect && isCorner && start.Width > 0 && start.Height > 0)
        {
            // Giữ tỉ lệ: lấy hệ số phóng lớn hơn của hai chiều, neo góc đối diện.
            var ratio = (double)start.Width / start.Height;
            var scale = Math.Max((right - left) / start.Width, (bottom - top) / start.Height);
            var width = Math.Max(MinElementSize, start.Width * scale);
            var height = Math.Max(MinElementSize, width / ratio);
            if (movesLeft)
            {
                left = right - width;
            }
            else
            {
                right = left + width;
            }

            if (movesTop)
            {
                top = bottom - height;
            }
            else
            {
                bottom = top + height;
            }
        }

        var x0 = (int)Math.Round(left, MidpointRounding.AwayFromZero);
        var y0 = (int)Math.Round(top, MidpointRounding.AwayFromZero);
        var x1 = (int)Math.Round(right, MidpointRounding.AwayFromZero);
        var y1 = (int)Math.Round(bottom, MidpointRounding.AwayFromZero);
        return new CanvasRect(x0, y0, Math.Max(MinElementSize, x1 - x0), Math.Max(MinElementSize, y1 - y0));
    }

    /// <summary>Căn giữa khung theo chiều ngang và/hoặc dọc của canvas.</summary>
    public static CanvasRect Center(CanvasRect rect, int canvasWidth, int canvasHeight, bool horizontal, bool vertical) => rect with
    {
        X = horizontal ? (int)Math.Round((canvasWidth - rect.Width) / 2.0, MidpointRounding.AwayFromZero) : rect.X,
        Y = vertical ? (int)Math.Round((canvasHeight - rect.Height) / 2.0, MidpointRounding.AwayFromZero) : rect.Y,
    };

    private static int Snap(double value, int gridSize) => gridSize > 1
        ? (int)(Math.Round(value / gridSize, MidpointRounding.AwayFromZero) * gridSize)
        : (int)Math.Round(value, MidpointRounding.AwayFromZero);
}
