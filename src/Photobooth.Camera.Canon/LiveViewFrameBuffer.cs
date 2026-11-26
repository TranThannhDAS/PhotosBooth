using Photobooth.Application.Photos;

namespace Photobooth.Camera.Canon;

/// <summary>Một khung Live View: JPEG kèm số thứ tự tăng dần và kích thước (pixel).</summary>
public sealed record LiveViewFrame(byte[] Jpeg, long Sequence, int WidthPixels, int HeightPixels);

/// <summary>
/// Giữ khung Live View mới nhất. Chỉ giữ một khung: bên hiển thị chậm hơn máy ảnh sẽ bỏ qua khung cũ
/// thay vì dồn hàng đợi (tránh trễ hình và tốn bộ nhớ).
/// </summary>
public sealed class LiveViewFrameBuffer
{
    private readonly Lock _gate = new();
    private LiveViewFrame? _latest;
    private long _sequence;
    private TaskCompletionSource _newFrame = NewSignal();

    public LiveViewFrame? Latest
    {
        get
        {
            lock (_gate)
            {
                return _latest;
            }
        }
    }

    /// <summary>Nhận khung mới; khung không đọc được kích thước JPEG bị bỏ qua.</summary>
    public bool Publish(byte[] jpeg)
    {
        if (!JpegImageInfo.TryReadDimensions(jpeg, out var width, out var height))
        {
            return false;
        }

        TaskCompletionSource signal;
        lock (_gate)
        {
            _latest = new LiveViewFrame(jpeg, ++_sequence, width, height);
            signal = _newFrame;
            _newFrame = NewSignal();
        }

        signal.TrySetResult();
        return true;
    }

    /// <summary>Bỏ khung hiện có (khi tắt Live View), để màn hình không hiện hình cũ.</summary>
    public void Clear()
    {
        lock (_gate)
        {
            _latest = null;
        }
    }

    /// <summary>
    /// Chờ khung có số thứ tự lớn hơn <paramref name="afterSequence"/>; hết <paramref name="timeout"/> thì trả về null.
    /// </summary>
    public async Task<LiveViewFrame?> WaitForNewerAsync(long afterSequence, TimeSpan timeout, CancellationToken cancellationToken = default)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (true)
        {
            Task signal;
            lock (_gate)
            {
                if (_latest is { } frame && frame.Sequence > afterSequence)
                {
                    return frame;
                }

                signal = _newFrame.Task;
            }

            var remaining = deadline - DateTime.UtcNow;
            if (remaining <= TimeSpan.Zero)
            {
                return null;
            }

            try
            {
                await signal.WaitAsync(remaining, cancellationToken).ConfigureAwait(false);
            }
            catch (TimeoutException)
            {
                return null;
            }
        }
    }

    private static TaskCompletionSource NewSignal() => new(TaskCreationOptions.RunContinuationsAsynchronously);
}
