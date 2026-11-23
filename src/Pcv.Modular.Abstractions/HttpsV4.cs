using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Text;
using System.Threading.Tasks;

namespace Pcv.Modular.Abstractions
{
    /// <summary>
    /// 强制走 IPv4 的 HTTPS 请求（纯 BCL，不依赖 WPF / Newtonsoft）。
    ///
    /// 为什么需要它 —— 这是 2026-11-23 实测出来的真故障：
    /// 这台机器（以及不少家用网络）到 Cloudflare 边缘的 **IPv6 路由是黑洞**。
    /// DNS 同时返回 IPv4 和 IPv6 时，.NET 的 `HttpWebRequest` 会先试 IPv6，
    /// 于是每个请求都卡满超时（实测 8 秒 `操作超时`）——
    /// 而浏览器和 curl 都好好的，因为它们有 Happy Eyeballs 回退。
    ///
    /// 同一个 URL 换成「Socket 只建 InterNetwork」就 2.5 秒正常返回。
    /// 所以这里自己拨号：`HttpWebRequest` 没有「只许 IPv4」的开关。
    ///
    /// 安全上不放松：TLS 的 SNI 与证书校验仍然用**域名**（`AuthenticateAsClient(host)`），
    /// 只是把 TCP 连接钉在 IPv4 地址上。证书链校验照旧，不做任何 callback 放宽。
    /// </summary>
    public static class HttpsV4
    {
        private static readonly object DnsGate = new object();
        private static readonly Dictionary<string, IPAddress[]> DnsCache =
            new Dictionary<string, IPAddress[]>(StringComparer.OrdinalIgnoreCase);

        /// <summary>
        /// 上次真正连通的地址，优先再试它。
        ///
        /// 为什么需要：Cloudflare 一个域名给两个 IPv4，实测**其中一个可能不通**
        /// （首次请求白等满超时才换下一个，明明 2 秒能完事的请求拖成 16 秒）。
        /// 记下来之后，后续请求一次就命中。
        /// </summary>
        private static readonly Dictionary<string, IPAddress> LastGood =
            new Dictionary<string, IPAddress>(StringComparer.OrdinalIgnoreCase);

        /// <summary>发一个 HTTPS 请求，返回状态码与正文（4xx/5xx 也是正常返回，不抛）。</summary>
        public static (int Status, string Body) Send(string url, string method, string body,
            int timeoutMs = 10000, string contentType = "application/json")
        {
            var uri = new Uri(url);
            if (uri.Scheme != Uri.UriSchemeHttps)
                throw new InvalidOperationException("只支持 https：" + url);

            var addresses = OrderAddresses(uri.Host);
            if (addresses.Length == 0)
                throw new InvalidOperationException("这个域名没有 IPv4 地址：" + uri.Host);

            // 并发抢连，谁先回来用谁（Happy Eyeballs 的思路）。
            //
            // 为什么不串行试：实测这个域名给的两个 IPv4 **都不稳**，
            // 串行的话第一个卡满超时再试第二个，一次请求能拖到 16 秒；
            // 并发之后最坏也就是一个超时的工夫。
            var tasks = new List<Task<((int, string) Result, IPAddress Address)>>();
            foreach (var address in addresses)
            {
                var target = address;
                tasks.Add(Task.Run(() =>
                {
                    var r = SendTo(uri, target, method, body, timeoutMs, contentType);
                    return (r, target);
                }));
            }

            var pending = new List<Task<((int, string) Result, IPAddress Address)>>(tasks);
            Exception last = null;
            while (pending.Count > 0)
            {
                var index = Task.WaitAny(pending.ToArray(), timeoutMs);
                if (index < 0) break;          // 全部没在预算内回来

                var finished = pending[index];
                pending.RemoveAt(index);
                if (finished.Status == TaskStatus.RanToCompletion)
                {
                    var (result, address) = finished.Result;
                    lock (DnsGate) LastGood[uri.Host] = address;
                    return result;
                }
                if (finished.Exception != null)
                    last = finished.Exception.GetBaseException();
            }
            throw last ?? new TimeoutException("请求超时（所有 IPv4 地址都没在预算内返回）");
        }

        /// <summary>上次连通的地址排最前，其余按 DNS 顺序。</summary>
        private static IPAddress[] OrderAddresses(string host)
        {
            var all = ResolveV4(host);
            IPAddress good;
            lock (DnsGate)
            {
                if (!LastGood.TryGetValue(host, out good)) return all;
            }
            var ordered = new List<IPAddress> { good };
            foreach (var a in all)
                if (!a.Equals(good)) ordered.Add(a);
            return ordered.ToArray();
        }

        /// <summary>解析 IPv4，按域名缓存（避免每次请求都查一遍 DNS）。</summary>
        private static IPAddress[] ResolveV4(string host)
        {
            lock (DnsGate)
            {
                if (DnsCache.TryGetValue(host, out var cached) && cached.Length > 0)
                    return cached;

                var list = Dns.GetHostAddresses(host)
                    .Where(a => a.AddressFamily == AddressFamily.InterNetwork)
                    .ToArray();
                if (list.Length > 0) DnsCache[host] = list;
                return list;
            }
        }

        private static (int, string) SendTo(Uri uri, IPAddress address, string method, string body,
            int timeoutMs, string contentType)
        {
            var port = uri.Port > 0 ? uri.Port : 443;

            using (var socket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp))
            {
                socket.ReceiveTimeout = timeoutMs;
                socket.SendTimeout = timeoutMs;

                // Socket.Connect 没有超时参数；用 BeginConnect + WaitOne 才能真的限时
                var pending = socket.BeginConnect(new IPEndPoint(address, port), null, null);
                if (!pending.AsyncWaitHandle.WaitOne(timeoutMs))
                    throw new TimeoutException("连接超时 " + address);
                socket.EndConnect(pending);

                using (var ssl = new SslStream(new NetworkStream(socket, false), false))
                {
                    ssl.ReadTimeout = timeoutMs;
                    ssl.WriteTimeout = timeoutMs;
                    // 传域名而不是 IP：SNI 与证书校验都用域名，跟浏览器一致
                    ssl.AuthenticateAsClient(uri.Host);

                    var head = new StringBuilder();
                    head.Append(method).Append(' ').Append(uri.PathAndQuery).Append(" HTTP/1.1\r\n");
                    head.Append("Host: ").Append(uri.Host).Append("\r\n");
                    head.Append("User-Agent: PCV-HttpsV4/1\r\n");
                    head.Append("Accept: application/json\r\n");
                    head.Append("Accept-Encoding: identity\r\n");   // 不要求压缩，省得解
                    head.Append("Cache-Control: no-cache\r\n");
                    head.Append("Connection: close\r\n");

                    byte[] payload = null;
                    if (body != null)
                    {
                        payload = Encoding.UTF8.GetBytes(body);
                        head.Append("Content-Type: ").Append(contentType).Append("\r\n");
                        head.Append("Content-Length: ").Append(payload.Length).Append("\r\n");
                    }
                    head.Append("\r\n");

                    var headBytes = Encoding.UTF8.GetBytes(head.ToString());
                    ssl.Write(headBytes, 0, headBytes.Length);
                    if (payload != null) ssl.Write(payload, 0, payload.Length);
                    ssl.Flush();

                    return ReadResponse(ssl);
                }
            }
        }

        private static (int, string) ReadResponse(Stream stream)
        {
            // 逐字节读头部：这样才能精确停在 \r\n\r\n，不把正文吃进缓冲区
            var header = new StringBuilder();
            var one = new byte[1];
            while (true)
            {
                var n = stream.Read(one, 0, 1);
                if (n <= 0) throw new IOException("连接在读完响应头之前断了");
                header.Append((char)one[0]);
                var len = header.Length;
                if (len >= 4
                    && header[len - 1] == '\n' && header[len - 2] == '\r'
                    && header[len - 3] == '\n' && header[len - 4] == '\r')
                {
                    break;
                }
                if (len > 65536) throw new IOException("响应头过长");
            }

            var lines = header.ToString().Split(new[] { "\r\n" }, StringSplitOptions.None);
            var status = 0;
            if (lines.Length > 0 && lines[0].StartsWith("HTTP/", StringComparison.Ordinal))
            {
                var parts = lines[0].Split(' ');
                if (parts.Length >= 2) int.TryParse(parts[1], out status);
            }

            long contentLength = -1;
            var chunked = false;
            var gzip = false;
            for (var i = 1; i < lines.Length; i++)
            {
                var line = lines[i];
                var colon = line.IndexOf(':');
                if (colon <= 0) continue;
                var name = line.Substring(0, colon).Trim();
                var value = line.Substring(colon + 1).Trim();
                if (name.Equals("Content-Length", StringComparison.OrdinalIgnoreCase))
                    long.TryParse(value, out contentLength);
                else if (name.Equals("Transfer-Encoding", StringComparison.OrdinalIgnoreCase)
                         && value.IndexOf("chunked", StringComparison.OrdinalIgnoreCase) >= 0)
                    chunked = true;
                else if (name.Equals("Content-Encoding", StringComparison.OrdinalIgnoreCase)
                         && value.IndexOf("gzip", StringComparison.OrdinalIgnoreCase) >= 0)
                    gzip = true;
            }

            byte[] raw;
            if (chunked) raw = ReadChunked(stream, contentLength);
            else raw = ReadFixed(stream, contentLength);

            if (gzip && raw.Length > 0)
            {
                using (var input = new MemoryStream(raw))
                using (var gz = new GZipStream(input, CompressionMode.Decompress))
                using (var output = new MemoryStream())
                {
                    gz.CopyTo(output);
                    raw = output.ToArray();
                }
            }

            return (status, Encoding.UTF8.GetString(raw));
        }

        private static byte[] ReadFixed(Stream stream, long length)
        {
            using (var output = new MemoryStream())
            {
                var buffer = new byte[8192];
                if (length > 0)
                {
                    var remaining = length;
                    while (remaining > 0)
                    {
                        var want = (int)Math.Min(buffer.Length, remaining);
                        var n = stream.Read(buffer, 0, want);
                        if (n <= 0) break;
                        output.Write(buffer, 0, n);
                        remaining -= n;
                    }
                }
                else
                {
                    // 没有 Content-Length 也没 chunked：读到对端关闭（Connection: close）
                    int n;
                    while ((n = stream.Read(buffer, 0, buffer.Length)) > 0)
                        output.Write(buffer, 0, n);
                }
                return output.ToArray();
            }
        }

        private static byte[] ReadChunked(Stream stream, long ignored)
        {
            using (var output = new MemoryStream())
            {
                while (true)
                {
                    var sizeLine = ReadLine(stream);
                    if (sizeLine == null) break;
                    var semicolon = sizeLine.IndexOf(';');
                    if (semicolon >= 0) sizeLine = sizeLine.Substring(0, semicolon);
                    if (!int.TryParse(sizeLine.Trim(), System.Globalization.NumberStyles.HexNumber,
                            System.Globalization.CultureInfo.InvariantCulture, out var size))
                        break;
                    if (size <= 0) break;

                    var buffer = new byte[size];
                    var read = 0;
                    while (read < size)
                    {
                        var n = stream.Read(buffer, read, size - read);
                        if (n <= 0) break;
                        read += n;
                    }
                    output.Write(buffer, 0, read);

                    ReadLine(stream);   // 块尾的 CRLF
                }
                return output.ToArray();
            }
        }

        private static string ReadLine(Stream stream)
        {
            var builder = new StringBuilder();
            var one = new byte[1];
            while (true)
            {
                var n = stream.Read(one, 0, 1);
                if (n <= 0) return builder.Length > 0 ? builder.ToString() : null;
                if (one[0] == '\n') break;
                if (one[0] != '\r') builder.Append((char)one[0]);
            }
            return builder.ToString();
        }
    }
}
