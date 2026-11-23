using System.Runtime.InteropServices;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.DXGI;
using VocalPracticeRecorder.Core;

namespace VocalPracticeRecorder.Capture;

/// <summary>Source of BGRA frames for a fixed region.</summary>
internal interface IFrameSource : IDisposable
{
    string Name { get; }
    /// <summary>Waits up to <paramref name="timeoutMs"/> for a new desktop image and copies it into <paramref name="frame"/>.
    /// Returns true if <paramref name="frame"/> was updated.</summary>
    bool TryGrab(byte[] frame, int timeoutMs);
}

/// <summary>DXGI Desktop Duplication: GPU copy of only the region, cheap and works with hardware video.</summary>
internal sealed class DxgiFrameSource : IFrameSource
{
    private readonly IDXGIFactory1 _factory;
    private readonly IDXGIAdapter1 _adapter;
    private readonly IDXGIOutput _output;
    private readonly IDXGIOutput1 _output1;
    private readonly ID3D11Device _device;
    private readonly ID3D11DeviceContext _context;
    private readonly ID3D11Texture2D _staging;
    private IDXGIOutputDuplication? _dup;
    private readonly int _w, _h, _x, _y;
    private bool _hasFrame;

    public string Name => "DXGI";

    public DxgiFrameSource(MonitorInfo monitor, PixelRect region)
    {
        _w = region.Width; _h = region.Height;
        _x = region.X - monitor.Bounds.X; _y = region.Y - monitor.Bounds.Y;
        _factory = DXGI.CreateDXGIFactory1<IDXGIFactory1>();
        try
        {
            _factory.EnumAdapters1((uint)monitor.AdapterIndex, out _adapter).CheckError();
            _adapter.EnumOutputs((uint)monitor.OutputIndex, out _output).CheckError();
            _output1 = _output.QueryInterface<IDXGIOutput1>();
            D3D11.D3D11CreateDevice(_adapter, DriverType.Unknown, DeviceCreationFlags.BgraSupport,
                [FeatureLevel.Level_11_0, FeatureLevel.Level_10_1, FeatureLevel.Level_10_0],
                out ID3D11Device? device, out ID3D11DeviceContext? context).CheckError();
            _device = device!;
            _context = context!;
            _staging = _device.CreateTexture2D(new Texture2DDescription
            {
                Width = (uint)_w, Height = (uint)_h, MipLevels = 1, ArraySize = 1,
                Format = Format.B8G8R8A8_UNorm, SampleDescription = new SampleDescription(1, 0),
                Usage = ResourceUsage.Staging, CPUAccessFlags = CpuAccessFlags.Read, BindFlags = BindFlags.None,
            });
            _dup = _output1.DuplicateOutput(_device); // throws E_ACCESSDENIED / UNSUPPORTED when unavailable
        }
        catch
        {
            Dispose();
            throw;
        }
    }

    public bool TryGrab(byte[] frame, int timeoutMs)
    {
        if (_dup == null)
        {
            try { _dup = _output1.DuplicateOutput(_device); }
            catch { Thread.Sleep(Math.Max(1, timeoutMs)); return false; }
        }
        var r = _dup.AcquireNextFrame((uint)Math.Max(0, timeoutMs), out var info, out var resource);
        if (r.Code == Vortice.DXGI.ResultCode.WaitTimeout.Code) return false;
        if (r.Failure)
        {
            // Access lost (resolution change, UAC, fullscreen switch): recreate next time.
            _dup.Dispose();
            _dup = null;
            return false;
        }
        try
        {
            if (info.LastPresentTime == 0 && info.AccumulatedFrames == 0 && _hasFrame) return false;
            using var tex = resource!.QueryInterface<ID3D11Texture2D>();
            _context.CopySubresourceRegion(_staging, 0, 0, 0, 0, tex, 0,
                new Vortice.Mathematics.Box(_x, _y, 0, _x + _w, _y + _h, 1));
            var map = _context.Map(_staging, 0, MapMode.Read, Vortice.Direct3D11.MapFlags.None);
            try
            {
                int rowBytes = _w * 4;
                for (int y = 0; y < _h; y++)
                    Marshal.Copy(map.DataPointer + y * (nint)map.RowPitch, frame, y * rowBytes, rowBytes);
            }
            finally { _context.Unmap(_staging, 0); }
            _hasFrame = true;
            return true;
        }
        finally
        {
            resource?.Dispose();
            _dup.ReleaseFrame();
        }
    }

    public void Dispose()
    {
        _dup?.Dispose();
        _staging?.Dispose();
        _context?.Dispose();
        _device?.Dispose();
        _output1?.Dispose();
        _output?.Dispose();
        _adapter?.Dispose();
        _factory?.Dispose();
    }
}

/// <summary>GDI BitBlt fallback (hybrid-GPU laptops, duplication denied). Polls at the frame rate.</summary>
internal sealed class GdiFrameSource : IFrameSource
{
    private const int SRCCOPY = 0x00CC0020;
    [DllImport("user32.dll")] private static extern IntPtr GetDC(IntPtr hwnd);
    [DllImport("user32.dll")] private static extern int ReleaseDC(IntPtr hwnd, IntPtr dc);
    [DllImport("gdi32.dll")] private static extern IntPtr CreateCompatibleDC(IntPtr dc);
    [DllImport("gdi32.dll")] private static extern bool DeleteDC(IntPtr dc);
    [DllImport("gdi32.dll")] private static extern IntPtr SelectObject(IntPtr dc, IntPtr obj);
    [DllImport("gdi32.dll")] private static extern bool DeleteObject(IntPtr obj);
    [DllImport("gdi32.dll")] private static extern bool BitBlt(IntPtr dst, int x, int y, int w, int h, IntPtr src, int sx, int sy, int rop);
    [DllImport("gdi32.dll")] private static extern IntPtr CreateDIBSection(IntPtr dc, ref BITMAPINFOHEADER bmi, uint usage, out IntPtr bits, IntPtr section, uint offset);

    [StructLayout(LayoutKind.Sequential)]
    private struct BITMAPINFOHEADER
    {
        public int biSize, biWidth, biHeight;
        public short biPlanes, biBitCount;
        public int biCompression, biSizeImage, biXPelsPerMeter, biYPelsPerMeter, biClrUsed, biClrImportant;
    }

    private readonly PixelRect _r;
    private readonly IntPtr _screen, _mem, _bmp, _old, _bits;

    public string Name => "GDI";

    public GdiFrameSource(PixelRect region)
    {
        _r = region;
        _screen = GetDC(IntPtr.Zero);
        _mem = CreateCompatibleDC(_screen);
        var bmi = new BITMAPINFOHEADER
        {
            biSize = Marshal.SizeOf<BITMAPINFOHEADER>(), biWidth = region.Width, biHeight = -region.Height, // top-down
            biPlanes = 1, biBitCount = 32,
        };
        _bmp = CreateDIBSection(_screen, ref bmi, 0, out _bits, IntPtr.Zero, 0);
        if (_bmp == IntPtr.Zero) { Dispose(); throw new InvalidOperationException("CreateDIBSection failed"); }
        _old = SelectObject(_mem, _bmp);
        if (!BitBlt(_mem, 0, 0, _r.Width, _r.Height, _screen, _r.X, _r.Y, SRCCOPY))
        {
            Dispose();
            throw new UnauthorizedAccessException("无法读取屏幕内容（BitBlt 被拒绝）。");
        }
    }

    public bool TryGrab(byte[] frame, int timeoutMs)
    {
        if (!BitBlt(_mem, 0, 0, _r.Width, _r.Height, _screen, _r.X, _r.Y, SRCCOPY)) return false;
        Marshal.Copy(_bits, frame, 0, frame.Length);
        // Sleep the remaining slot so the caller's pacing loop does not spin.
        if (timeoutMs > 2) Thread.Sleep(timeoutMs - 1);
        return true;
    }

    public void Dispose()
    {
        if (_old != IntPtr.Zero) SelectObject(_mem, _old);
        if (_bmp != IntPtr.Zero) DeleteObject(_bmp);
        if (_mem != IntPtr.Zero) DeleteDC(_mem);
        if (_screen != IntPtr.Zero) ReleaseDC(IntPtr.Zero, _screen);
    }
}

/// <summary>Moving test pattern; used only by the self-test when the desktop is not accessible.</summary>
internal sealed class SyntheticFrameSource(int w, int h) : IFrameSource
{
    private int _n;
    public string Name => "Synthetic";

    public bool TryGrab(byte[] frame, int timeoutMs)
    {
        if (timeoutMs > 2) Thread.Sleep(timeoutMs - 1);
        int bar = (_n++ * 8) % w;
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
            {
                int i = (y * w + x) * 4;
                byte v = (byte)(Math.Abs(x - bar) < 20 ? 255 : (x ^ y) & 0x3F);
                frame[i] = v; frame[i + 1] = v; frame[i + 2] = v; frame[i + 3] = 255;
            }
        return true;
    }

    public void Dispose() { }
}
