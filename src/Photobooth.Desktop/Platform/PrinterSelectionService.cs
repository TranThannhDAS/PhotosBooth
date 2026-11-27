using System.Printing;
using Microsoft.Extensions.Logging;
using Photobooth.Application.Printing;
using Photobooth.Infrastructure.Events;
using Photobooth.Infrastructure.Printing;
using Photobooth.Infrastructure.Persistence;

namespace Photobooth.Desktop.Platform;

/// <summary>
/// Máy in đang dùng (chọn trong Admin, ghi nhớ trong SQLite): chưa cấu hình, máy in giả lập hoặc máy in Windows.
/// </summary>
public sealed class PrinterSelectionService : IPrinterProvider
{
    private readonly DeviceSettingsStore _state;
    private readonly MockPrinter _mock;
    private readonly ILogger<PrinterSelectionService> _logger;
    private readonly ILoggerFactory _loggerFactory;

    public PrinterSelectionService(DeviceSettingsStore state, MockPrinter mock, ILoggerFactory loggerFactory)
    {
        _state = state;
        _mock = mock;
        _loggerFactory = loggerFactory;
        _logger = loggerFactory.CreateLogger<PrinterSelectionService>();
    }

    public PrinterSelection Selection => _state.Current.Printer;

    public IPrinter? Current => Selection switch
    {
        { Kind: PrinterKind.Mock } => _mock,
        { Kind: PrinterKind.Windows, WindowsPrinterName: { Length: > 0 } name } => new WindowsPrinter(name, _loggerFactory.CreateLogger<WindowsPrinter>()),
        _ => null,
    };

    /// <summary>Danh sách máy in Windows đã cài (cục bộ và kết nối mạng).</summary>
    public IReadOnlyList<string> GetInstalledPrinters()
    {
        try
        {
            using var server = new LocalPrintServer();
            using var queues = server.GetPrintQueues([EnumeratedPrintQueueTypes.Local, EnumeratedPrintQueueTypes.Connections]);
            return queues.Select(q => q.FullName).Order(StringComparer.CurrentCultureIgnoreCase).ToArray();
        }
        catch (Exception ex) when (ex is PrintSystemException or InvalidOperationException)
        {
            _logger.LogWarning(ex, "Không đọc được danh sách máy in Windows");
            return [];
        }
    }

    public Task SaveAsync(PrinterSelection selection) => _state.SavePrinterAsync(selection);
}
