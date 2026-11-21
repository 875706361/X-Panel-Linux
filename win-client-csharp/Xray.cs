// Xray 内核进程管理：释放内嵌资源、写配置、拉起/关闭
// v10.1：四协议 xray 出站（vless reality / trojan tls / vmess tcp+ws+tls / ss）+ hy2 独立 hysteria.exe 内核。
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Threading.Tasks;
using System.Web.Script.Serialization;

namespace XPanelWin
{
    internal static class Xray
    {
        // 内嵌资源解压释放到 %TEMP%\XPanel 隐藏目录，软件所在目录（桌面/文件夹）不产生任何文件
        private static readonly string[] RequiredFiles = { "xray.exe", "geoip.dat", "geosite.dat", "hysteria.exe" };

        public static string BinDir
        {
            get
            {
                // 诊断/测试用：XPL_KERNEL_DIR 指向已有内核的目录（Windows 上可指向解压好的 %TEMP%\XPanel，
                // 省掉每次释放 90MB 资源；Linux 上用 mono 跑界面时指向 Linux 版内核做联调）
                string env = Environment.GetEnvironmentVariable("XPL_KERNEL_DIR");
                if (!string.IsNullOrEmpty(env) && Directory.Exists(env)) return env;
                string dir = Path.Combine(Path.GetTempPath(), "XPanel");
                if (!Directory.Exists(dir))
                {
                    Directory.CreateDirectory(dir);
                }
                return dir;
            }
        }

        // 释放内嵌资源。已存在且字节数一致则跳过，避免每次启动写盘。
        public static void EnsureFiles()
        {
            if (!string.IsNullOrEmpty(Environment.GetEnvironmentVariable("XPL_KERNEL_DIR"))) return;   // 外部内核目录，不用释放
            var asm = System.Reflection.Assembly.GetExecutingAssembly();
            foreach (string name in RequiredFiles)
            {
                string path = Path.Combine(BinDir, name);
                string resName = "XPanelWin.resources." + name + ".gz";
                byte[] data;
                try { data = ReadResource(asm, resName); }
                catch
                {
                    // 资源里没有（比如只拷了 exe），磁盘上有就用磁盘的
                    if (File.Exists(path)) continue;
                    throw new FileNotFoundException("缺少内核文件: " + name);
                }
                bool needWrite = true;
                if (File.Exists(path))
                {
                    try
                    {
                        byte[] existing = File.ReadAllBytes(path);
                        needWrite = existing.Length != data.Length;
                        if (!needWrite)
                        {
                            using (var a = new System.Security.Cryptography.SHA256Managed())
                            {
                                byte[] ha = a.ComputeHash(existing), hb = a.ComputeHash(data);
                                for (int i = 0; i < ha.Length; i++)
                                    if (ha[i] != hb[i]) { needWrite = true; break; }
                            }
                        }
                    }
                    catch { needWrite = true; }
                }
                if (needWrite)
                {
                    // 资源是 gzip 包装的，这里解出原始字节再落盘
                    byte[] raw = Gunzip(data);
                    File.WriteAllBytes(path, raw);
                    MainForm.LogToFile($"[XRAY] 释放 {name} ({raw.Length / 1024 / 1024}MB)");
                }
            }
        }

        // 解压 gzip 资源。GZipStream 只能读一次，所以每次重新构造。
        private static byte[] Gunzip(byte[] gz)
        {
            using (var input = new MemoryStream(gz))
            using (var gzs = new System.IO.Compression.GZipStream(input, System.IO.Compression.CompressionMode.Decompress))
            using (var output = new MemoryStream())
            {
                gzs.CopyTo(output);
                return output.ToArray();
            }
        }

        private static byte[] ReadResource(System.Reflection.Assembly asm, string name)
        {
            using (Stream s = asm.GetManifestResourceStream(name))
            {
                if (s == null) throw new FileNotFoundException(name);
                byte[] buf = new byte[s.Length];
                int read = 0;
                while (read < buf.Length)
                {
                    int n = s.Read(buf, read, buf.Length - read);
                    if (n <= 0) break;
                    read += n;
                }
                return buf;
            }
        }

        // v10.1：五协议。hy2 走 BuildHy2Config(hysteria.exe)，其余走 BuildConfig(xray.exe)。
        public static string BuildConfig(NodeInfo n, bool bypassCn)
        {
            object outbound;
            if (n.proto == "ss")
            {
                int ci = n.uuid.IndexOf(':');
                outbound = new Dictionary<string, object>
                {
                    ["protocol"] = "shadowsocks",
                    ["settings"] = new Dictionary<string, object>
                    {
                        ["servers"] = new object[]
                        {
                            new Dictionary<string, object>
                            {
                                ["address"] = n.address, ["port"] = int.Parse(n.port),
                                ["method"] = n.uuid.Substring(0, ci), ["password"] = n.uuid.Substring(ci + 1),
                                ["ota"] = false, ["level"] = 0
                            }
                        }
                    },
                    ["tag"] = "proxy"
                };
            }
            else
            {
                var user = new Dictionary<string, object>();
                if (n.proto == "vless") { user["id"] = n.uuid; user["encryption"] = "none"; if (!string.IsNullOrEmpty(n.flow)) user["flow"] = n.flow; }
                else if (n.proto == "vmess") { user["id"] = n.uuid; user["alterId"] = 0; user["security"] = "auto"; }
                else { user["password"] = n.uuid; }   // trojan

                var stream = new Dictionary<string, object>();
                if (n.proto == "vmess")
                {
                    stream["network"] = n.net;
                    if (n.net == "ws")
                        stream["wsSettings"] = new Dictionary<string, object> { ["path"] = string.IsNullOrEmpty(n.path) ? "/" : n.path, ["headers"] = new Dictionary<string, object> { ["Host"] = n.type } };
                }
                if (n.proto == "vless")
                {
                    stream["network"] = "tcp";
                    stream["security"] = "reality";
                    stream["realitySettings"] = new Dictionary<string, object>
                    {
                        ["show"] = false, ["fingerprint"] = "chrome",
                        ["serverName"] = n.sni, ["publicKey"] = n.pbk,
                        ["shortId"] = n.sid, ["spiderX"] = ""
                    };
                }
                else if (n.proto == "trojan" || (n.proto == "vmess" && n.security == "tls"))
                {
                    stream["security"] = "tls";
                    var tls = new Dictionary<string, object> { ["allowInsecure"] = false, ["fingerprint"] = "chrome" };
                    if (!string.IsNullOrEmpty(n.sni)) tls["serverName"] = n.sni;
                    stream["tlsSettings"] = tls;
                }

                outbound = new Dictionary<string, object>
                {
                    ["protocol"] = n.proto,
                    ["settings"] = new Dictionary<string, object>
                    {
                        ["vnext"] = new object[]
                        {
                            new Dictionary<string, object>
                            {
                                ["address"] = n.address, ["port"] = int.Parse(n.port),
                                ["users"] = new object[] { user }
                            }
                        }
                    },
                    ["streamSettings"] = stream,
                    ["tag"] = "proxy"
                };
            }

            var conf = new Dictionary<string, object>
            {
                ["log"] = new Dictionary<string, object> { ["loglevel"] = "warning" },
                ["inbounds"] = new object[]
                {
                    new Dictionary<string, object>
                    {
                        ["tag"] = "socks-in", ["port"] = 10808, ["listen"] = "127.0.0.1",
                        ["protocol"] = "socks",
                        ["settings"] = new Dictionary<string, object> { ["auth"] = "noauth", ["udp"] = true }
                    },
                    new Dictionary<string, object>
                    {
                        ["tag"] = "http-in", ["port"] = 10809, ["listen"] = "127.0.0.1",
                        ["protocol"] = "http",
                        ["settings"] = new Dictionary<string, object> { ["allowTransparent"] = false }
                    }
                },
                ["outbounds"] = new object[]
                {
                    outbound,
                    new Dictionary<string, object> { ["protocol"] = "freedom", ["tag"] = "direct" }
                }
            };
            if (bypassCn)
            {
                conf["routing"] = new Dictionary<string, object>
                {
                    ["domainStrategy"] = "IPIfNonMatch",
                    ["rules"] = new object[]
                    {
                        new Dictionary<string, object> { ["type"] = "field", ["domain"] = new object[] { "geosite:cn" }, ["outboundTag"] = "direct" },
                        new Dictionary<string, object> { ["type"] = "field", ["ip"] = new object[] { "geoip:cn", "geoip:private" }, ["outboundTag"] = "direct" }
                    }
                };
            }
            var jss = new JavaScriptSerializer();
            return jss.Serialize(conf);
        }

        // hysteria2 走独立内核 hysteria.exe（Xray 不支持 hy2）。
        // 配置用官方 YAML：socks+http 入站 10808/10809 与 xray 版对齐；bypassCn 用 acl 分流。
        //
        // 三个必须踩准的点（都是内核会直接 FATAL 或静默走错的）：
        // 1) obfs 密码在 hysteria2 里是 **按类型名嵌套**（obfs.salamander.password），
        //    写成 obfs.password 会被当成"没设密码"，内核报
        //    `invalid config: obfs.salamander.password: PSK must be at least 4 bytes` 后秒退。
        // 2) 所有标量都要 YAML 引号：密码里出现 `:` `#` `{` 或指到 `-`，裸写会被截断/报错，
        //    现象是"配置看起来没错但认证一直失败"。
        // 3) server 里的 IPv6 地址必须带方括号。
        public static string BuildHy2Config(NodeInfo n, bool bypassCn)
        {
            string host = n.address.IndexOf(':') >= 0 ? "[" + n.address + "]" : n.address;
            var sb = new StringBuilder();
            sb.Append("server: ").Append(Q(host + ":" + n.port)).Append('\n');
            sb.Append("auth: ").Append(Q(n.uuid)).Append('\n');
            sb.Append("tls:\n  sni: ").Append(Q(n.sni.Length > 0 ? n.sni : n.address)).Append('\n');
            if (n.insecure) sb.Append("  insecure: true\n");
            if (!string.IsNullOrEmpty(n.pbk)) sb.Append("  pinSHA256: ").Append(Q(n.pbk)).Append('\n');
            if (!string.IsNullOrEmpty(n.obfs) && !string.IsNullOrEmpty(n.obfsPw))
            {
                sb.Append("obfs:\n  type: ").Append(n.obfs).Append('\n');
                sb.Append("  ").Append(n.obfs).Append(":\n    password: ").Append(Q(n.obfsPw)).Append('\n');
            }
            // 带宽只是声明，内核会自适应；链接里带了就尊重它（少报会限制 Brutal 吞吐）
            sb.Append("bandwidth:\n  up: ").Append(Q(Bw(n.up, "100"))).Append("\n  down: ").Append(Q(Bw(n.down, "200"))).Append('\n');
            sb.Append("socks5:\n  listen: 127.0.0.1:10808\n");
            sb.Append("http:\n  listen: 127.0.0.1:10809\n");
            if (bypassCn)
            {
                // hysteria ACL：逐条匹配，无匹配条目的流量默认走代理（client 出站）。
                // 顺序重要：私网先拒（防本机局域网请求被代理劫持），大陆直连，其余默认代理。
                sb.Append("acl:\n");
                sb.Append("  - reject(geoip:private)\n");
                sb.Append("  - direct(geoip:cn)\n");
                sb.Append("  - direct(geosite:cn)\n");
            }
            return sb.ToString();
        }

        // 带宽值：链接里可能只给数字（"100"），hysteria2 需要带单位，补 mbps
        private static string Bw(string v, string dflt)
        {
            if (string.IsNullOrEmpty(v)) return dflt + " mbps";
            v = v.Trim();
            foreach (char c in v) if (!char.IsDigit(c) && c != '.') return v;   // 已带单位
            return v + " mbps";
        }

        // YAML 双引号标量：转义反斜杠与引号，去掉控制字符（换行塞进配置只会让内核解析失败）
        private static string Q(string v)
        {
            var sb = new StringBuilder("\"");
            foreach (char c in v ?? "")
            {
                if (c == '\\' || c == '"') sb.Append('\\').Append(c);
                else if (c < 0x20) sb.Append(' ');
                else sb.Append(c);
            }
            return sb.Append('"').ToString();
        }

        // 内核输出的最近若干行：拉起失败时要把内核自己说的那句话报给用户，
        // 而不是笼统的"连接失败"（hy2 的配置错误/认证失败全在这一行里）。
        private static readonly object _logLock = new object();
        private static readonly List<string> _recent = new List<string>();

        private static void PushLog(string line)
        {
            lock (_logLock)
            {
                _recent.Add(line);
                if (_recent.Count > 40) _recent.RemoveAt(0);
            }
        }

        public static string LastLogLine()
        {
            lock (_logLock)
            {
                for (int i = _recent.Count - 1; i >= 0; i--)
                {
                    string l = _recent[i];
                    if (l.IndexOf("FATAL", StringComparison.OrdinalIgnoreCase) >= 0
                        || l.IndexOf("error", StringComparison.OrdinalIgnoreCase) >= 0
                        || l.IndexOf("failed", StringComparison.OrdinalIgnoreCase) >= 0)
                        return l;
                }
                return _recent.Count > 0 ? _recent[_recent.Count - 1] : "";
            }
        }

        // 等内核真正可用：端口能连上才算"连上"。
        // hy2 是先握手成功才监听 http 入站，所以这一步同时验证了认证/obfs/TLS —— 之前的实现
        // 只看 Process.Start 没抛异常就宣布"已连接"，内核秒退时用户看到的是"已连接但没网"。
        public static bool WaitReady(Process p, int port, int timeoutMs, out string err)
        {
            err = "";
            var sw = System.Diagnostics.Stopwatch.StartNew();
            while (sw.ElapsedMilliseconds < timeoutMs)
            {
                if (p != null)
                {
                    try { if (p.HasExited) { err = LastLogLine(); return false; } }
                    catch { }
                }
                try
                {
                    using (var c = new System.Net.Sockets.TcpClient())
                    {
                        var ar = c.BeginConnect("127.0.0.1", port, null, null);
                        if (ar.AsyncWaitHandle.WaitOne(400) && c.Connected)
                        {
                            c.EndConnect(ar);
                            // 端口通了也可能是"上一次的僵尸内核还占着"，本进程其实已经 FATAL 退出前的
                            // 绑定失败状态；看到绑定失败就按失败处理，别把它当成本次连接成功。
                            string last = LastLogLine();
                            if (last.IndexOf("already in use", StringComparison.OrdinalIgnoreCase) >= 0
                                || last.IndexOf("address in use", StringComparison.OrdinalIgnoreCase) >= 0)
                            { err = last; return false; }
                            return true;
                        }
                    }
                }
                catch { }
                System.Threading.Thread.Sleep(150);
            }
            err = LastLogLine();
            return false;
        }

        // 把内核那行日志压成一行短提示（完整内容仍在日志文件里）
        internal static string Short(string line)
        {
            if (string.IsNullOrEmpty(line)) return "内核没有响应";
            string s = line.Replace('\t', ' ').Trim();
            int i = s.IndexOf("\"error\":");
            if (i >= 0) s = s.Substring(i + 8).Trim();
            s = s.Trim('{', '}', ' ', '"');
            if (s.Length > 120) s = s.Substring(0, 120) + "…";
            return s;
        }

        // 内核报错翻成人话：hysteria2 的原话（authentication error, HTTP status code: 301）对
        // 用户毫无意义，得说清楚是哪一环不对、该去改什么。
        internal static string Friendly(string raw)
        {
            string s = (raw ?? "").ToLowerInvariant();
            if (s.Length == 0) return "内核没有响应（端口未打开）";
            if (s.Contains("authentication error") || s.Contains("auth failed"))
                return "认证失败，检查密码与 obfs 密码是否与服务器一致";
            if (s.Contains("psk must be") || s.Contains("obfs"))
                return "obfs 密码不对（salamander 密码需 ≥4 字节且与服务端一致）";
            if (s.Contains("already in use") || s.Contains("address in use"))
                return "本地端口 10808/10809 被占用（可能上一次的内核还在跑）";
            if (s.Contains("x509") || s.Contains("certificate") || s.Contains("tls:"))
                return "TLS 证书校验失败，检查 sni/pinSHA256，或在链接里加 insecure=1";
            if (s.Contains("no such host") || s.Contains("i/o timeout") || s.Contains("connection refused")
                || s.Contains("dial tcp") || s.Contains("timeout"))
                return "连不上服务器（地址/端口不通，或被墙）";
            if (s.Contains("invalid config") || s.Contains("failed to parse"))
                return "内核拒绝配置：" + Short(raw);
            return Short(raw);
        }

        public static Process Start(string configJson, string proto)
        {
            string exe, args, cfgPath;
            if (proto == "hy2")
            {
                exe = Path.Combine(BinDir, "hysteria.exe");
                cfgPath = Path.Combine(BinDir, "xpanel_hy2.yaml");
                File.WriteAllText(cfgPath, configJson, new UTF8Encoding(false));
                args = "-c \"" + cfgPath + "\"";
            }
            else
            {
                exe = Path.Combine(BinDir, "xray.exe");
                cfgPath = Path.Combine(BinDir, "xpanel_client.json");
                File.WriteAllText(cfgPath, configJson, new UTF8Encoding(false));
                args = "run -c \"" + cfgPath + "\"";
            }
            var psi = new ProcessStartInfo
            {
                FileName = exe,
                Arguments = args,
                UseShellExecute = false,
                CreateNoWindow = true,
                WindowStyle = ProcessWindowStyle.Hidden,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                WorkingDirectory = BinDir
            };
            Process p = Process.Start(psi);
            string tag = proto == "hy2" ? "[HY2]" : "[XRAY]";
            // 不读流会撑爆管道；读了又不要阻塞 UI 线程，开后台任务持续读
            if (p != null)
            {
                _ = Task.Run(() =>
                {
                    try
                    {
                        using (var r = p.StandardOutput)
                        {
                            string line;
                            while ((line = r.ReadLine()) != null)
                            {
                                PushLog(line);
                                MainForm.LogToFile(tag + " " + line);
                            }
                        }
                    }
                    catch { }
                });
                _ = Task.Run(() =>
                {
                    try
                    {
                        using (var r = p.StandardError)
                        {
                            string line;
                            while ((line = r.ReadLine()) != null)
                            {
                                PushLog(line);
                                MainForm.LogToFile(tag + "-ERR " + line);
                            }
                        }
                    }
                    catch { }
                });
            }
            return p;
        }

        public static void Stop(Process p)
        {
            try
            {
                if (p != null && !p.HasExited)
                {
                    p.Kill();
                    p.WaitForExit(3000);
                }
            }
            catch { }
        }
    }
}
