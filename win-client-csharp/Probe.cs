// X-Panel Windows 客户端 —— 连接后的真实连通性探测（延迟 / Google / YouTube）
// 与 Android 端同一套逻辑：客户端自身不能拿物理网卡去测，
// 必须经内核开在 127.0.0.1:10809 的 http 入站出站，结果才代表隧道是否真的通。
using System;
using System.Diagnostics;
using System.IO;
using System.Net.Security;
using System.Net.Sockets;
using System.Text;
using System.Threading;

namespace XPanelWin
{
    internal static class Probe
    {
        internal const int HttpPort = 10809;
        internal const int SocksPort = 10808;

        internal sealed class Result
        {
            // 0 = 本轮还没测出，-1 = 测了但失败，>0 = 成功
            public int Latency;        // CONNECT 到 gstatic:443 的耗时
            public int Google;         // HTTP 状态码
            public int GoogleConn;     // 到目标 443 的建连耗时
            public int GoogleTtfb;     // 首字节耗时（TLS + 请求 + 响应头）
            public int Youtube;
            public int YoutubeConn;
            public int YoutubeTtfb;

            public bool AllDone { get { return Latency != 0 && Google != 0 && Youtube != 0; } }

            public bool AllOk
            {
                get
                {
                    return Latency > 0
                        && Google > 0 && Google < 400
                        && Youtube > 0 && Youtube < 400;
                }
            }
        }

        /// <summary>内核把本地代理端口监听起来 = 隧道已就绪，比固定 sleep 准得多。</summary>
        internal static bool WaitPort(int port, int waitMs, Func<bool> cancelled)
        {
            long t0 = Environment.TickCount;
            while (Environment.TickCount - t0 < waitMs)
            {
                if (cancelled != null && cancelled()) return false;
                try
                {
                    using (var c = new TcpClient())
                    {
                        var ar = c.BeginConnect("127.0.0.1", port, null, null);
                        if (ar.AsyncWaitHandle.WaitOne(250) && c.Connected) return true;
                    }
                }
                catch { }
                Thread.Sleep(80);
            }
            return false;
        }

        /// <summary>只测到目标 443 的 TCP 建连（经本地 http 代理 CONNECT），当作"延迟"。</summary>
        internal static int Latency(string host, int timeoutMs)
        {
            using (var c = new TcpClient())
            {
                var sw = Stopwatch.StartNew();
                ConnectLocal(c, timeoutMs);
                NetworkStream ns = c.GetStream();
                byte[] req = Encoding.ASCII.GetBytes(
                    "CONNECT " + host + ":443 HTTP/1.1\r\nHost: " + host + ":443\r\n\r\n");
                ns.Write(req, 0, req.Length);
                ns.Flush();
                string status = ReadHeader(ns);
                long ms = sw.ElapsedMilliseconds;
                if (status == null || status.IndexOf(" 200") < 0)
                    throw new Exception("代理拒绝: " + status);
                return (int)Math.Max(1, ms);
            }
        }

        /// <summary>经本地 http 代理 CONNECT + TLS + GET，返回 {状态码, 连接耗时, 首字节耗时}。</summary>
        internal static int[] Http(string host, string path, int timeoutMs)
        {
            int[] res = new int[3];
            using (var c = new TcpClient())
            {
                var sw = Stopwatch.StartNew();
                ConnectLocal(c, timeoutMs);
                NetworkStream ns = c.GetStream();
                byte[] creq = Encoding.ASCII.GetBytes(
                    "CONNECT " + host + ":443 HTTP/1.1\r\nHost: " + host + ":443\r\n\r\n");
                ns.Write(creq, 0, creq.Length);
                ns.Flush();
                string status = ReadHeader(ns);
                res[1] = (int)sw.ElapsedMilliseconds;
                if (status == null || status.IndexOf(" 200") < 0)
                    throw new Exception("代理拒绝: " + status);

                sw.Reset();
                sw.Start();
                // 探测只发一个 GET /generate_204，不带任何凭据；老系统根证书缺失时忽略证书错误，
                // 免得把"其实通了、只是证书链不全"误判成隧道不通。
                using (var ssl = new SslStream(ns, false,
                           new RemoteCertificateValidationCallback(delegate { return true; })))
                {
                    ssl.AuthenticateAsClient(host);
                    byte[] greq = Encoding.ASCII.GetBytes(
                        "GET " + path + " HTTP/1.1\r\nHost: " + host
                        + "\r\nUser-Agent: XPanel-Win\r\nAccept: */*\r\nConnection: close\r\n\r\n");
                    ssl.Write(greq, 0, greq.Length);
                    ssl.Flush();
                    string line = ReadLine(ssl);
                    res[2] = (int)sw.ElapsedMilliseconds;
                    res[0] = ParseCode(line);
                }
            }
            return res;
        }

        private static void ConnectLocal(TcpClient c, int timeoutMs)
        {
            c.NoDelay = true;
            var ar = c.BeginConnect("127.0.0.1", HttpPort, null, null);
            if (!ar.AsyncWaitHandle.WaitOne(timeoutMs))
                throw new TimeoutException("本地代理未响应");
            c.EndConnect(ar);
            c.ReceiveTimeout = timeoutMs;
            c.SendTimeout = timeoutMs;
        }

        /// <summary>读到响应头结束（空行），返回状态行。</summary>
        private static string ReadHeader(Stream s)
        {
            string first = null;
            int guard = 0;
            while (guard++ < 40)
            {
                string line = ReadLine(s);
                if (line == null) return first;
                if (first == null) first = line;
                if (line.Length == 0) return first;
            }
            return first;
        }

        private static string ReadLine(Stream s)
        {
            var sb = new StringBuilder();
            int b;
            while ((b = s.ReadByte()) >= 0)
            {
                if (b == '\n') break;
                if (b != '\r') sb.Append((char)b);
            }
            if (b < 0 && sb.Length == 0) return null;
            return sb.ToString();
        }

        private static int ParseCode(string statusLine)
        {
            if (statusLine == null) throw new Exception("无响应");
            string[] parts = statusLine.Split(new char[] { ' ' });
            int code;
            if (parts.Length >= 2 && int.TryParse(parts[1], out code)) return code;
            throw new Exception("响应异常: " + statusLine);
        }
    }
}
