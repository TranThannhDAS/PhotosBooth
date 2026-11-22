using System.Security.Cryptography;
using System.Text;

namespace Photobooth.Application.Admin;

/// <summary>
/// Phân biệt chế độ Khởi động/Quản trị và Kiosk trong một lần chạy ứng dụng.
/// </summary>
/// <remarks>
/// Quyết định của chủ sản phẩm (19/11/2026): màn khởi động (chọn sự kiện, vào Admin) hiện khi mở ứng dụng.
/// Đang phục vụ khách vẫn vào lại được Admin bằng nút "Quản trị" ở màn hình chào + PIN (<see cref="TryReturnFromKiosk"/>);
/// thoát Admin thì về màn khởi động và bấm "Bắt đầu phục vụ khách" để quay lại Kiosk.
/// Admin được bảo vệ bằng PIN cố định <see cref="Pin"/>.
/// </remarks>
public sealed class AdminAccessService
{
    /// <summary>
    /// PIN quản trị do chủ sản phẩm chỉ định. Đây là nơi DUY NHẤT khai báo PIN.
    /// Lưu ý: PIN cố định, ngắn — chỉ đủ ngăn khách bấm nhầm, không phải biện pháp bảo mật mạnh.
    /// </summary>
    public const string Pin = "1234";

    private readonly Lock _gate = new();
    private bool _isUnlocked;
    private bool _kioskStarted;

    public event EventHandler? Changed;

    /// <summary>Đang ở chế độ quản trị (đã nhập đúng PIN, chưa thoát, chưa chuyển sang Kiosk).</summary>
    public bool IsUnlocked
    {
        get
        {
            lock (_gate)
            {
                return _isUnlocked;
            }
        }
    }

    /// <summary>Đang phục vụ khách (Kiosk): màn khởi động/Admin bị ẩn cho đến khi nhập đúng PIN.</summary>
    public bool KioskStarted
    {
        get
        {
            lock (_gate)
            {
                return _kioskStarted;
            }
        }
    }

    /// <summary>Mở Admin bằng PIN từ màn khởi động. Thất bại khi đang ở Kiosk (dùng <see cref="TryReturnFromKiosk"/>).</summary>
    public bool TryUnlock(string? pin)
    {
        var ok = pin is not null
            && CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(pin), Encoding.UTF8.GetBytes(Pin));
        lock (_gate)
        {
            if (!ok || _kioskStarted)
            {
                return false;
            }

            _isUnlocked = true;
        }

        Changed?.Invoke(this, EventArgs.Empty);
        return true;
    }

    /// <summary>
    /// Từ Kiosk (màn hình chào, không có phiên chụp dang dở — giao diện kiểm tra) vào lại Admin bằng PIN.
    /// Đúng PIN: rời Kiosk và mở Admin; thoát Admin sẽ về màn khởi động. Sai PIN: vẫn ở Kiosk.
    /// </summary>
    public bool TryReturnFromKiosk(string? pin)
    {
        var ok = pin is not null
            && CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(pin), Encoding.UTF8.GetBytes(Pin));
        lock (_gate)
        {
            if (!ok || !_kioskStarted)
            {
                return false;
            }

            _kioskStarted = false;
            _isUnlocked = true;
        }

        Changed?.Invoke(this, EventArgs.Empty);
        return true;
    }

    /// <summary>Thoát Admin, quay về màn khởi động.</summary>
    public void Lock()
    {
        lock (_gate)
        {
            _isUnlocked = false;
        }

        Changed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Chuyển sang Kiosk: khóa Admin; chỉ vào lại được bằng <see cref="TryReturnFromKiosk"/> (cần PIN).</summary>
    public void StartKiosk()
    {
        lock (_gate)
        {
            _isUnlocked = false;
            _kioskStarted = true;
        }

        Changed?.Invoke(this, EventArgs.Empty);
    }

    /// <exception cref="UnauthorizedAccessException">Không ở chế độ quản trị.</exception>
    public void EnsureAdmin()
    {
        if (!IsUnlocked)
        {
            throw new UnauthorizedAccessException("Chức năng này chỉ dành cho chế độ quản trị.");
        }
    }
}
