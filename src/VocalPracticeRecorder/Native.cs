using System.Runtime.InteropServices;
using System.Text;
using System.Windows;
using System.Windows.Interop;
using VocalPracticeRecorder.Core;

namespace VocalPracticeRecorder;

internal static class Native
{
    private const uint WDA_EXCLUDEFROMCAPTURE = 0x11;
    private const int GWL_EXSTYLE = -20;
    private const int WS_EX_TRANSPARENT = 0x20;
    private const int WS_EX_LAYERED = 0x80000;
    private const int WS_EX_TOOLWINDOW = 0x80;
    private const int WS_EX_NOACTIVATE = 0x08000000;
    private const uint GA_ROOT = 2;
    private const uint SWP_NOZORDER = 0x0004;
    private const uint SWP_NOACTIVATE = 0x0010;

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT { public int X, Y; }

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT { public int Left, Top, Right, Bottom; }

    [DllImport("user32.dll")] private static extern bool SetWindowDisplayAffinity(IntPtr hWnd, uint affinity);
    [DllImport("user32.dll")] private static extern int GetWindowLong(IntPtr hWnd, int index);
    [DllImport("user32.dll")] private static extern int SetWindowLong(IntPtr hWnd, int index, int value);
    [DllImport("user32.dll")] private static extern IntPtr WindowFromPoint(POINT p);
    [DllImport("user32.dll")] private static extern IntPtr GetAncestor(IntPtr hWnd, uint flags);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetWindowText(IntPtr hWnd, StringBuilder sb, int max);
    [DllImport("user32.dll")] private static extern bool SetWindowPos(IntPtr hWnd, IntPtr after, int x, int y, int cx, int cy, uint flags);
    [DllImport("user32.dll")] private static extern bool GetWindowRect(IntPtr hWnd, out RECT rect);

    public static IntPtr Handle(Window w) => new WindowInteropHelper(w).EnsureHandle();

    /// <summary>Hides our own overlay/control windows from any screen capture (Win10 2004+).</summary>
    public static void ExcludeFromCapture(Window w)
    {
        try { SetWindowDisplayAffinity(Handle(w), WDA_EXCLUDEFROMCAPTURE); } catch { }
    }

    public static void MakeClickThrough(Window w)
    {
        var h = Handle(w);
        SetWindowLong(h, GWL_EXSTYLE, GetWindowLong(h, GWL_EXSTYLE) | WS_EX_TRANSPARENT | WS_EX_LAYERED | WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE);
    }

    public static void MakeNoActivate(Window w)
    {
        var h = Handle(w);
        SetWindowLong(h, GWL_EXSTYLE, GetWindowLong(h, GWL_EXSTYLE) | WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE);
    }

    /// <summary>Positions a window in physical pixels (bypasses WPF's DIP conversion).</summary>
    public static void SetPhysicalBounds(Window w, PixelRect r) =>
        SetWindowPos(Handle(w), IntPtr.Zero, r.X, r.Y, r.Width, r.Height, SWP_NOZORDER | SWP_NOACTIVATE);

    public static PixelRect GetPhysicalBounds(Window w)
    {
        GetWindowRect(Handle(w), out var r);
        return new PixelRect(r.Left, r.Top, r.Right - r.Left, r.Bottom - r.Top);
    }

    /// <summary>Title of the top-level window at a physical screen point (e.g. the browser tab title).</summary>
    public static string TopLevelTitleAt(int x, int y)
    {
        var h = WindowFromPoint(new POINT { X = x, Y = y });
        if (h == IntPtr.Zero) return "";
        var root = GetAncestor(h, GA_ROOT);
        var sb = new StringBuilder(512);
        GetWindowText(root == IntPtr.Zero ? h : root, sb, sb.Capacity);
        return sb.ToString();
    }
}
