using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;

namespace VocalPracticeRecorder.Core;

public sealed record LanAddress(string Ip, string AdapterName)
{
    public override string ToString() => $"{Ip} ({AdapterName})";
}

/// <summary>
/// Minimal zero-admin LAN HTTP/1.1 file transfer server using TcpListener (never requires netsh urlacl).
/// Serves only one authorized file behind a random session token, with a mobile-friendly landing page
/// (for WeChat/Android QR scanners) and full Range-request download support.
/// </summary>
public sealed class LanFileShareServer : IDisposable
{
    private readonly string _filePath;
    private readonly string _fileName;
    private readonly long _fileSize;
    private readonly string _contentType;
    private readonly string _token;
    private readonly TcpListener _listener;
    private readonly CancellationTokenSource _cts = new();
    private int _downloadCount;

    public int Port { get; }
    public string Token => _token;
    public int DownloadCount => Volatile.Read(ref _downloadCount);
    public event Action<int>? DownloadCountChanged;

    public LanFileShareServer(string filePath)
    {
        if (!File.Exists(filePath)) throw new FileNotFoundException("找不到要分享的文件。", filePath);
        _filePath = filePath;
        _fileName = Path.GetFileName(filePath);
        _fileSize = new FileInfo(filePath).Length;
        _contentType = Path.GetExtension(filePath).ToLowerInvariant() switch
        {
            ".mp4" => "video/mp4",
            ".mp3" => "audio/mpeg",
            _ => "application/octet-stream",
        };
        _token = Convert.ToHexString(RandomNumberGenerator.GetBytes(8)).ToLowerInvariant();
        _listener = new TcpListener(IPAddress.Any, 0);
        _listener.Start();
        Port = ((IPEndPoint)_listener.LocalEndpoint).Port;
        _ = Task.Run(() => AcceptLoopAsync(_cts.Token));
    }

    public string LandingUrl(string hostIp) => $"http://{hostIp}:{Port}/p/{_token}";
    public string DownloadUrl(string hostIp) => $"http://{hostIp}:{Port}/dl/{_token}/{Uri.EscapeDataString(_fileName)}";

    /// <summary>Returns candidate local LAN IPv4 addresses sorted with the real Wi-Fi/Ethernet gateway adapter first.</summary>
    public static List<LanAddress> DiscoverLanIPv4s()
    {
        string[] virtualHints =
        [
            "vethernet", "wsl", "hyper-v", "docker", "vmware", "virtualbox", "vbox",
            "tailscale", "zerotier", "clash", "mihomo", "sing-box", "tap-", "wintun", "meta", "radmin", "loopback",
        ];

        var candidates = new List<(int Score, LanAddress Addr)>();
        foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
        {
            if (nic.OperationalStatus != OperationalStatus.Up) continue;
            if (nic.NetworkInterfaceType is NetworkInterfaceType.Loopback or NetworkInterfaceType.Tunnel) continue;

            var props = nic.GetIPProperties();
            bool hasGateway = props.GatewayAddresses.Any(g => g.Address.AddressFamily == AddressFamily.InterNetwork && !IPAddress.Any.Equals(g.Address));
            string nameLower = $"{nic.Name} {nic.Description}".ToLowerInvariant();
            bool looksVirtual = virtualHints.Any(h => nameLower.Contains(h));

            foreach (var ua in props.UnicastAddresses)
            {
                if (ua.Address.AddressFamily != AddressFamily.InterNetwork) continue;
                var bytes = ua.Address.GetAddressBytes();
                if (bytes[0] == 127 || (bytes[0] == 169 && bytes[1] == 254)) continue; // loopback / APIPA

                int score = 0;
                if (hasGateway) score += 100;
                if (!looksVirtual) score += 50;
                if (bytes[0] == 192 && bytes[1] == 168) score += 30;
                else if (bytes[0] == 10) score += 20;
                else if (bytes[0] == 172 && bytes[1] >= 16 && bytes[1] <= 31) score += 20;
                if (nic.NetworkInterfaceType == NetworkInterfaceType.Wireless80211) score += 5;

                candidates.Add((score, new LanAddress(ua.Address.ToString(), nic.Name)));
            }
        }

        return candidates.OrderByDescending(c => c.Score).Select(c => c.Addr).ToList();
    }

    private async Task AcceptLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            TcpClient client;
            try { client = await _listener.AcceptTcpClientAsync(ct).ConfigureAwait(false); }
            catch { break; }
            _ = Task.Run(() => HandleClientAsync(client, ct), ct);
        }
    }

    private async Task HandleClientAsync(TcpClient client, CancellationToken ct)
    {
        using (client)
        {
            client.ReceiveTimeout = 10000;
            client.SendTimeout = 30000;
            try
            {
                using var stream = client.GetStream();
                var reqText = await ReadRequestHeaderAsync(stream, ct).ConfigureAwait(false);
                if (string.IsNullOrEmpty(reqText)) return;

                var lines = reqText.Split("\r\n", StringSplitOptions.RemoveEmptyEntries);
                if (lines.Length == 0) return;
                var parts = lines[0].Split(' ', 3);
                if (parts.Length < 2) return;
                string method = parts[0].ToUpperInvariant();
                string rawPath = parts[1].Split('?', 2)[0];

                if (method != "GET" && method != "HEAD")
                {
                    await WriteSimpleAsync(stream, "405 Method Not Allowed", "text/plain; charset=utf-8", "Method Not Allowed", headOnly: false, ct).ConfigureAwait(false);
                    return;
                }

                string? rangeHeader = null;
                for (int i = 1; i < lines.Length; i++)
                {
                    if (lines[i].StartsWith("Range:", StringComparison.OrdinalIgnoreCase))
                    {
                        rangeHeader = lines[i][6..].Trim();
                        break;
                    }
                }

                if (rawPath == $"/p/{_token}")
                {
                    string html = BuildLandingHtml();
                    await WriteSimpleAsync(stream, "200 OK", "text/html; charset=utf-8", html, method == "HEAD", ct).ConfigureAwait(false);
                    return;
                }

                if (rawPath == $"/dl/{_token}" || rawPath.StartsWith($"/dl/{_token}/", StringComparison.Ordinal))
                {
                    await WriteFileAsync(stream, rangeHeader, attachment: true, headOnly: method == "HEAD", ct).ConfigureAwait(false);
                    return;
                }

                if (rawPath == $"/raw/{_token}")
                {
                    await WriteFileAsync(stream, rangeHeader, attachment: false, headOnly: method == "HEAD", ct).ConfigureAwait(false);
                    return;
                }

                await WriteSimpleAsync(stream, "404 Not Found", "text/plain; charset=utf-8", "Not Found", method == "HEAD", ct).ConfigureAwait(false);
            }
            catch { }
        }
    }

    private static async Task<string> ReadRequestHeaderAsync(NetworkStream stream, CancellationToken ct)
    {
        var buf = new byte[8192];
        int total = 0;
        while (total < buf.Length)
        {
            int n = await stream.ReadAsync(buf.AsMemory(total, buf.Length - total), ct).ConfigureAwait(false);
            if (n <= 0) break;
            total += n;
            var text = Encoding.ASCII.GetString(buf, 0, total);
            if (text.Contains("\r\n\r\n", StringComparison.Ordinal)) return text;
        }
        return total > 0 ? Encoding.ASCII.GetString(buf, 0, total) : "";
    }

    private static async Task WriteSimpleAsync(NetworkStream stream, string status, string contentType, string body, bool headOnly, CancellationToken ct)
    {
        var bytes = Encoding.UTF8.GetBytes(body);
        var hdr = $"HTTP/1.1 {status}\r\nContent-Type: {contentType}\r\nContent-Length: {bytes.Length}\r\nCache-Control: no-store\r\nConnection: close\r\n\r\n";
        await stream.WriteAsync(Encoding.ASCII.GetBytes(hdr), ct).ConfigureAwait(false);
        if (!headOnly && bytes.Length > 0) await stream.WriteAsync(bytes, ct).ConfigureAwait(false);
    }

    private async Task WriteFileAsync(NetworkStream stream, string? rangeHeader, bool attachment, bool headOnly, CancellationToken ct)
    {
        long start = 0, end = _fileSize - 1;
        bool partial = false;
        if (!string.IsNullOrEmpty(rangeHeader) && rangeHeader.StartsWith("bytes=", StringComparison.OrdinalIgnoreCase))
        {
            var spec = rangeHeader[6..].Split(',')[0].Trim();
            var dash = spec.IndexOf('-');
            if (dash >= 0)
            {
                string a = spec[..dash].Trim(), b = spec[(dash + 1)..].Trim();
                if (a.Length == 0 && long.TryParse(b, out long suffix) && suffix > 0)
                {
                    start = Math.Max(0, _fileSize - suffix);
                    end = _fileSize - 1;
                    partial = true;
                }
                else if (long.TryParse(a, out long sVal) && sVal >= 0 && sVal < _fileSize)
                {
                    start = sVal;
                    end = (b.Length > 0 && long.TryParse(b, out long eVal)) ? Math.Min(_fileSize - 1, eVal) : _fileSize - 1;
                    if (end >= start) partial = true;
                }
            }
        }

        long length = Math.Max(0, end - start + 1);
        var sb = new StringBuilder();
        sb.Append(partial ? "HTTP/1.1 206 Partial Content\r\n" : "HTTP/1.1 200 OK\r\n");
        sb.Append($"Content-Type: {_contentType}\r\n");
        sb.Append("Accept-Ranges: bytes\r\n");
        sb.Append($"Content-Length: {length}\r\n");
        if (partial) sb.Append($"Content-Range: bytes {start}-{end}/{_fileSize}\r\n");
        if (attachment)
        {
            string asciiFallback = _contentType == "video/mp4" ? "vocal-practice.mp4" : "vocal-practice.mp3";
            sb.Append($"Content-Disposition: attachment; filename=\"{asciiFallback}\"; filename*=UTF-8''{Uri.EscapeDataString(_fileName)}\r\n");
        }
        sb.Append("Cache-Control: no-store\r\nConnection: close\r\n\r\n");
        await stream.WriteAsync(Encoding.ASCII.GetBytes(sb.ToString()), ct).ConfigureAwait(false);
        if (headOnly || length == 0) return;

        using var fs = new FileStream(_filePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 65536, useAsync: true);
        fs.Seek(start, SeekOrigin.Begin);
        var chunk = new byte[65536];
        long left = length;
        while (left > 0)
        {
            int n = await fs.ReadAsync(chunk.AsMemory(0, (int)Math.Min(chunk.Length, left)), ct).ConfigureAwait(false);
            if (n <= 0) break;
            await stream.WriteAsync(chunk.AsMemory(0, n), ct).ConfigureAwait(false);
            left -= n;
        }
        await stream.FlushAsync(ct).ConfigureAwait(false);

        if (attachment && left == 0 && start == 0)
        {
            int c = Interlocked.Increment(ref _downloadCount);
            DownloadCountChanged?.Invoke(c);
        }
    }

    private string BuildLandingHtml()
    {
        string safeName = WebUtility.HtmlEncode(_fileName);
        string sizeStr = $"{_fileSize / 1024.0 / 1024.0:0.0} MB";
        string dlHref = $"/dl/{_token}/{Uri.EscapeDataString(_fileName)}";
        string rawHref = $"/raw/{_token}";
        string player = _contentType == "video/mp4"
            ? $"<video controls playsinline preload=\"metadata\" src=\"{rawHref}\" style=\"width:100%;border-radius:10px;background:#000;max-height:52vh\"></video>"
            : $"<audio controls preload=\"metadata\" src=\"{rawHref}\" style=\"width:100%\"></audio>";

        return $$"""
        <!DOCTYPE html>
        <html lang="zh-CN">
        <head>
          <meta charset="UTF-8" />
          <meta name="viewport" content="width=device-width, initial-scale=1.0" />
          <title>{{safeName}}</title>
          <style>
            body{margin:0;padding:20px;background:#14171d;color:#eef1f6;font-family:-apple-system,BlinkMacSystemFont,"Segoe UI",sans-serif}
            .card{max-width:460px;margin:0 auto;background:#1d212a;border:1px solid #2e3442;border-radius:14px;padding:18px}
            h1{font-size:16px;margin:0 0 6px;word-break:break-all;line-height:1.4}
            .meta{font-size:13px;color:#8f96a3;margin-bottom:14px}
            .btn{display:block;text-align:center;text-decoration:none;background:#3ba8f5;color:#fff;font-weight:600;font-size:15px;padding:13px 16px;border-radius:10px;margin-top:14px}
            .tip{font-size:12px;color:#8f96a3;margin-top:12px;line-height:1.5}
          </style>
        </head>
        <body>
          <div class="card">
            <h1>{{safeName}}</h1>
            <div class="meta">练唱录制 · 局域网直传 · {{sizeStr}}</div>
            {{player}}
            <a class="btn" href="{{dlHref}}" download="{{safeName}}">下载到手机</a>
            <div class="tip">若在微信内扫码打开无法直接保存，请点右上角「···」选择「在浏览器中打开」，再点下载即可保存到手机相册/文件。</div>
          </div>
        </body>
        </html>
        """;
    }

    public void Dispose()
    {
        try { _cts.Cancel(); } catch { }
        try { _listener.Stop(); } catch { }
        _cts.Dispose();
    }
}
