using System.Runtime.InteropServices;

namespace CanonSpike.Native;

/// <summary>
/// Xử lý các Windows message đang chờ trên luồng hiện tại (tương đương một vòng message loop của WinForms/WPF).
/// Sample WinForms của Canon nhận callback EDSDK nhờ message loop của luồng chính; spike dùng lớp này để kiểm chứng điều đó.
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

    /// <summary>Xử lý hết message đang chờ; trả về số message đã xử lý.</summary>
    public static int Drain()
    {
        var count = 0;
        while (PeekMessage(out var message, IntPtr.Zero, 0, 0, PmRemove))
        {
            TranslateMessage(ref message);
            DispatchMessage(ref message);
            count++;
        }

        return count;
    }
}
