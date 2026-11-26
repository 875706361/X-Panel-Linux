// X-Panel Windows 客户端 —— 连接后的真实连通性探测（延迟 / Google / YouTube）
// 与 Android 端同一套逻辑：客户端自身不能拿物理网卡去测，
// 必须经内核开在 127.0.0.1:10809 的 http 入站出站，结果才代表隧道是否真的通。
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Net;
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
            public int Latency;        // 节点入口 TCP RTT
            public string LatencyErr;
            public int Google;         // HTTP 状态码
            public int GoogleConn;     // 到目标 443 的建连耗时（经 http 入站，回环瞬时值，仅日志参考）
            public int GoogleTtfb;     // 首字节耗时（TLS + 请求 + 响应头）—— 这才是用户感知
            public int Youtube;
            public int YoutubeConn;
            public int YoutubeTtfb;
            public string GoogleErr, YoutubeErr;

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

        /// <summary>
        /// 节点入口的 TCP RTT —— 这就是用户口中的"延迟"。
        /// 不能经本地 http 入站测：xray 的 http inbound 收到 CONNECT 会先回 200 再拨号，
        /// 测出来永远是 1~6ms 的回环值（线上日志已踩到 "延迟 1ms / Google 1ms"）。
        /// 只走 IPv4：域名若带 AAAA，在 IPv6 不通的网络里首个地址就会把预算耗光（线上踩到全部超时）。
        /// </summary>
        internal static int TcpRtt(string host, int port, int timeoutMs)
        {
            System.Net.IPAddress[] addrs;
            try { addrs = Dns.GetHostAddresses(host); }
            catch (Exception ex) { throw new Exception("解析失败: " + ShortErr(ex)); }

            var v4 = new List<System.Net.IPAddress>();
            foreach (var a in addrs)
                if (a.AddressFamily == AddressFamily.InterNetwork && !v4.Contains(a)) v4.Add(a);
            if (v4.Count == 0) throw new Exception("域名没有 IPv4 地址: " + host);

            Exception last = null;
            int best = -1;
            // 两轮 × 每个地址：单次 SYN 被丢不代表不通，同时也不让整项拖过 4.5s
            int perTry = Math.Max(1200, timeoutMs / (v4.Count * 2));
            for (int pass = 0; pass < 2; pass++)
            {
                foreach (var ip in v4)
                {
                    try
                    {
                        int ms = ConnectOnce(ip, port, perTry);
                        if (best < 0 || ms < best) best = ms;
                    }
                    catch (Exception ex) { last = ex; }
                }
                if (best > 0) break;
            }
            if (best > 0) return best;
            throw new Exception(last != null ? ShortErr(last) : "连接超时 " + timeoutMs + "ms");
        }

        private static int ConnectOnce(System.Net.IPAddress ip, int port, int timeoutMs)
        {
            using (var c = new TcpClient())
            {
                c.NoDelay = true;
                var sw = Stopwatch.StartNew();
                var ar = c.BeginConnect(ip, port, null, null);
                if (!ar.AsyncWaitHandle.WaitOne(timeoutMs))
                {
                    try { c.Close(); } catch { }
                    throw new TimeoutException("连接 " + ip + ":" + port + " 超时");
                }
                c.EndConnect(ar);
                return (int)Math.Max(1, sw.ElapsedMilliseconds);
            }
        }

        /// <summary>把异常压成一行可读原因，写日志用。</summary>
        internal static string ShortErr(Exception ex)
        {
            string m = ex == null ? "未知错误" : (ex.Message ?? ex.GetType().Name);
            m = m.Replace("\r", " ").Replace("\n", " ").Trim();
            return m.Length > 60 ? m.Substring(0, 60) + "…" : m;
        }

        /// <summary>经本地 http 代理 CONNECT + TLS + GET，返回 {状态码, 连接耗时, 首字节耗时}。</summary>
        internal static int[] Http(string host, string path, int timeoutMs, out string err)
        {
            err = null;
            int[] res = new int[3];
            string stage = "建连";
            using (var c = new TcpClient())
            {
                try
                {
                    var sw = Stopwatch.StartNew();
                    ConnectLocal(c, timeoutMs);
                    stage = "隧道";
                    NetworkStream ns = c.GetStream();
                    byte[] creq = Encoding.ASCII.GetBytes(
                        "CONNECT " + host + ":443 HTTP/1.1\r\nHost: " + host + ":443\r\n\r\n");
                    ns.Write(creq, 0, creq.Length);
                    ns.Flush();
                    string status = ReadHeader(ns);
                    res[1] = (int)sw.ElapsedMilliseconds;
                    if (status == null || status.IndexOf(" 200") < 0)
                    {
                        err = "本地代理拒绝: " + (status ?? "无响应");
                        throw new Exception(err);
                    }

                    sw.Reset();
                    sw.Start();
                    stage = "TLS";
                    // 探测只发一个 GET /generate_204，不带任何凭据；老系统根证书缺失时忽略证书错误，
                    // 免得把"其实通了、只是证书链不全"误判成隧道不通。
                    using (var ssl = new SslStream(ns, false,
                               new RemoteCertificateValidationCallback(delegate { return true; })))
                    {
                        ssl.AuthenticateAsClient(host);
                        stage = "HTTP";
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
                catch (Exception ex)
                {
                    if (err == null) err = stage + "失败: " + ShortErr(ex);
                    throw;
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
