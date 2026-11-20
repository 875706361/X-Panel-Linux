// 客户端自动更新：向面板镜像的 /download/pkg/client.json 取哈希，
// 与本机 exe 不一致则下载新版，退出后由 cmd 脚本替换并重启。
using System;
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
        private static int _started;

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

        // 只检查一次；baseUrl 例如 https://域名:1111/clay
        public static void CheckOnce(string baseUrl)
        {
            if (string.IsNullOrEmpty(baseUrl)) return;
            if (Interlocked.Exchange(ref _started, 1) == 1) return;
            try { Check(baseUrl); }
            catch (Exception ex) { MainForm.LogToFile("[UPDATE] " + ex.Message); }
        }

        private static void Check(string baseUrl)
        {
            string url = baseUrl.TrimEnd('/') + "/download/pkg/client.json";
            string json = HttpGet(url, 10000);
            if (string.IsNullOrEmpty(json)) return;

            var m = new JavaScriptSerializer().Deserialize<Manifest>(json);
            if (m == null || m.windows == null || string.IsNullOrEmpty(m.windows.sha256)) return;

            // 版本护栏：清单没写版本号（旧镜像）或版本不高于本机时都不换。
            // 只按 sha 比对会把已修好的新版 exe 换回镜像里的旧包（降级）。
            if (string.IsNullOrEmpty(m.windows.version))
            {
                MainForm.LogToFile("[UPDATE] client.json 无版本号，跳过自动更新");
                return;
            }
            if (CompareVersion(m.windows.version, MainForm.VER) <= 0) return;

            string exe = System.Windows.Forms.Application.ExecutablePath;
            string selfSha = Sha256File(exe);
            if (string.Equals(selfSha, m.windows.sha256, StringComparison.OrdinalIgnoreCase))
                return;

            MainForm.LogToFile("[UPDATE] 发现新版本，开始下载");
            string newExe = Path.Combine(Path.GetTempPath(), "XPanel.new." + Guid.NewGuid().ToString("N") + ".exe");
            Download(baseUrl.TrimEnd('/') + "/download/pkg/XPanel.exe", newExe, 300000);

            string got = Sha256File(newExe);
            if (!string.Equals(got, m.windows.sha256, StringComparison.OrdinalIgnoreCase))
            {
                try { File.Delete(newExe); } catch { }
                MainForm.LogToFile("[UPDATE] 校验失败，放弃更新");
                return;
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
            Environment.Exit(0);
        }

        private static string HttpGet(string url, int timeoutMs)
        {
            var req = (HttpWebRequest)WebRequest.Create(url);
            req.Timeout = timeoutMs;
            req.ReadWriteTimeout = timeoutMs;
            req.Method = "GET";
            req.ServerCertificateValidationCallback = (s, cert, chain, errors) => errors == System.Net.Security.SslPolicyErrors.None;
            using (var resp = req.GetResponse())
            using (var sr = new StreamReader(resp.GetResponseStream(), Encoding.UTF8))
                return sr.ReadToEnd();
        }

        private static void Download(string url, string path, int timeoutMs)
        {
            var req = (HttpWebRequest)WebRequest.Create(url);
            req.Timeout = timeoutMs;
            req.ReadWriteTimeout = timeoutMs;
            req.ServerCertificateValidationCallback = (s, cert, chain, errors) => errors == System.Net.Security.SslPolicyErrors.None;
            using (var resp = req.GetResponse())
            using (var input = resp.GetResponseStream())
            using (var output = File.Create(path))
                input.CopyTo(output);
        }

        // 版本比较：v10.4.3 vs 10.10.0，缺失段按 0 处理。返回 -1/0/1。
        private static int CompareVersion(string a, string b)
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
