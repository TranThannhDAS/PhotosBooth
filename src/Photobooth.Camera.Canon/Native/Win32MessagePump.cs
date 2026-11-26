using System.Runtime.InteropServices;

namespace Photobooth.Camera.Canon.Native;

/// <summary>
/// Xử lý các Windows message đang chờ trên luồng hiện tại. EDSDK trên Windows gửi callback qua message queue
/// của luồng đã khởi tạo SDK (đã kiểm chứng bằng tools/CanonSpike), nên luồng SDK phải pump định kỳ.
/// </summary>
internal static class Win32MessagePump
{
    private const uint PmRemove = 0x0001;

    [StructLayout(LayoutKind.Sequential)]
    private struct Msg
    {
        public IntPtr Hwnd;
        public uint Message;
        public UIntPtr WParam;
        public IntPtr LParam;
        public uint Time;
        public int PointX;
        public int PointY;
    }

    [DllImport("user32.dll", EntryPoint = "PeekMessageW")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool PeekMessage(out Msg message, IntPtr hwnd, uint filterMin, uint filterMax, uint removeFlags);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool TranslateMessage(ref Msg message);

    [DllImport("user32.dll", EntryPoint = "DispatchMessageW")]
    private static extern IntPtr DispatchMessage(ref Msg message);

    public static void Drain()
    {
        while (PeekMessage(out var message, IntPtr.Zero, 0, 0, PmRemove))
        {
            TranslateMessage(ref message);
            DispatchMessage(ref message);
        }
    }
}
