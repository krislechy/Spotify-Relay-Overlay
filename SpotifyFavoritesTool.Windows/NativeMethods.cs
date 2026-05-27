using System.Runtime.InteropServices;
using System.Windows.Interop;
using System.Windows.Media;

namespace SpotifyFavoritesTool;

public static class NativeMethods
{
    public const int WmHotkey = 0x0312;
    public const int HotkeyToggleFavorite = 1001;
    public const int HotkeyShowFavoriteStatus = 1002;

    public static readonly uint ShowExistingWindowMessage = RegisterWindowMessage("SpotifyFavoritesTool.ShowExistingWindow");

    private const uint SwpNomove = 0x0002;
    private const uint SwpNosize = 0x0001;
    private const uint SwpNoactivate = 0x0010;
    private const uint SwpShowwindow = 0x0040;
    private const int SwShownormal = 1;
    private const int AccentEnableAcrylicBlurBehind = 4;
    private const int WcaAccentPolicy = 19;

    private static readonly IntPtr HwndTopmost = new(-1);
    private static readonly IntPtr HwndBroadcast = new(0xffff);

    [DllImport("user32.dll", EntryPoint = "RegisterWindowMessageW", SetLastError = true, CharSet = CharSet.Unicode)]
    public static extern uint RegisterWindowMessage(string lpString);

    [DllImport("user32.dll", EntryPoint = "PostMessageW", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool PostMessage(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetForegroundWindow(IntPtr hWnd);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool UnregisterHotKey(IntPtr hWnd, int id);

    [DllImport("user32.dll", EntryPoint = "SetWindowPos", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetWindowPosNative(
        IntPtr hWnd,
        IntPtr hWndInsertAfter,
        int x,
        int y,
        int cx,
        int cy,
        uint uFlags);

    [DllImport("dwmapi.dll", PreserveSig = true)]
    private static extern int DwmIsCompositionEnabled([MarshalAs(UnmanagedType.Bool)] out bool enabled);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern int SetWindowCompositionAttribute(IntPtr hWnd, ref WindowCompositionAttributeData data);

    [DllImport("gdi32.dll", SetLastError = true)]
    private static extern IntPtr CreateRoundRectRgn(int left, int top, int right, int bottom, int ellipseWidth, int ellipseHeight);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern int SetWindowRgn(IntPtr hWnd, IntPtr hRgn, [MarshalAs(UnmanagedType.Bool)] bool redraw);

    [DllImport("gdi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DeleteObject(IntPtr hObject);

    public static void SignalExistingInstance()
    {
        if (ShowExistingWindowMessage != 0)
        {
            PostMessage(HwndBroadcast, ShowExistingWindowMessage, IntPtr.Zero, IntPtr.Zero);
        }
    }

    public static void BringWindowToFront(IntPtr hWnd)
    {
        if (hWnd == IntPtr.Zero)
        {
            return;
        }

        ShowWindow(hWnd, SwShownormal);
        SetForegroundWindow(hWnd);
    }

    public static void ForceTopmost(IntPtr hWnd)
    {
        if (hWnd == IntPtr.Zero)
        {
            return;
        }

        SetWindowPosNative(hWnd, HwndTopmost, 0, 0, 0, 0, SwpNomove | SwpNosize | SwpNoactivate | SwpShowwindow);
    }

    public static void EnableBlurredGlass(IntPtr hWnd, HwndSource? source)
    {
        if (hWnd == IntPtr.Zero)
        {
            return;
        }

        if (DwmIsCompositionEnabled(out var compositionEnabled) != 0 || !compositionEnabled)
        {
            return;
        }

        if (source?.CompositionTarget is { } compositionTarget)
        {
            compositionTarget.BackgroundColor = Colors.Transparent;
        }

        var accentPolicy = new AccentPolicy
        {
            AccentState = AccentEnableAcrylicBlurBehind,
            AccentFlags = 2,
            GradientColor = 0
        };
        var accentPolicySize = Marshal.SizeOf<AccentPolicy>();
        var accentPolicyPointer = Marshal.AllocHGlobal(accentPolicySize);
        try
        {
            Marshal.StructureToPtr(accentPolicy, accentPolicyPointer, fDeleteOld: false);
            var data = new WindowCompositionAttributeData
            {
                Attribute = WcaAccentPolicy,
                Data = accentPolicyPointer,
                SizeOfData = accentPolicySize
            };
            SetWindowCompositionAttribute(hWnd, ref data);
        }
        finally
        {
            Marshal.FreeHGlobal(accentPolicyPointer);
        }
    }

    public static void ApplyRoundedWindowRegion(IntPtr hWnd, HwndSource? source, double width, double height, double radius)
    {
        if (hWnd == IntPtr.Zero || width <= 0 || height <= 0)
        {
            return;
        }

        var transform = source?.CompositionTarget?.TransformToDevice ?? Matrix.Identity;
        var pixelWidth = Math.Max(1, (int)Math.Round(width * transform.M11));
        var pixelHeight = Math.Max(1, (int)Math.Round(height * transform.M22));
        var pixelRadius = Math.Max(1, (int)Math.Round(radius * Math.Max(transform.M11, transform.M22)));
        var region = CreateRoundRectRgn(0, 0, pixelWidth + 1, pixelHeight + 1, pixelRadius * 2, pixelRadius * 2);
        if (region == IntPtr.Zero)
        {
            return;
        }

        if (SetWindowRgn(hWnd, region, redraw: true) == 0)
        {
            DeleteObject(region);
        }
    }

    private static uint ToAbgr(byte alpha, byte red, byte green, byte blue)
    {
        return (uint)(alpha << 24 | blue << 16 | green << 8 | red);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct WindowCompositionAttributeData
    {
        public int Attribute;
        public IntPtr Data;
        public int SizeOfData;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct AccentPolicy
    {
        public int AccentState;
        public int AccentFlags;
        public uint GradientColor;
        public int AnimationId;
    }
}
