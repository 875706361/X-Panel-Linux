// X-Panel Windows 客户端 v9.0 — 入口
// 单文件 EXE：内嵌 Xray 内核（xray.exe/geoip.dat/geosite.dat），启动时释放到 exe 同目录。
using System;
using System.Windows.Forms;

namespace XPanelWin
{
    internal static class Program
    {
        private const string MUTEX_NAME = "XPanelWin_SingleInstance";
        private const string SHOW_EVENT = "XPanelWin_ShowWindow";

        [STAThread]
        private static void Main()
        {
            bool createdNew;
            var mutex = new System.Threading.Mutex(true, MUTEX_NAME, out createdNew);
            if (!createdNew)
            {
                // 已经有实例在跑：不能像老版本那样静默退出。
                // 老版本是「点叉隐藏到托盘」+「第二个实例直接 return」的组合，
                // 用户以为关了、再点图标却没任何反应，只能去任务管理器杀进程。
                // 这里改成：让在跑的实例把窗口叫到前台，自己再退出。
                if (TryActivateExisting()) return;
                // 叫不回来（对方是旧版本僵尸进程，或者当前系统不支持跨进程事件）：
                // 宁可多开一个实例，也不能让用户点了图标一片安静。
                MainForm.LogToFile("[INIT] 已有实例但无法召回窗口，本次按新实例启动");
            }
            try
            {
                // 命名事件在某些非 Windows 环境建不出来，失败就只是不能「叫回窗口」，
                // 不能因此让客户端起不来。
                System.Threading.EventWaitHandle ev = null;
                try
                {
                    ev = new System.Threading.EventWaitHandle(false,
                        System.Threading.EventResetMode.AutoReset, SHOW_EVENT);
                }
                catch { ev = null; }

                if (ev != null)
                {
                    var th = new System.Threading.Thread(() =>
                    {
                        while (true)
                        {
                            try { ev.WaitOne(); MainForm.ActivateMain(); }
                            catch { return; }
                        }
                    });
                    th.IsBackground = true;
                    th.Start();
                }
                Run();
                if (ev != null) { try { ev.Dispose(); } catch { } }
            }
            finally { if (createdNew) { try { mutex.ReleaseMutex(); } catch { } } }
        }

        // 唤起已有实例；成功返回 true。
        private static bool TryActivateExisting()
        {
            // 1) 新版客户端注册了命名事件：让对方自己 Show 出来（走正常的 WinForms 显示流程）。
            try
            {
                using (var ev = System.Threading.EventWaitHandle.OpenExisting(SHOW_EVENT))
                {
                    ev.Set();
                    System.Threading.Thread.Sleep(200);
                }
                return true;
            }
            catch { }

            // 2) 对方是旧版本（没注册事件）或者事件没建起来：直接找窗口，
            //    原生 ShowWindow 能对付「隐藏到托盘」的僵尸进程。
            //    只认 WinForms 窗口，免得撞上别的同名程序被误判成「已经在跑」。
            try
            {
                IntPtr h = FindWindowW(null, WINDOW_TITLE);
                if (h != IntPtr.Zero && IsWinFormsWindow(h))
                {
                    ShowWindow(h, SW_RESTORE);
                    SetForegroundWindow(h);
                    System.Threading.Thread.Sleep(200);
                    return true;
                }
            }
            catch { }

            return false;
        }

        private static bool IsWinFormsWindow(IntPtr h)
        {
            try
            {
                var sb = new System.Text.StringBuilder(256);
                GetClassNameW(h, sb, sb.Capacity);
                return sb.ToString().StartsWith("WindowsForms10", StringComparison.OrdinalIgnoreCase);
            }
            catch { return false; }
        }

        private const string WINDOW_TITLE = "客户端";
        private const int SW_RESTORE = 9;

        [System.Runtime.InteropServices.DllImport("user32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
        private static extern IntPtr FindWindowW(string cls, string title);
        [System.Runtime.InteropServices.DllImport("user32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
        private static extern int GetClassNameW(IntPtr hWnd, System.Text.StringBuilder sb, int max);
        [System.Runtime.InteropServices.DllImport("user32.dll")]
        private static extern bool ShowWindow(IntPtr hWnd, int cmd);
        [System.Runtime.InteropServices.DllImport("user32.dll")]
        private static extern bool SetForegroundWindow(IntPtr hWnd);

        private static void Run() {
            try {
                System.Net.ServicePointManager.ServerCertificateValidationCallback = delegate { return true; };
                System.Net.ServicePointManager.SecurityProtocol = System.Net.SecurityProtocolType.Tls12 | (System.Net.SecurityProtocolType)3072;
            } catch { }
            try { Native.SetProcessDPIAware(); } catch { }
            MainForm.DpiScale = 1f;
            try
            {
                using (var g = System.Drawing.Graphics.FromHwnd(IntPtr.Zero))
                {
                    MainForm.DpiScale = g.DpiX / 96f;
                }
            }
            catch { }
            // 诊断开关：XPL_DPI=144 可强制按 125% 栅格渲染，用来在没有高分屏的机器上
            // 复现「缩放后文字挤在一起 / 半截字」这类问题（正式用户不会设置它）。
            try
            {
                string ov = Environment.GetEnvironmentVariable("XPL_DPI");
                int forced;
                if (!string.IsNullOrEmpty(ov) && int.TryParse(ov, out forced) && forced >= 96 && forced <= 480)
                    MainForm.DpiScale = forced / 96f;
            }
            catch { }

            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            Application.ThreadException += (s, e) =>
                MainForm.LogToFile("[UI-ERR] " + e.Exception.Message);
            AppDomain.CurrentDomain.UnhandledException += (s, e) =>
                MainForm.LogToFile("[FATAL] " + (e.ExceptionObject ?? ""));

            try { Xray.EnsureFiles(); }
            catch (Exception ex) { MainForm.LogToFile("[XRAY] 内核释放失败: " + ex.Message); }

            Application.Run(new MainForm());
        }
    }
}
