using EDSDKLib;
using Microsoft.Extensions.Logging.Abstractions;

namespace Photobooth.Camera.Canon.Tests;

public sealed class CanonSdkHostTests
{
    [Fact]
    public async Task EnsureInitializedAsync_InitializesOnce_AndDisposeTerminatesSdk()
    {
        var api = new FakeEdsApi();
        var host = new CanonSdkHost(api, NullLogger<CanonSdkHost>.Instance);

        Assert.True(await host.EnsureInitializedAsync());
        Assert.True(await host.EnsureInitializedAsync());
        host.Dispose();

        Assert.Equal(1, api.TerminateCalls);
    }

    [Fact]
    public async Task EnsureInitializedAsync_InitializeError_ReturnsFalse_AndDoesNotTerminate()
    {
        var api = new FakeEdsApi { InitializeResult = EDSDK.EDS_ERR_INTERNAL_ERROR };
        var host = new CanonSdkHost(api, NullLogger<CanonSdkHost>.Instance);

        Assert.False(await host.EnsureInitializedAsync());
        host.Dispose();

        Assert.Equal(0, api.TerminateCalls);
    }

    [Fact]
    public async Task InvokeAsync_ExceptionInWork_IsPropagatedToCaller()
    {
        using var host = new CanonSdkHost(new FakeEdsApi(), NullLogger<CanonSdkHost>.Instance);

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => host.InvokeAsync<int>(_ => throw new InvalidOperationException("lỗi thử")));

        // Luồng SDK vẫn tiếp tục phục vụ sau lỗi.
        Assert.Equal(42, await host.InvokeAsync(_ => 42));
    }

    [Fact]
    public void InvokeAsync_AfterDispose_ThrowsObjectDisposed()
    {
        var host = new CanonSdkHost(new FakeEdsApi(), NullLogger<CanonSdkHost>.Instance);
        host.Dispose();

        // Ném ngay khi gọi (đồng bộ), không trả về Task treo.
        Assert.Throws<ObjectDisposedException>(() => { _ = host.InvokeAsync(_ => 1); });
    }
}
