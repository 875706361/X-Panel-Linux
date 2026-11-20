// X-Panel Windows 客户端 v9.0 — 入口
// 单文件 EXE：内嵌 Xray 内核（xray.exe/geoip.dat/geosite.dat），启动时释放到 exe 同目录。
using System;
using System.Windows.Forms;

namespace XPanelWin
{
    internal static class Program
    {
        [STAThread]
        private static void Main()
        {
            bool createdNew;
            using (var mutex = new System.Threading.Mutex(true, "XPanelWin_SingleInstance", out createdNew))
            {
                if (!createdNew) return;   // 已有实例在跑，直接退出（防批量打开）
                try
                {
                    Run();
                }
                finally { mutex.ReleaseMutex(); }
            }
        }

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
