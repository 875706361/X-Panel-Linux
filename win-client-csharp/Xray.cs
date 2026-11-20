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
        public static string BuildHy2Config(NodeInfo n, bool bypassCn)
        {
            var sb = new StringBuilder();
            sb.Append("server: ").Append(n.address).Append(':').Append(n.port).Append('\n');
            sb.Append("auth: ").Append(n.uuid).Append('\n');
            sb.Append("tls:\n  sni: ").Append(string.IsNullOrEmpty(n.sni) ? "bing.com" : n.sni).Append('\n');
            if (n.insecure) sb.Append("  insecure: true\n");
            if (!string.IsNullOrEmpty(n.pbk)) sb.Append("  pinSHA256: ").Append(n.pbk).Append('\n');
            if (!string.IsNullOrEmpty(n.type))   // obfs=salamander 时 LinkParser 把 obfs-password 放 type
                sb.Append("obfs:\n  type: salamander\n  password: ").Append(n.type).Append('\n');
            sb.Append("bandwidth:\n  up: 100 mbps\n  down: 200 mbps\n");
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

        // 拉起内核：proto=hy2 用 hysteria.exe -c，其余 xray.exe run -c
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
                                MainForm.LogToFile(tag + " " + line);
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
                                MainForm.LogToFile("[XRAY-ERR] " + line);
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
