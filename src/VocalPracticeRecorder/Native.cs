using System.Runtime.InteropServices;
using System.Text;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using VocalPracticeRecorder.Core;

namespace VocalPracticeRecorder;

internal static class Native
{
    private const uint WDA_NONE = 0x00;
    private const uint WDA_EXCLUDEFROMCAPTURE = 0x11;
    private const uint GA_ROOT = 2;
    private const uint GW_HWNDNEXT = 2;
    private const uint SWP_NOZORDER = 0x0004;
    private const uint SWP_NOACTIVATE = 0x0010;
    private const uint MONITOR_DEFAULTTONEAREST = 2;
    private const int RGN_DIFF = 4;
    private const int WM_GETMINMAXINFO = 0x0024;
    private const int DWMWA_USE_IMMERSIVE_DARK_MODE = 20;

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT { public int X, Y; }

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT { public int Left, Top, Right, Bottom; }

    [StructLayout(LayoutKind.Sequential)]
    private struct MINMAXINFO
    {
        public POINT ptReserved, ptMaxSize, ptMaxPosition, ptMinTrackSize, ptMaxTrackSize;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MONITORINFO
    {
        public int cbSize;
        public RECT rcMonitor, rcWork;
        public uint dwFlags;
    }

    [DllImport("user32.dll")] private static extern bool SetWindowDisplayAffinity(IntPtr hWnd, uint affinity);
    [DllImport("user32.dll")] private static extern IntPtr WindowFromPoint(POINT p);
    [DllImport("user32.dll")] private static extern IntPtr GetAncestor(IntPtr hWnd, uint flags);
    [DllImport("user32.dll")] private static extern IntPtr GetWindow(IntPtr hWnd, uint cmd);
    [DllImport("user32.dll")] private static extern bool IsWindowVisible(IntPtr hWnd);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetWindowText(IntPtr hWnd, StringBuilder sb, int max);
    [DllImport("user32.dll")] private static extern bool SetWindowPos(IntPtr hWnd, IntPtr after, int x, int y, int cx, int cy, uint flags);
    [DllImport("user32.dll")] private static extern bool GetWindowRect(IntPtr hWnd, out RECT rect);
    [DllImport("user32.dll")] private static extern int SetWindowRgn(IntPtr hWnd, IntPtr hRgn, bool bRedraw);
    [DllImport("user32.dll")] private static extern IntPtr MonitorFromWindow(IntPtr hWnd, uint flags);
    [DllImport("user32.dll")] private static extern bool GetMonitorInfo(IntPtr hMonitor, ref MONITORINFO info);
    [DllImport("gdi32.dll")] private static extern IntPtr CreateRectRgn(int x1, int y1, int x2, int y2);
    [DllImport("gdi32.dll")] private static extern int CombineRgn(IntPtr dst, IntPtr src1, IntPtr src2, int mode);
    [DllImport("gdi32.dll")] private static extern bool DeleteObject(IntPtr obj);
    [DllImport("dwmapi.dll")] private static extern int DwmSetWindowAttribute(IntPtr hWnd, int attr, ref int val, int size);

    public static IntPtr Handle(Window w) => new WindowInteropHelper(w).EnsureHandle();

    /// <summary>Toggles whether this window is hidden from DXGI/GDI screen capture (Win10 2004+).</summary>
    public static void SetExcludeFromCapture(Window w, bool exclude)
    {
        try { SetWindowDisplayAffinity(Handle(w), exclude ? WDA_EXCLUDEFROMCAPTURE : WDA_NONE); } catch { }
    }

    /// <summary>Hints DWM to use dark titlebar chrome on secondary dialogs (e.g. SettingsWindow).</summary>
    public static void SetDarkTitleBar(Window w, bool dark)
    {
        try { int v = dark ? 1 : 0; DwmSetWindowAttribute(Handle(w), DWMWA_USE_IMMERSIVE_DARK_MODE, ref v, sizeof(int)); } catch { }
    }

    /// <summary>
    /// Punches a crisp rectangular transparent + click-through hole out of <paramref name="w"/> using <c>SetWindowRgn</c>.
    /// Pass <c>null</c> to restore a solid rectangular window (editor mode).
    /// </summary>
    public static void SetWindowHole(Window w, PixelRect? holeWindowLocal, int winW, int winH)
    {
        var hwnd = Handle(w);
        if (holeWindowLocal is not { } h || h.IsEmpty || winW <= 0 || winH <= 0)
        {
            SetWindowRgn(hwnd, IntPtr.Zero, true);
            return;
        }
        IntPtr outer = CreateRectRgn(0, 0, winW, winH);
        IntPtr hole = CreateRectRgn(h.X, h.Y, h.Right, h.Bottom);
        CombineRgn(outer, outer, hole, RGN_DIFF);
        DeleteObject(hole);
        // SetWindowRgn transfers ownership of `outer` to the OS on success (non-zero).
        if (SetWindowRgn(hwnd, outer, true) == 0) DeleteObject(outer);
    }

    /// <summary>Positions a window in physical pixels (bypasses WPF's DIP conversion).</summary>
    public static void SetPhysicalBounds(Window w, PixelRect r) =>
        SetWindowPos(Handle(w), IntPtr.Zero, r.X, r.Y, r.Width, r.Height, SWP_NOZORDER | SWP_NOACTIVATE);

    public static PixelRect GetPhysicalBounds(Window w)
    {
        GetWindowRect(Handle(w), out var r);
        return new PixelRect(r.Left, r.Top, r.Right - r.Left, r.Bottom - r.Top);
    }

    /// <summary>Work area (excluding taskbar) of the monitor hosting <paramref name="w"/>, in physical pixels.</summary>
    public static PixelRect GetMonitorWorkArea(Window w)
    {
        var mon = MonitorFromWindow(Handle(w), MONITOR_DEFAULTTONEAREST);
        var mi = new MONITORINFO { cbSize = Marshal.SizeOf<MONITORINFO>() };
        if (mon != IntPtr.Zero && GetMonitorInfo(mon, ref mi))
            return new PixelRect(mi.rcWork.Left, mi.rcWork.Top, mi.rcWork.Right - mi.rcWork.Left, mi.rcWork.Bottom - mi.rcWork.Top);
        return new PixelRect(0, 0, 1920, 1040);
    }

    /// <summary>Ensures a borderless WindowChrome window maximizes to the monitor work area (never covers the taskbar).</summary>
    public static void InstallWorkAreaMaximizeHook(Window w)
    {
        var src = HwndSource.FromHwnd(Handle(w));
        src?.AddHook((IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled) =>
        {
            if (msg != WM_GETMINMAXINFO) return IntPtr.Zero;
            var mon = MonitorFromWindow(hwnd, MONITOR_DEFAULTTONEAREST);
            if (mon == IntPtr.Zero) return IntPtr.Zero;
            var mi = new MONITORINFO { cbSize = Marshal.SizeOf<MONITORINFO>() };
            if (!GetMonitorInfo(mon, ref mi)) return IntPtr.Zero;

            var mmi = Marshal.PtrToStructure<MINMAXINFO>(lParam);
            mmi.ptMaxPosition.X = mi.rcWork.Left - mi.rcMonitor.Left;
            mmi.ptMaxPosition.Y = mi.rcWork.Top - mi.rcMonitor.Top;
            mmi.ptMaxSize.X = mi.rcWork.Right - mi.rcWork.Left;
            mmi.ptMaxSize.Y = mi.rcWork.Bottom - mi.rcWork.Top;
            var dpi = VisualTreeHelper.GetDpi(w);
            if (w.MinWidth > 0) mmi.ptMinTrackSize.X = (int)Math.Ceiling(w.MinWidth * dpi.DpiScaleX);
            if (w.MinHeight > 0) mmi.ptMinTrackSize.Y = (int)Math.Ceiling(w.MinHeight * dpi.DpiScaleY);
            Marshal.StructureToPtr(mmi, lParam, true);
            handled = true;
            return IntPtr.Zero;
        });
    }

    /// <summary>
    /// Title of the top-level window at a physical screen point (e.g. the browser tab title behind the viewfinder).
    /// Skips <paramref name="ignoreRoot"/> if present at that point.
    /// </summary>
    public static string TopLevelTitleAt(int x, int y, IntPtr ignoreRoot = default)
    {
        var h = WindowFromPoint(new POINT { X = x, Y = y });
        var root = h != IntPtr.Zero ? GetAncestor(h, GA_ROOT) : IntPtr.Zero;
        if (root == IntPtr.Zero) root = h;
        // Walk below our own window in Z-order if WindowFromPoint hit us.
        while (root != IntPtr.Zero && root == ignoreRoot)
        {
            var next = GetWindow(root, GW_HWNDNEXT);
            root = IntPtr.Zero;
            while (next != IntPtr.Zero)
            {
                if (IsWindowVisible(next) && GetWindowRect(next, out var r) && x >= r.Left && x < r.Right && y >= r.Top && y < r.Bottom)
                {
                    var title = WindowTitle(next);
                    if (!string.IsNullOrWhiteSpace(title)) { root = next; break; }
                }
                next = GetWindow(next, GW_HWNDNEXT);
            }
        }
        return root == IntPtr.Zero ? "" : WindowTitle(root);
    }

    private static string WindowTitle(IntPtr hWnd)
    {
        var sb = new StringBuilder(512);
        GetWindowText(hWnd, sb, sb.Capacity);
        return sb.ToString();
    }
}
