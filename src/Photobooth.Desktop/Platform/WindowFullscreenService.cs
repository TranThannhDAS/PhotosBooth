using System.Windows;

namespace Photobooth.Desktop.Platform;

/// <summary>
/// Đưa cửa sổ WPF lên toàn màn hình vật lý cho trình xem ảnh thành phẩm.
/// Lưu và khôi phục trạng thái cửa sổ, không đổi chế độ toàn màn hình của Windows.
/// </summary>
public sealed class WindowFullscreenService
{
    private Window? _window;
    private Rect _restoreBounds;
    private WindowState _restoreState;
    private WindowStyle _restoreStyle;
    private ResizeMode _restoreResizeMode;
    private bool _restoreTopmost;

    public bool IsFullscreen { get; private set; }

    public void Attach(Window window)
    {
        ArgumentNullException.ThrowIfNull(window);
        if (_window is not null && !ReferenceEquals(_window, window))
        {
            throw new InvalidOperationException("Đã gắn một cửa sổ toàn màn hình khác.");
        }

        _window = window;
    }

    public bool Enter()
    {
        if (_window is not { } window)
        {
            return false;
        }

        if (!window.Dispatcher.CheckAccess())
        {
            return window.Dispatcher.Invoke(Enter);
        }

        if (IsFullscreen)
        {
            return true;
        }

        _restoreBounds = window.RestoreBounds;
        _restoreState = window.WindowState;
        _restoreStyle = window.WindowStyle;
        _restoreResizeMode = window.ResizeMode;
        _restoreTopmost = window.Topmost;

        // Đặt Normal trước khi bỏ viền để hệ điều hành tính lại vùng hiển thị.
        window.WindowState = WindowState.Normal;
        window.WindowStyle = WindowStyle.None;
        window.ResizeMode = ResizeMode.NoResize;
        window.Topmost = true;
        window.WindowState = WindowState.Maximized;

        IsFullscreen = true;
        return true;
    }

    public void Exit()
    {
        if (_window is not { } window || !IsFullscreen)
        {
            return;
        }

        if (!window.Dispatcher.CheckAccess())
        {
            window.Dispatcher.Invoke(Exit);
            return;
        }

        window.WindowState = WindowState.Normal;
        window.WindowStyle = _restoreStyle;
        window.ResizeMode = _restoreResizeMode;
        window.Topmost = _restoreTopmost;

        if (_restoreBounds.Width > 0 && _restoreBounds.Height > 0)
        {
            window.Left = _restoreBounds.Left;
            window.Top = _restoreBounds.Top;
            window.Width = _restoreBounds.Width;
            window.Height = _restoreBounds.Height;
        }

        window.WindowState = _restoreState;
        IsFullscreen = false;
    }

    public void Detach(Window window)
    {
        if (!ReferenceEquals(_window, window))
        {
            return;
        }

        // Cửa sổ đã đóng, không được khôi phục trạng thái trên HWND đã hủy.
        _window = null;
        IsFullscreen = false;
    }
}
