using System.Collections.Concurrent;
using EDSDKLib;
using Microsoft.Extensions.Logging;
using Photobooth.Camera.Canon.Native;

namespace Photobooth.Camera.Canon;

/// <summary>
/// Sở hữu một luồng STA riêng cho Canon EDSDK: khởi tạo/kết thúc SDK, thực thi tuần tự mọi lệnh SDK
/// và pump Windows message để SDK gọi callback trên chính luồng này (mô hình đã kiểm chứng bằng tools/CanonSpike).
/// </summary>
/// <remarks>
/// EDSDK chỉ được khởi tạo một lần mỗi tiến trình → đăng ký singleton. Không bao giờ chặn luồng gọi chờ luồng SDK,
/// trừ <see cref="Dispose"/> khi đóng ứng dụng (có giới hạn thời gian).
/// </remarks>
public sealed class CanonSdkHost : IDisposable
{
    private static readonly TimeSpan PumpInterval = TimeSpan.FromMilliseconds(10);
    private static readonly TimeSpan ShutdownTimeout = TimeSpan.FromSeconds(5);

    private readonly IEdsApi _api;
    private readonly ILogger<CanonSdkHost> _logger;
    private readonly ConcurrentQueue<WorkItem> _work = new();
    private readonly Lock _queueGate = new();
    private readonly AutoResetEvent _workAvailable = new(false);
    private readonly Thread _thread;
    private volatile bool _stopping;
    private bool _closed;
    private bool _disposed;

    // Chỉ truy cập trên luồng SDK.
    private SdkState _sdkState = SdkState.NotInitialized;

    public CanonSdkHost(ILogger<CanonSdkHost> logger)
        : this(api: null, logger)
    {
    }

    internal CanonSdkHost(IEdsApi? api, ILogger<CanonSdkHost> logger)
    {
        _logger = logger;
        _api = api ?? new NativeEdsApi(logger);
        _thread = new Thread(Run)
        {
            Name = "Canon EDSDK",
            IsBackground = true,
        };
        _thread.SetApartmentState(ApartmentState.STA);
        _thread.Start();
    }

    private enum SdkState
    {
        NotInitialized,
        Ready,
        Failed,
    }

    /// <summary>API SDK. Chỉ được dùng bên trong công việc chạy trên luồng SDK (callback hoặc <see cref="InvokeAsync{T}"/>).</summary>
    internal IEdsApi Api => _api;

    internal bool IsOnSdkThread => Environment.CurrentManagedThreadId == _thread.ManagedThreadId;

    /// <summary>
    /// Khởi tạo SDK nếu chưa (chạy một lần). Trả về <c>false</c> nếu SDK không dùng được
    /// (thiếu EDSDK.dll, sai kiến trúc 32/64-bit hoặc <c>EdsInitializeSDK</c> lỗi).
    /// </summary>
    internal Task<bool> EnsureInitializedAsync(CancellationToken cancellationToken = default) =>
        InvokeAsync(_ =>
        {
            if (_sdkState == SdkState.NotInitialized)
            {
                _sdkState = InitializeSdk();
            }

            return _sdkState == SdkState.Ready;
        }, cancellationToken);

    /// <summary>Chạy <paramref name="work"/> trên luồng SDK và trả về kết quả.</summary>
    internal Task<T> InvokeAsync<T>(Func<IEdsApi, T> work, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_stopping, this);
        cancellationToken.ThrowIfCancellationRequested();

        var completion = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        Enqueue(new WorkItem(
            Execute: () =>
            {
                if (cancellationToken.IsCancellationRequested)
                {
                    completion.TrySetCanceled(cancellationToken);
                    return;
                }

                try
                {
                    completion.TrySetResult(work(_api));
                }
                catch (Exception ex)
                {
                    completion.TrySetException(ex);
                }
            },
            Abandon: () => completion.TrySetException(new ObjectDisposedException(nameof(CanonSdkHost)))));
        return completion.Task;
    }

    internal Task InvokeAsync(Action<IEdsApi> work, CancellationToken cancellationToken = default) =>
        InvokeAsync(api =>
        {
            work(api);
            return true;
        }, cancellationToken);

    /// <summary>Xếp việc chạy sau trên luồng SDK (dùng từ callback, khi không được thao tác ngay trong callback).</summary>
    internal void Post(Action<IEdsApi> work)
    {
        Enqueue(new WorkItem(
            Execute: () =>
            {
                try
                {
                    work(_api);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Lỗi khi xử lý công việc nền của Canon EDSDK");
                }
            },
            Abandon: static () => { }));
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _stopping = true;
        _workAvailable.Set();
        // Không Dispose _workAvailable: luồng khác có thể vẫn gọi Set() đúng lúc đóng; handle được thu hồi cùng tiến trình.
        if (!_thread.Join(ShutdownTimeout))
        {
            _logger.LogWarning("Luồng Canon EDSDK không dừng kịp trong {Seconds} giây", ShutdownTimeout.TotalSeconds);
        }
    }

    private void Enqueue(WorkItem item)
    {
        lock (_queueGate)
        {
            if (_closed)
            {
                item.Abandon();
                return;
            }

            _work.Enqueue(item);
        }

        _workAvailable.Set();
    }

    private void Run()
    {
        while (true)
        {
            while (_work.TryDequeue(out var item))
            {
                item.Execute();
            }

            if (_sdkState == SdkState.Ready)
            {
                _api.PumpEvents();
            }

            if (_stopping && _work.IsEmpty)
            {
                break;
            }

            _workAvailable.WaitOne(PumpInterval);
        }

        TerminateSdk();

        // Công việc xếp hàng đúng lúc dừng sẽ không chạy nữa: báo lỗi thay vì để Task treo.
        lock (_queueGate)
        {
            _closed = true;
            while (_work.TryDequeue(out var item))
            {
                item.Abandon();
            }
        }
    }

    private sealed record WorkItem(Action Execute, Action Abandon);

    private SdkState InitializeSdk()
    {
        try
        {
            var err = _api.Initialize();
            if (err == EDSDK.EDS_ERR_OK)
            {
                _logger.LogInformation("Đã khởi tạo Canon EDSDK");
                return SdkState.Ready;
            }

            _logger.LogError("Không khởi tạo được Canon EDSDK: {Error}", EdsErrors.Describe(err));
        }
        catch (Exception ex) when (ex is DllNotFoundException or BadImageFormatException or EntryPointNotFoundException)
        {
            // Thiếu EDSDK.dll/EdsImage.dll hoặc tiến trình không chạy 64-bit.
            _logger.LogError(ex, "Không nạp được thư viện Canon EDSDK");
        }

        return SdkState.Failed;
    }

    private void TerminateSdk()
    {
        if (_sdkState != SdkState.Ready)
        {
            return;
        }

        try
        {
            var err = _api.Terminate();
            _logger.LogInformation("Đã kết thúc Canon EDSDK: {Error}", EdsErrors.Describe(err));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Lỗi khi kết thúc Canon EDSDK");
        }

        _sdkState = SdkState.NotInitialized;
    }
}
