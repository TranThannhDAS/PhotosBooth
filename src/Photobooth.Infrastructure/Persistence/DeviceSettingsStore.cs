using Microsoft.EntityFrameworkCore;
using Photobooth.Infrastructure.Events;

namespace Photobooth.Infrastructure.Persistence;

/// <summary>Cấu hình máy hiện hành đã nạp từ SQLite; đọc đồng bộ không chặn Live View.</summary>
public sealed record DeviceSettingsSnapshot
{
    public PrinterSelection Printer { get; init; } = new();
    public CameraSelection Camera { get; init; } = new();
    public CaptureGuideSettings CaptureGuide { get; init; } = new();
}

/// <summary>
/// Nguồn dữ liệu DUY NHẤT của cấu hình thiết bị: bảng DeviceSettings trong photobooth.db.
/// Có cache trong RAM để Camera/Printer truy cập đồng bộ. Chỉ cập nhật cache sau khi SaveChanges thành công.
/// Không đọc, ghi, import hay fallback sang app-state.json.
/// </summary>
public sealed class DeviceSettingsStore
{
    private readonly IDbContextFactory<PhotoboothDbContext> _factory;
    private readonly SemaphoreSlim _writeGate = new(1, 1);
    private DeviceSettingsSnapshot _current;

    public DeviceSettingsStore(IDbContextFactory<PhotoboothDbContext> factory)
    {
        _factory = factory;
        using var db = factory.CreateDbContext();
        var row = db.DeviceSettings.AsNoTracking().SingleOrDefault(x => x.Id == DeviceSettingsRecord.SingletonId);
        _current = row is null ? new DeviceSettingsSnapshot() : Snapshot(row);
    }

    public DeviceSettingsSnapshot Current => Volatile.Read(ref _current);

    public async Task SavePrinterAsync(PrinterSelection selection, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(selection);
        if (!Enum.IsDefined(selection.Kind)
            || (selection.Kind == PrinterKind.Windows && string.IsNullOrWhiteSpace(selection.WindowsPrinterName)))
        {
            throw new ArgumentOutOfRangeException(nameof(selection), "Chọn máy in Windows hợp lệ.");
        }

        await UpdateAsync(
            row =>
            {
                row.PrinterKind = selection.Kind;
                row.WindowsPrinterName = selection.Kind == PrinterKind.Windows ? selection.WindowsPrinterName : null;
            },
            current => current with { Printer = selection },
            cancellationToken).ConfigureAwait(false);
    }

    public Task SaveCameraAsync(CameraSelection selection, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(selection);
        return UpdateAsync(
            row =>
            {
                row.WebcamDeviceId = selection.WebcamDeviceId;
                row.WebcamLabel = selection.IsDefault ? null : selection.WebcamLabel;
            },
            current => current with { Camera = selection },
            cancellationToken);
    }

    /// <summary>Chỉ cập nhật deviceId mới nếu Admin chưa đổi webcam trong lúc kết nối.</summary>
    public async Task<bool> UpdateResolvedCameraAsync(string expectedDeviceId, string resolvedDeviceId, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(expectedDeviceId);
        ArgumentException.ThrowIfNullOrWhiteSpace(resolvedDeviceId);

        await _writeGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (Current.Camera.WebcamDeviceId != expectedDeviceId)
            {
                return false;
            }

            await using var db = await _factory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
            var row = await GetOrAddAsync(db, cancellationToken).ConfigureAwait(false);
            // Kiểm tra lại bản ghi SQLite để không ghi đè thay đổi của một kết nối khác.
            if (row.WebcamDeviceId != expectedDeviceId)
            {
                return false;
            }

            row.WebcamDeviceId = resolvedDeviceId;
            await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            Volatile.Write(ref _current, Current with
            {
                Camera = Current.Camera with { WebcamDeviceId = resolvedDeviceId },
            });
            return true;
        }
        finally
        {
            _writeGate.Release();
        }
    }

    public Task SaveCaptureGuideAsync(CaptureGuideSettings settings, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(settings);
        settings.Validate();
        return UpdateAsync(
            row =>
            {
                row.GuideMode = settings.Mode;
                row.GuideOutsideOpacity = settings.OutsideOpacity;
                row.GuideShowLabel = settings.ShowGuideLabel;
            },
            current => current with { CaptureGuide = settings },
            cancellationToken);
    }

    private async Task UpdateAsync(
        Action<DeviceSettingsRecord> updateRow,
        Func<DeviceSettingsSnapshot, DeviceSettingsSnapshot> updateCache,
        CancellationToken cancellationToken)
    {
        await _writeGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await using var db = await _factory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
            var row = await GetOrAddAsync(db, cancellationToken).ConfigureAwait(false);
            updateRow(row);
            await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            Volatile.Write(ref _current, updateCache(Current));
        }
        finally
        {
            _writeGate.Release();
        }
    }

    private static async Task<DeviceSettingsRecord> GetOrAddAsync(
        PhotoboothDbContext db, CancellationToken cancellationToken)
    {
        var row = await db.DeviceSettings
            .SingleOrDefaultAsync(x => x.Id == DeviceSettingsRecord.SingletonId, cancellationToken)
            .ConfigureAwait(false);

        if (row is not null)
        {
            return row;
        }

        row = new DeviceSettingsRecord();
        db.DeviceSettings.Add(row);
        return row;
    }

    private static DeviceSettingsSnapshot Snapshot(DeviceSettingsRecord row) => new()
    {
        Printer = new PrinterSelection
        {
            Kind = row.PrinterKind,
            WindowsPrinterName = row.WindowsPrinterName,
        },
        Camera = new CameraSelection
        {
            WebcamDeviceId = row.WebcamDeviceId,
            WebcamLabel = row.WebcamLabel,
        },
        CaptureGuide = new CaptureGuideSettings
        {
            Mode = row.GuideMode,
            OutsideOpacity = row.GuideOutsideOpacity,
            ShowGuideLabel = row.GuideShowLabel,
        }.Normalize(),
    };
}
