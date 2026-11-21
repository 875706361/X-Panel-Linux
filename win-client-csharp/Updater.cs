// 客户端自动更新：向面板镜像的 /download/pkg/client.json 取版本与哈希，
// 比本机新则下载、校验、由 cmd 脚本替换并重启。
//
// v10.4.6 起的行为：
//   · 启动后自动检查一次，发现新版直接装（保持无人值守）；
//   · 之后每 30 分钟复查一次，只记录/提示，不打断使用（避免开着一整天永远不更新）；
//   · 设置页「检查更新」手动触发，发现新版弹确认再装。
//   · 拉清单和下载都带硬超时（HttpWebRequest.Timeout 管不住 DNS/TCP 阶段），
//     mirror 地址按「节点域名 → 上次命中 → 内置面板」顺序试，证书一律不校验
//     （面板常用自签/裸 IP，校验失败会让所有候选都超时，跟流量轮询同一策略）。
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Web.Script.Serialization;

namespace XPanelWin
{
    internal static class Updater
    {
        internal enum State { Idle, Checking, UpToDate, Found, Downloading, Failed }

        private const int META_TIMEOUT_MS = 8000;

        private static int _busy;                 // 同一时刻只跑一次检查
        internal static volatile string LatestVersion = "";   // 清单里比本机新的版本号
        internal static volatile string LastError = "";

        private class ClientFile
        {
            public string file;
            public string sha256;
            public string version;
            public long size;
        }

        private class Manifest
        {
            public ClientFile windows;
        }

        // 候选镜像前缀：节点域名常与面板域名不同（中转/CDN/反代），逐个试。
        internal static string[] ManifestBases(string host, string preferredBase)
        {
            var list = new List<string>();
            Action<string> add = b =>
            {
                if (string.IsNullOrEmpty(b)) return;
                string s = b.TrimEnd('/');
                if (!list.Contains(s)) list.Add(s);
            };
            add(preferredBase);
            if (!string.IsNullOrEmpty(host))
            {
                add("https://" + host + ":1111/clay");
                add("https://" + host + ":1111");
                add("https://" + host + "/clay");
                add("https://" + host);
                add("http://" + host + ":1111/clay");
            }
            add(MainForm.PANEL_BASE);
            return list.ToArray();
        }

        // 启动时调用：先立即检查一次（发现即装），之后周期性复查只看不装。
        // 「刚打开」判定：进程起来后 STARTUP_WINDOW 内都算启动期，这期间发现新版直接安装
        // （用户在等它自己升级）；超过这个窗口只提示，免得正在用的时候被突然重启。
        // 开机的头两分钟内网络/面板常还没就绪，检查失败要快速补查几次，
        // 否则一次连不上就退到 30 分钟节奏，用户会以为「自动更新根本没生效」。
        private static readonly TimeSpan STARTUP_WINDOW = TimeSpan.FromMinutes(3);
        private static readonly int[] STARTUP_RETRY_MS = { 6000, 15000, 35000, 90000 };

        public static void StartAutoLoop(string preferredBase, Func<string> hostProvider)
        {
            Thread.Sleep(1500);   // 让窗口先画出来，别和首帧抢启动时间
            var clock = Stopwatch.StartNew();
            int round = 0, retry = 0;
            while (true)
            {
                string st;
                try
                {
                    string host = hostProvider == null ? "" : hostProvider();
                    var bases = ManifestBases(host, preferredBase);
                    bool startup = clock.Elapsed < STARTUP_WINDOW;
                    var state = Check(bases, startup, out string msg);
                    st = state.ToString();
                    MainForm.LogToFile("[UPDATE] 第" + (round + 1) + "次检查(" +
                                       (startup ? "启动期" : "运行期") + "): " + st + " " + msg);
                    if (state != State.Failed) retry = 0;
                    else if (retry < STARTUP_RETRY_MS.Length - 1) retry++;
                }
                catch (Exception ex)
                {
                    st = "Exception";
                    MainForm.LogToFile("[UPDATE] 第" + (round + 1) + "次检查异常: " + ex.Message);
                }
                round++;
                int wait = st == "Failed"
                    ? STARTUP_RETRY_MS[Math.Min(retry, STARTUP_RETRY_MS.Length - 1)]
                    : 30 * 60 * 1000;
                Thread.Sleep(wait);
            }
        }

        // 手动「检查更新」：只查不装，把结果交回界面。
        public static State CheckNow(string preferredBase, Func<string> hostProvider, out string msg)
        {
            string host = hostProvider == null ? "" : hostProvider();
            return Check(ManifestBases(host, preferredBase), false, out msg);
        }

        // 手动确认后安装已发现的版本。
        public static State Install(string preferredBase, Func<string> hostProvider, out string msg)
        {
            string host = hostProvider == null ? "" : hostProvider();
            return Check(ManifestBases(host, preferredBase), true, out msg);
        }

        private static State Check(string[] bases, bool install, out string msg)
        {
            msg = "";
            if (Interlocked.Exchange(ref _busy, 1) == 1) { msg = "正在检查中"; return State.Checking; }
            try
            {
                Manifest m = null;
                string hitBase = null;
                string lastErr = "";
                foreach (var b in bases)
                {
                    try
                    {
                        string json = HttpGet(b + "/download/pkg/client.json", META_TIMEOUT_MS);
                        if (string.IsNullOrEmpty(json)) { lastErr = b + " 空响应"; continue; }
                        var parsed = new JavaScriptSerializer().Deserialize<Manifest>(json);
                        if (parsed != null && parsed.windows != null && !string.IsNullOrEmpty(parsed.windows.sha256))
                        {
                            m = parsed; hitBase = b; break;
                        }
                        lastErr = b + " 清单无效";
                    }
                    catch (Exception ex) { lastErr = b + " " + ex.Message; }
                }

                LastError = "";
                if (m == null)
                {
                    LastError = lastErr;
                    msg = "面板不可达（" + lastErr + "）";
                    return State.Failed;
                }
                if (string.IsNullOrEmpty(m.windows.version))
                {
                    msg = "清单无版本号，跳过";
                    return State.UpToDate;    // 旧镜像：不按 sha 换包，避免降级
                }
                if (CompareVersion(m.windows.version, MainForm.VER) <= 0)
                {
                    LatestVersion = "";
                    msg = "已是最新 " + MainForm.VER;
                    return State.UpToDate;
                }

                LatestVersion = m.windows.version;
                if (!install)
                {
                    msg = "发现新版本 " + m.windows.version;
                    return State.Found;
                }

                string exe = System.Windows.Forms.Application.ExecutablePath;
                string selfSha = Sha256File(exe);
                if (string.Equals(selfSha, m.windows.sha256, StringComparison.OrdinalIgnoreCase))
                {
                    msg = "本机已是新包";
                    return State.UpToDate;
                }

                MainForm.LogToFile("[UPDATE] 发现 " + m.windows.version + "，从 " + hitBase + " 下载");
                MainForm.SetUpdateHint("发现新版本 " + m.windows.version + "，正在更新…");
                string newExe = Path.Combine(Path.GetTempPath(), "XPanel.new." + Guid.NewGuid().ToString("N") + ".exe");
                Download(hitBase + "/download/pkg/XPanel.exe", newExe, 300000);

                string got = Sha256File(newExe);
                if (!string.Equals(got, m.windows.sha256, StringComparison.OrdinalIgnoreCase))
                {
                    try { File.Delete(newExe); } catch { }
                    msg = "下载校验失败，已放弃";
                    MainForm.LogToFile("[UPDATE] 校验失败: " + got);
                    return State.Failed;
                }

                int pid = Process.GetCurrentProcess().Id;
                string bat = Path.Combine(Path.GetTempPath(), "xpanel-update-" + pid + ".cmd");
                string script =
                    "@echo off\r\n" +
                    "setlocal\r\n" +
                    ":wait\r\n" +
                    "tasklist /FI \"PID eq " + pid + "\" /NH 2>nul | findstr /C:\"" + pid + "\" >nul\r\n" +
                    "if not errorlevel 1 (\r\n" +
                    "  timeout /t 1 /nobreak >nul\r\n" +
                    "  goto wait\r\n" +
                    ")\r\n" +
                    "move /y \"" + newExe + "\" \"" + exe + "\" >nul\r\n" +
                    "if errorlevel 1 (\r\n" +
                    "  echo update failed > \"%TEMP%\\xpanel-update-error.log\"\r\n" +
                    "  del \"" + newExe + "\" >nul 2>&1\r\n" +
                    ") else (\r\n" +
                    "  start \"\" \"" + exe + "\"\r\n" +
                    ")\r\n" +
                    "del \"%~f0\"\r\n";
                File.WriteAllText(bat, script, Encoding.Default);

                MainForm.LogToFile("[UPDATE] 启动替换脚本并退出");
                Process.Start(new ProcessStartInfo("cmd.exe", "/c \"" + bat + "\"")
                {
                    CreateNoWindow = true,
                    UseShellExecute = false,
                    WindowStyle = ProcessWindowStyle.Hidden
                });
                msg = "正在更新到 " + m.windows.version;
                Environment.Exit(0);
                return State.Downloading;
            }
            finally { Interlocked.Exchange(ref _busy, 0); }
        }

        private static string HttpGet(string url, int timeoutMs)
        {
            HttpWebRequest req = null;
            try
            {
                req = (HttpWebRequest)WebRequest.Create(url);
                req.Method = "GET";
                req.Timeout = timeoutMs;
                req.ReadWriteTimeout = timeoutMs;
                req.Proxy = null;   // 直连：IE 里残留的系统代理会把面板请求绕到不存在的通道
                req.ServerCertificateValidationCallback = (s, cert, chain, errors) => true;
                var ar = req.BeginGetResponse(null, null);
                if (!ar.AsyncWaitHandle.WaitOne(timeoutMs)) throw new TimeoutException("请求超时");
                using (var resp = req.EndGetResponse(ar))
                using (var sr = new StreamReader(resp.GetResponseStream(), Encoding.UTF8))
                    return sr.ReadToEnd();
            }
            catch
            {
                try { if (req != null) req.Abort(); } catch { }
                throw;
            }
        }

        private static void Download(string url, string path, int timeoutMs)
        {
            HttpWebRequest req = null;
            try
            {
                req = (HttpWebRequest)WebRequest.Create(url);
                req.Method = "GET";
                req.Timeout = timeoutMs;
                req.ReadWriteTimeout = timeoutMs;
                req.Proxy = null;
                req.ServerCertificateValidationCallback = (s, cert, chain, errors) => true;
                using (var resp = req.GetResponse())
                using (var input = resp.GetResponseStream())
                using (var output = File.Create(path))
                    input.CopyTo(output);
            }
            catch
            {
                try { if (req != null) req.Abort(); } catch { }
                throw;
            }
        }

        // 版本比较：v10.4.3 vs 10.10.0，缺失段按 0 处理。返回 -1/0/1。
        internal static int CompareVersion(string a, string b)
        {
            int[] x = VerParts(a), y = VerParts(b);
            for (int i = 0; i < 3; i++)
            {
                if (x[i] != y[i]) return x[i] < y[i] ? -1 : 1;
            }
            return 0;
        }

        private static int[] VerParts(string v)
        {
            var nums = new int[3];
            if (string.IsNullOrEmpty(v)) return nums;
            string[] parts = v.Trim().TrimStart('v', 'V').Split('.');
            for (int i = 0; i < 3 && i < parts.Length; i++)
                int.TryParse(parts[i], out nums[i]);
            return nums;
        }

        private static string Sha256File(string path)
        {
            using (var sha = SHA256.Create())
            using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
            {
                byte[] hash = sha.ComputeHash(fs);
                var sb = new StringBuilder(hash.Length * 2);
                foreach (byte b in hash) sb.Append(b.ToString("x2"));
                return sb.ToString();
            }
        }
    }
}
