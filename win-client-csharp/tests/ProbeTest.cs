using System;
namespace XPanelWin
{
    internal static class ProbeTest
    {
        static int Main(string[] args)
        {
            int rc = 0;
            try
            {
                int lat = Probe.Latency("www.gstatic.com", 6000);
                Console.WriteLine("latency=" + lat + "ms");
                if (lat <= 0) rc = 1;
            }
            catch (Exception e) { Console.WriteLine("latency FAIL " + e.Message); rc = 1; }

            try
            {
                int[] g = Probe.Http("www.gstatic.com", "/generate_204", 6000);
                Console.WriteLine("google code=" + g[0] + " conn=" + g[1] + "ms ttfb=" + g[2] + "ms");
                if (g[0] != 204) rc = 1;
            }
            catch (Exception e) { Console.WriteLine("google FAIL " + e.Message); rc = 1; }

            try
            {
                int[] y = Probe.Http("www.youtube.com", "/generate_204", 6000);
                Console.WriteLine("youtube code=" + y[0] + " conn=" + y[1] + "ms ttfb=" + y[2] + "ms");
                if (y[0] != 204) rc = 1;
            }
            catch (Exception e) { Console.WriteLine("youtube FAIL " + e.Message); rc = 1; }

            try
            {
                bool up = Probe.WaitPort(10809, 2000, null);
                Console.WriteLine("waitport=" + up);
                if (!up) rc = 1;
            }
            catch (Exception e) { Console.WriteLine("waitport FAIL " + e.Message); rc = 1; }

            Console.WriteLine(rc == 0 ? "ALL PASS" : "FAILURES");
            return rc;
        }
    }
}
