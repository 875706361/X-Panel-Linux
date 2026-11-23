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

        private const int META_TIMEOUT_MS = 4000;

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
                string s = b.TrimEnd(new char[] { '/' });
                if (!list.Contains(s)) list.Add(s);
            };
            add(preferredBase);
            if (!string.IsNullOrEmpty(host))
            {
                // 443 优先：面板的 1111 端口在国内经常被干扰/丢包，节点地址的 443 由 nginx
                // 反代到同一个面板，国内可直连。1111 留在后面兜底（部分中转只开 1111）。
                add("https://" + host + "/clay");
                add("https://" + host);
                add("http://" + host + "/clay");
                add("https://" + host + ":1111/clay");
                add("https://" + host + ":1111");
                add("http://" + host + ":1111/clay");
            }
            // 面板自己的域名：443（证书正常）→ 80 → 1111。很多用户能打开面板的 443
            // 下载页、却卡在 1111 的更新检查上，就是因为候选里没有域名 443 这一项。
            string panelHost = "";
            try { panelHost = new Uri(MainForm.PANEL_BASE).Host; } catch { }
            if (!string.IsNullOrEmpty(panelHost))
            {
                add("https://" + panelHost + "/clay");
                add("http://" + panelHost + "/clay");
            }
            add(MainForm.PANEL_BASE);
            // 域名被污染/解析失败时的最后兜底：直连面板 IP。
            add("https://23.144.20.121/clay");
            add("http://23.144.20.121/clay");
            add("https://23.144.20.121:1111/clay");
            return list.ToArray();
        }

        // 启动时调用：先立即检查一次（发现即装），之后周期性复查只看不装。
        // 「刚打开」判定：进程起来后 STARTUP_WINDOW 内都算启动期，这期间发现新版直接安装
        // （用户在等它自己升级）；超过这个窗口只提示，免得正在用的时候被突然重启。
        // 开机的头两分钟内网络/面板常还没就绪，检查失败要快速补查几次，
        // 否则一次连不上就退到 30 分钟节奏，用户会以为「自动更新根本没生效」。
        // 用户明确要求「每次打开先检查更新」：窗口放宽到 10 分钟、重试更密，
        // 这样开机网络晚就绪也一定能在本次启动内装上。
        private static readonly TimeSpan STARTUP_WINDOW = TimeSpan.FromMinutes(10);
        private static readonly int[] STARTUP_RETRY_MS = { 3000, 6000, 10000, 20000, 40000, 60000 };

        public static void StartAutoLoop(string preferredBase, Func<string> hostProvider)
        {
            Thread.Sleep(500);   // 让窗口先画出来，别和首帧抢启动时间
            // 打开即给出「在检查」的可见状态：用户要求每次启动先查更新，静默就等于没查。
            try { MainForm.SetUpdateHint("正在检查更新…"); } catch { }
            var clock = Stopwatch.StartNew();
            int round = 0, retry = 0;
            while (true)
            {
                string st;
                try
                {
                    string host = hostProvider == null ? "" : hostProvider();
                    var bases = ManifestBases(host, preferredBase);
                    // 每轮开始都刷新一次提示：让用户看到「确实在查」，失败后紧接着显示原因。
                    MainForm.SetUpdateHint("正在检查更新…");
                    bool startup = clock.Elapsed < STARTUP_WINDOW;
                    bool connected = MainForm.Instance != null && MainForm.Instance.IsConnected;
                    // 启动期发现即装；运行期只有未连接时才自动装（正在用代理时不打断）。
                    bool install = startup || !connected;
                    var state = Check(bases, install, out string msg, connected);
                    st = state.ToString();
                    MainForm.LogToFile("[UPDATE] 第" + (round + 1) + "次检查(" +
                                       (startup ? "启动期" : "运行期") + "): " + st + " " + msg);
                    if (startup) ReportStartupOutcome(state, msg);
                    // 运行期发现了但不装（用户正在连接）时，必须把提示留在界面上，
                    // 否则「自动更新」对用户又变成完全静默。
                    else if (state == State.Found)
                        MainForm.SetUpdateHint("发现新版本 " + LatestVersion + "，断开连接后自动更新");
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

        // 启动期结果反馈：自动更新一直是「静默」的——连不上和已最新对用户完全一样，
        // 都表现为「打开软件什么都没发生」。这里把启动期结论写到导入页提示行：
        // 失败保留在界面（带日志路径，用户能直接反馈），已最新 6 秒后清掉。
        private static void ReportStartupOutcome(State state, string msg)
        {
            try
            {
                if (state == State.Failed)
                {
                    string shortMsg = msg.Length > 44 ? msg.Substring(0, 44) + "…" : msg;
                    MainForm.SetUpdateHint("自动更新检查失败：" + shortMsg + "，稍后自动重试");
                    return;
                }
                if (state == State.UpToDate)
                {
                    MainForm.SetUpdateHint("客户端已是最新 " + MainForm.VER);
                    ThreadPool.QueueUserWorkItem(_ =>
                    {
                        Thread.Sleep(6000);
                        try { MainForm.SetUpdateHint(""); } catch { }
                    });
                }
            }
            catch { }
        }

        // 手动「检查更新」：只查不装，把结果交回界面。
        public static State CheckNow(string preferredBase, Func<string> hostProvider, out string msg)
        {
            string host = hostProvider == null ? "" : hostProvider();
            bool connected = MainForm.Instance != null && MainForm.Instance.IsConnected;
            return Check(ManifestBases(host, preferredBase), false, out msg, connected);
        }

        // 手动确认后安装已发现的版本。
        public static State Install(string preferredBase, Func<string> hostProvider, out string msg)
        {
            string host = hostProvider == null ? "" : hostProvider();
            bool connected = MainForm.Instance != null && MainForm.Instance.IsConnected;
            return Check(ManifestBases(host, preferredBase), true, out msg, connected);
        }

        private static State Check(string[] bases, bool install, out string msg, bool online = false)
        {
            msg = "";
            if (Interlocked.Exchange(ref _busy, 1) == 1) { msg = "正在检查中"; return State.Checking; }
            try
            {
                Manifest m = null;
                string hitBase = null;
                bool viaProxy = false;
                string lastErr = "";
                foreach (var b in bases)
                {
                    try
                    {
                        string json = HttpGet(b + "/download/pkg/client.json", META_TIMEOUT_MS, false);
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
                // 直连全失败且本地代理在跑（已连接）：再走 127.0.0.1:10809 试一轮。
                // 面板 1111 在国内常被干扰，但用户连上节点后本地代理是通的。
                if (m == null && online)
                {
                    foreach (var b in bases)
                    {
                        try
                        {
                            string json = HttpGet(b + "/download/pkg/client.json", META_TIMEOUT_MS, true);
                            if (string.IsNullOrEmpty(json)) continue;
                            var parsed = new JavaScriptSerializer().Deserialize<Manifest>(json);
                            if (parsed != null && parsed.windows != null && !string.IsNullOrEmpty(parsed.windows.sha256))
                            {
                                m = parsed; hitBase = b; viaProxy = true; break;
                            }
                        }
                        catch { }
                    }
                    if (m != null) lastErr += "（经本地代理）";
                }

                LastError = "";
                if (m == null)
                {
                    LastError = lastErr;
                    msg = "面板不可达（" + lastErr + "）";
                    return State.Failed;
                }
                // 命中哪个地址必须留痕：用户说「一直检查更新」时，靠这行判断是走的 443、80 还是 1111。
                MainForm.LogToFile("[UPDATE] 清单命中 " + hitBase + (viaProxy ? "（经本地代理）" : ""));
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
                Download(hitBase + "/download/pkg/XPanel.exe", newExe, 300000, viaProxy);

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
                string script = BuildReplaceScript(pid, newExe, exe);
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

        // 生成「等本进程退出 → 覆盖 exe → 重启」的替换脚本。抽成独立方法是为了能在测试里
        // 单独喂给 cmd 跑一遍（for /f 的引号与 ^ 转义很容易写错，错了只会在用户机器上表现为
        // 「更新失败/卡住」，本地编译期完全看不出来）。
        internal static string BuildReplaceScript(int pid, string newExe, string exe)
        {
            string oldName = "XPanel.old.exe";
            return
                "@echo off\r\n" +
                "setlocal enabledelayedexpansion\r\n" +
                "set N=0\r\n" +
                ":wait\r\n" +
                // 不再用 PID 判断存活：PID 会被系统复用，tasklist 也可能在进程刚退出时仍列出。
                // 直接尝试把 exe 改名——成功就说明旧进程已释放文件锁，失败就再等。
                "del \"" + oldName + "\" >nul 2>&1\r\n" +
                "ren \"" + exe + "\" \"" + oldName + "\" >nul 2>&1\r\n" +
                "if exist \"" + exe + "\" (\r\n" +
                "  set /a N+=1\r\n" +
                "  if !N! GEQ 30 goto fail\r\n" +
                "  ping -n 2 127.0.0.1 >nul\r\n" +
                "  goto wait\r\n" +
                ")\r\n" +
                "move /y \"" + newExe + "\" \"" + exe + "\" >nul\r\n" +
                "if errorlevel 1 goto fail\r\n" +
                "del \"" + oldName + "\" >nul 2>&1\r\n" +
                "start \"\" \"" + exe + "\"\r\n" +
                "goto done\r\n" +
                ":fail\r\n" +
                // 失败留痕：客户端下次启动会读这个文件并把原因写进日志/提示行。
                "echo update failed %DATE% %TIME% >> \"%TEMP%\\xpanel-update-error.log\"\r\n" +
                "copy /y \"" + newExe + "\" \"%USERPROFILE%\\Desktop\\XPanel.new.exe\" >nul 2>&1\r\n" +
                ":done\r\n" +
                "del \"%~f0\"\r\n";
        }

        private static string HttpGet(string url, int timeoutMs, bool viaProxy)
        {
            HttpWebRequest req = null;
            try
            {
                req = (HttpWebRequest)WebRequest.Create(url);
                req.Method = "GET";
                req.Timeout = timeoutMs;
                req.ReadWriteTimeout = timeoutMs;
                // viaProxy：已连接时走客户端自己的 http 入站（1111 被干扰时的唯一活路）。
                req.Proxy = viaProxy ? new WebProxy("http://127.0.0.1:10809") : null;
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

        private static void Download(string url, string path, int timeoutMs, bool viaProxy)
        {
            HttpWebRequest req = null;
            try
            {
                req = (HttpWebRequest)WebRequest.Create(url);
                req.Method = "GET";
                req.Timeout = timeoutMs;
                req.ReadWriteTimeout = timeoutMs;
                req.Proxy = viaProxy ? new WebProxy("http://127.0.0.1:10809") : null;
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
