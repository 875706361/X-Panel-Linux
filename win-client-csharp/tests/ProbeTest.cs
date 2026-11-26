using System;
namespace XPanelWin
{
    internal static class ProbeTest
    {
        static int Main(string[] args)
        {
            int rc = 0;
            string err;

            // 1) 节点 TCP RTT：不经本地入站，直接连目标端口（这才是"延迟"）
            try
            {
                int rtt = Probe.TcpRtt("www.gstatic.com", 443, 6000);
                Console.WriteLine("tcprtt=" + rtt + "ms");
                if (rtt <= 0) rc = 1;
            }
            catch (Exception e) { Console.WriteLine("tcprtt FAIL " + e.Message); rc = 1; }

            // 2) 经本地 http 入站 CONNECT + TLS + 204（需 10809 上有 CONNECT 代理）
            try
            {
                int[] g = Probe.Http("www.gstatic.com", "/generate_204", 6000, out err);
                Console.WriteLine("google code=" + g[0] + " conn=" + g[1] + "ms ttfb=" + g[2] + "ms err=" + (err ?? "-"));
                if (g[0] != 204) rc = 1;
            }
            catch (Exception e) { Console.WriteLine("google FAIL " + Probe.ShortErr(e)); rc = 1; }

            try
            {
                int[] y = Probe.Http("www.youtube.com", "/generate_204", 6000, out err);
                Console.WriteLine("youtube code=" + y[0] + " conn=" + y[1] + "ms ttfb=" + y[2] + "ms err=" + (err ?? "-"));
                if (y[0] != 204) rc = 1;
            }
            catch (Exception e) { Console.WriteLine("youtube FAIL " + Probe.ShortErr(e)); rc = 1; }

            // 3) 端口就绪轮询：10809 有代理时必须立刻返回
            bool up = Probe.WaitPort(10809, 2000, null);
            Console.WriteLine("waitport=" + up);
            if (!up) rc = 1;

            // 4) 失败路径必须带原因抛出，且原因可读（不能是空字符串）
            try
            {
                Probe.Http("127.0.0.1.nonexistent.invalid", "/generate_204", 1500, out err);
                Console.WriteLine("badhost unexpected success err=" + (err ?? "-"));
            }
            catch (Exception e)
            {
                Console.WriteLine("badhost handled: " + Probe.ShortErr(e));
                if (string.IsNullOrEmpty(Probe.ShortErr(e))) rc = 1;
            }

            Console.WriteLine(rc == 0 ? "ALL PASS" : "FAILURES");
            return rc;
        }
    }
}
