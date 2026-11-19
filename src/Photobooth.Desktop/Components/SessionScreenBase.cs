using Microsoft.AspNetCore.Components;
using Photobooth.Application.Sessions;

namespace Photobooth.Desktop.Components;

/// <summary>
/// Lớp cơ sở cho các màn hình trong phiên: tự cập nhật khi trạng thái phiên thay đổi.
/// </summary>
/// <remarks>
/// Không dựa vào component cha: Blazor bỏ qua re-render component con khi tham số (EventCallback)
/// không đổi, nên màn hình phải tự lắng nghe <see cref="PhotoSessionService.SessionChanged"/>.
/// </remarks>
public abstract class SessionScreenBase : ComponentBase, IAsyncDisposable
{
    [Inject]
    protected PhotoSessionService Sessions { get; set; } = default!;

    protected PhotoSession? Session => Sessions.Current;

    protected bool IsDisposed { get; private set; }

    protected override void OnInitialized()
    {
        Sessions.SessionChanged += OnSessionChanged;
    }

    public async ValueTask DisposeAsync()
    {
        if (IsDisposed)
        {
            return;
        }

        IsDisposed = true;
        Sessions.SessionChanged -= OnSessionChanged;
        await DisposeCoreAsync();
        GC.SuppressFinalize(this);
    }

    /// <summary>Giải phóng tài nguyên riêng của màn hình (camera, CancellationTokenSource...).</summary>
    protected virtual ValueTask DisposeCoreAsync() => ValueTask.CompletedTask;

    private void OnSessionChanged(object? sender, EventArgs e)
    {
        // Sự kiện có thể phát từ luồng nền.
        _ = InvokeAsync(() =>
        {
            if (!IsDisposed)
            {
                StateHasChanged();
            }
        });
    }
}
