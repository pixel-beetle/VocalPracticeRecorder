using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using QRCoder;
using VocalPracticeRecorder.Core;

namespace VocalPracticeRecorder.Views;

public partial class PhoneShareWindow : Window
{
    private readonly LanFileShareServer _server;
    private readonly List<LanAddress> _addrs;

    public PhoneShareWindow(string filePath)
    {
        InitializeComponent();
        _server = new LanFileShareServer(filePath);
        _server.DownloadCountChanged += count =>
            Dispatcher.BeginInvoke(() => StatusText.Text = $"✓ 已传到手机 {count} 次");

        var fi = new FileInfo(filePath);
        FileNameText.Text = fi.Name;
        FileNameText.ToolTip = filePath;
        FileMetaText.Text = $"{LibraryCleanup.FormatBytes(fi.Length)} · 局域网直传";

        _addrs = LanFileShareServer.DiscoverLanIPv4s();
        if (_addrs.Count == 0) _addrs.Add(new LanAddress("127.0.0.1", "本机"));

        if (_addrs.Count > 1)
        {
            AdapterRow.Visibility = Visibility.Visible;
            AdapterBox.ItemsSource = _addrs;
            AdapterBox.SelectedIndex = 0;
        }
        UpdateQr(_addrs[0].Ip);
        Closed += (_, _) => _server.Dispose();
    }

    private void OnAdapterChanged(object sender, SelectionChangedEventArgs e)
    {
        if (AdapterBox.SelectedItem is LanAddress a) UpdateQr(a.Ip);
    }

    private void UpdateQr(string ip)
    {
        string url = _server.LandingUrl(ip);
        UrlBox.Text = url;
        QrImage.Source = RenderQrBitmap(url);
    }

    /// <summary>Renders a crisp monochrome QR WriteableBitmap directly from QRCoder's ModuleMatrix (zero GDI/System.Drawing).</summary>
    public static BitmapSource RenderQrBitmap(string payload, int pixelsPerModule = 7)
    {
        using var gen = new QRCodeGenerator();
        using var data = gen.CreateQrCode(payload, QRCodeGenerator.ECCLevel.M);
        var matrix = data.ModuleMatrix;
        int modules = matrix.Count;
        int size = modules * pixelsPerModule;
        var pixels = new byte[size * size * 4];

        for (int my = 0; my < modules; my++)
        {
            var row = matrix[my];
            for (int mx = 0; mx < modules; mx++)
            {
                byte v = row[mx] ? (byte)0x14 : (byte)0xFF;
                for (int dy = 0; dy < pixelsPerModule; dy++)
                {
                    int py = my * pixelsPerModule + dy;
                    int baseIdx = (py * size + mx * pixelsPerModule) * 4;
                    for (int dx = 0; dx < pixelsPerModule; dx++, baseIdx += 4)
                    {
                        pixels[baseIdx] = v;
                        pixels[baseIdx + 1] = v;
                        pixels[baseIdx + 2] = v;
                        pixels[baseIdx + 3] = 255;
                    }
                }
            }
        }

        var bmp = BitmapSource.Create(size, size, 96, 96, PixelFormats.Bgra32, null, pixels, size * 4);
        bmp.Freeze();
        return bmp;
    }

    private void OnCopyUrl(object sender, RoutedEventArgs e)
    {
        Clipboard.SetText(UrlBox.Text);
        CopyUrlButton.Content = "已复制";
    }

    private void OnClose(object sender, RoutedEventArgs e) => Close();
}
