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
    private const int WM_GETMINMAXINFO = 0x0024;
    private const int WM_NCHITTEST = 0x0084;
    private const int DWMWA_SYSTEMBACKDROP_TYPE = 38;
    private const int DWMSBT_NONE = 1;

    private const int HTLEFT = 10, HTRIGHT = 11, HTTOP = 12, HTTOPLEFT = 13, HTTOPRIGHT = 14,
        HTBOTTOM = 15, HTBOTTOMLEFT = 16, HTBOTTOMRIGHT = 17;

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT { public int X, Y; }

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT { public int Left, Top, Right, Bottom; }

    [StructLayout(LayoutKind.Sequential)]
    private struct MARGINS { public int Left, Right, Top, Bottom; }

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
    [DllImport("user32.dll")] private static extern IntPtr MonitorFromWindow(IntPtr hWnd, uint flags);
    [DllImport("user32.dll")] private static extern bool GetMonitorInfo(IntPtr hMonitor, ref MONITORINFO info);
    [DllImport("dwmapi.dll")] private static extern int DwmSetWindowAttribute(IntPtr hWnd, int attr, ref int val, int size);
    [DllImport("dwmapi.dll")] private static extern int DwmGetWindowAttribute(IntPtr hWnd, int attr, out int val, int size);
    [DllImport("dwmapi.dll")] private static extern int DwmExtendFrameIntoClientArea(IntPtr hWnd, ref MARGINS margins);

    public static IntPtr Handle(Window w) => new WindowInteropHelper(w).EnsureHandle();

    /// <summary>Toggles whether this window is hidden from DXGI/GDI screen capture (Win10 2004+).</summary>
    public static bool SetExcludeFromCapture(Window w, bool exclude)
    {
        try { return SetWindowDisplayAffinity(Handle(w), exclude ? WDA_EXCLUDEFROMCAPTURE : WDA_NONE); }
        catch { return false; }
    }

    /// <summary>Ensures Windows 11 DWM never paints a Mica/Acrylic system backdrop behind our transparent viewport.</summary>
    public static void DisableBackdrop(Window w)
    {
        try
        {
            var hwnd = Handle(w);
            int none = DWMSBT_NONE;
            DwmSetWindowAttribute(hwnd, DWMWA_SYSTEMBACKDROP_TYPE, ref none, sizeof(int));
            var zero = default(MARGINS);
            DwmExtendFrameIntoClientArea(hwnd, ref zero);
        }
        catch { }
    }

    public static int GetBackdropType(Window w)
    {
        try { return DwmGetWindowAttribute(Handle(w), DWMWA_SYSTEMBACKDROP_TYPE, out int v, sizeof(int)) == 0 ? v : -1; }
        catch { return -1; }
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

    /// <summary>
    /// Installs native Win32 hooks for a borderless transparent window:
    /// 1) WM_GETMINMAXINFO so maximizing respects the taskbar and honours MinWidth/MinHeight.
    /// 2) WM_NCHITTEST so dragging the 6px outer edges/corners resizes natively with OS cursors.
    /// </summary>
    public static void InstallBorderlessHooks(Window w, Func<bool> canResize, int resizeBorderDip = 6)
    {
        var src = HwndSource.FromHwnd(Handle(w));
        src?.AddHook((IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled) =>
        {
            if (msg == WM_GETMINMAXINFO)
            {
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
            }
            if (msg == WM_NCHITTEST && w.WindowState == WindowState.Normal && canResize())
            {
                // Signed 16-bit screen coords (important for monitors to the left/above primary).
                long lp = lParam.ToInt64();
                int x = (short)(lp & 0xFFFF);
                int y = (short)((lp >> 16) & 0xFFFF);
                GetWindowRect(hwnd, out var r);
                var dpi = VisualTreeHelper.GetDpi(w);
                int bx = (int)Math.Ceiling(resizeBorderDip * dpi.DpiScaleX);
                int by = (int)Math.Ceiling(resizeBorderDip * dpi.DpiScaleY);
                bool left = x >= r.Left && x < r.Left + bx;
                bool right = x <= r.Right && x >= r.Right - bx;
                bool top = y >= r.Top && y < r.Top + by;
                bool bottom = y <= r.Bottom && y >= r.Bottom - by;

                int hit = (top, bottom, left, right) switch
                {
                    (true, _, true, _) => HTTOPLEFT,
                    (true, _, _, true) => HTTOPRIGHT,
                    (_, true, true, _) => HTBOTTOMLEFT,
                    (_, true, _, true) => HTBOTTOMRIGHT,
                    (true, _, _, _) => HTTOP,
                    (_, true, _, _) => HTBOTTOM,
                    (_, _, true, _) => HTLEFT,
                    (_, _, _, true) => HTRIGHT,
                    _ => 0,
                };
                if (hit != 0)
                {
                    handled = true;
                    return new IntPtr(hit);
                }
            }
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
