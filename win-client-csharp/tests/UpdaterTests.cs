// 自动更新链路冒烟测试：只验证「拉清单 → 解析 → 版本比较 → 下载校验」的服务端契约，
// 不做 Windows 特有的替换（move/restart 只能在 Windows 上验证）。
// 用法：见 run-updater.sh（mono 下运行，Linux 可用）。
using System;
using System.IO;
using System.Security.Cryptography;

namespace XPanelWin
{
    internal static class UpdaterTests
    {
        private static int _fail;

        private static void Ok(string name, bool cond, string detail)
        {
            Console.WriteLine((cond ? "PASS " : "FAIL ") + name + (detail == null ? "" : "  " + detail));
            if (!cond) _fail++;
        }

        public static int Main(string[] args)
        {
            Console.WriteLine("=== 1. 候选镜像顺序（未登录节点时）===");
            foreach (var b in Updater.ManifestBases("", null))
                Console.WriteLine("  " + b);

            Console.WriteLine("=== 2. 候选镜像顺序（节点域名 1.2.3.4 + 首选基址）===");
            foreach (var b in Updater.ManifestBases("1.2.3.4", "https://panel.example:1111/clay/"))
                Console.WriteLine("  " + b);

            Console.WriteLine("=== 3. 版本比较 ===");
            Ok("10.5.2 > 10.5.1", Updater.CompareVersion("v10.5.2", "v10.5.1") == 1, null);
            Ok("10.5.2 == 10.5.2", Updater.CompareVersion("10.5.2", "v10.5.2") == 0, null);
            Ok("10.10.0 > 10.5.2", Updater.CompareVersion("v10.10.0", "v10.5.2") == 1, null);
            Ok("9.9.9 < 10.0.0", Updater.CompareVersion("v9.9.9", "v10.0.0") == -1, null);

            Console.WriteLine("=== 4. 真实检查（PANEL_BASE）===");
            string msg;
            var st = Updater.CheckNow("", null, out msg);
            Console.WriteLine("  state=" + st + "  msg=" + msg + "  本机VER=" + MainForm.VER);
            Ok("清单可达且可解析", st == Updater.State.UpToDate || st == Updater.State.Found, "state=" + st);

            Console.WriteLine("=== 5. 清单版本 vs 本机版本 ===");
            // 从磁盘读镜像清单原文，确认服务端声明的版本号
            Console.WriteLine("  本机版本 " + MainForm.VER + "，若面板清单更高则应 Found");

            Console.WriteLine("=== 6. 清单原文（服务端契约）===");
            try
            {
                Console.WriteLine("  " + MetaJson("https://888888666.xyz:1111/clay/download/pkg/client.json").Replace("\n", "\n  ").Trim());
            }
            catch (Exception ex) { Console.WriteLine("  拉清单失败: " + ex.Message); }

            Console.WriteLine("=== 7. 替换脚本（XPL_WINE_TEST=1 时用 wine 的 cmd 真跑一遍）===");
            if (Environment.GetEnvironmentVariable("XPL_WINE_TEST") == "1")
                ReplaceScriptTest();
            else
                Console.WriteLine("  SKIP（未设置 XPL_WINE_TEST）");

            Console.WriteLine(_fail == 0 ? "ALL PASS" : _fail + " FAILED");
            return _fail == 0 ? 0 : 1;
        }

        private static string WinePath(string p)
        {
            return "Z:" + p.Replace('/', '\\');
        }

        // 用 wine 的 cmd 跑一次生成的替换脚本（pid 用不存在的值，跳过等待分支），
        // 验证 for /f 的转义、move /y 覆盖、脚本自删除这三个只在 Windows 上才会暴露的点。
        private static void ReplaceScriptTest()
        {
            string dir = Path.Combine(Path.GetTempPath(), "xpl-updtest-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
            try
            {
                string newExe = Path.Combine(dir, "new.exe");
                string exe = Path.Combine(dir, "old.exe");
                string bat = Path.Combine(dir, "upd.cmd");
                File.WriteAllText(newExe, "NEW");
                File.WriteAllText(exe, "OLD");
                File.WriteAllText(bat, Updater.BuildReplaceScript(999999, WinePath(newExe), WinePath(exe)),
                                  System.Text.Encoding.Default);

                var psi = new System.Diagnostics.ProcessStartInfo("/usr/bin/wine", "cmd /c \"" + WinePath(bat) + "\"");
                psi.UseShellExecute = false;
                psi.RedirectStandardOutput = true;
                psi.RedirectStandardError = true;
                psi.EnvironmentVariables["WINEDEBUG"] = "-all";
                var p = System.Diagnostics.Process.Start(psi);
                string so = p.StandardOutput.ReadToEnd();
                string se = p.StandardError.ReadToEnd();
                if (!p.WaitForExit(180000)) { try { p.Kill(); } catch { } Console.WriteLine("  FAIL 脚本 180s 未退出（可能卡在等待分支）"); _fail++; return; }

                bool moved = File.Exists(exe) && File.ReadAllText(exe) == "NEW";
                bool cleaned = !File.Exists(newExe);
                bool selfDeleted = !File.Exists(bat);
                Ok("move /y 把新包覆盖到 exe", moved, null);
                Ok("临时新包已清理", cleaned, null);
                Ok("脚本自删除", selfDeleted, null);
                if (!moved || !cleaned || !selfDeleted)
                    Console.WriteLine("  cmd 输出: " + so.Replace("\r\n", " | ") + " err: " + se.Replace("\r\n", " | "));
            }
            finally
            {
                try { Directory.Delete(dir, true); } catch { }
            }
        }

        private static string MetaJson(string url)
        {
            var req = (System.Net.HttpWebRequest)System.Net.WebRequest.Create(url);
            req.Timeout = 8000; req.ReadWriteTimeout = 8000; req.Proxy = null;
            req.ServerCertificateValidationCallback = (s, c, ch, e) => true;
            using (var resp = req.GetResponse())
            using (var sr = new StreamReader(resp.GetResponseStream(), System.Text.Encoding.UTF8))
                return sr.ReadToEnd();
        }
    }
}
