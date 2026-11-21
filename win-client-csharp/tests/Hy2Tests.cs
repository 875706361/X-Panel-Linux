// hy2 解析 + 配置生成回归测试（mcs 编译，mono 运行）
using System;
using System.Collections.Generic;

namespace XPanelWin
{
    internal static class Hy2Tests
    {
        static int fail = 0;

        static void Check(string name, bool ok, string detail)
        {
            Console.WriteLine((ok ? "PASS " : "FAIL ") + name + (detail.Length > 0 ? "  -> " + detail : ""));
            if (!ok) fail++;
        }

        static string Show(NodeInfo n)
        {
            if (n == null) return "(null)";
            return string.Format("proto={0} addr={1} port={2} auth={3} sni={4} pbk={5} obfs={6} insecure={7}",
                n.proto, n.address, n.port, n.uuid, n.sni, n.pbk, n.type, n.insecure);
        }

        static void Main(string[] args)
        {
            // 调试模式：Hy2Tests <link> <out.yaml> —— 用真实代码路径生成内核配置，供 end-to-end 验证
            if (args.Length >= 2)
            {
                NodeInfo node = LinkParser.Parse(args[0]);
                if (node == null) { Console.WriteLine("PARSE_FAIL"); Environment.Exit(2); }
                string y = Xray.BuildHy2Config(node, false);
                System.IO.File.WriteAllText(args[1], y, new System.Text.UTF8Encoding(false));
                Console.WriteLine("WROTE " + args[1]);
                Console.WriteLine(y);
                Environment.Exit(0);
            }

            var cases = new List<string[]>
            {
                new[] { "A 无斜杠", "hy2://pass@1.2.3.4:443?sni=a.com&insecure=1#n1" },
                new[] { "B 带斜杠(常见)", "hy2://pass@1.2.3.4:443/?sni=a.com&insecure=1#n2" },
                new[] { "C obfs 无斜杠", "hy2://pass@1.2.3.4:443?sni=a.com&obfs=salamander&obfs-password=xyz#n3" },
                new[] { "D obfs 带斜杠", "hy2://pass@1.2.3.4:443/?sni=a.com&obfs=salamander&obfs-password=xyz#n4" },
                new[] { "E hysteria2://", "hysteria2://pass@example.com:443?insecure=1#n5" },
                new[] { "F 密码 %40", "hy2://p%40ss@example.com:443?insecure=1" },
                new[] { "G pinSHA256", "hy2://pass@example.com:443?insecure=0&pinSHA256=AbC+/d==#n6" },
                new[] { "H 无 userinfo", "hy2://example.com:443?auth=pass" },
                new[] { "I 只有 host:port", "hy2://example.com:443" },
                new[] { "J 大写 scheme", "HY2://pass@example.com:443" },
                new[] { "K sni 带端口/空格", "hy2://pass@example.com:443?sni=example.com&insecure=true#tag" },
            };

            foreach (var c in cases)
            {
                NodeInfo n = LinkParser.Parse(c[1]);
                Console.WriteLine("[" + c[0] + "] " + Show(n));
            }

            Console.WriteLine("---- 配置生成（obfs 嵌套形式检查）");
            NodeInfo obfs = LinkParser.Parse("hy2://mypass@1.2.3.4:443/?sni=a.com&obfs=salamander&obfs-password=obspw#t");
            Check("D 带斜杠应能解析", obfs != null, "");
            if (obfs != null)
            {
                string cfg = Xray.BuildHy2Config(obfs, false);
                Console.WriteLine(cfg.Replace("\n", " | "));
                Check("obfs 密码必须是 salamander.password 嵌套", cfg.Contains("salamander:") && cfg.Contains("password: \"obspw\""), cfg);
                Check("obfs 类型应是 salamander", cfg.Contains("type: salamander"), "");
            }

            NodeInfo plain = LinkParser.Parse("hy2://p:w#d\"q@1.2.3.4:443?sni=a.com#x");
            if (plain != null)
            {
                string cfg2 = Xray.BuildHy2Config(plain, false);
                Check("含特殊字符的 auth 必须被引号包住", cfg2.Contains("auth: \"") || cfg2.Contains("auth: '"), cfg2);
                Check("含特殊字符的 sni 必须被引号包住", cfg2.Contains("\"a.com\""), cfg2);
            }

            // ---- WaitReady：连接成功的判定不能只看 Process.Start ----
            Console.WriteLine("---- 就绪检测（内核端口）");
            var py = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("python3",
                "-m http.server 39081 --bind 127.0.0.1")
            { UseShellExecute = false, CreateNoWindow = true });
            string e1;
            bool r1 = Xray.WaitReady(py, 39081, 8000, out e1);
            try { py.Kill(); } catch { }
            Check("WaitReady: 端口监听起来 -> true", r1, e1);

            var sleeper = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("sleep", "30")
            { UseShellExecute = false, CreateNoWindow = true });
            string e2;
            bool r2 = Xray.WaitReady(sleeper, 39082, 1500, out e2);
            try { sleeper.Kill(); } catch { }
            Check("WaitReady: 端口不通 -> false（不会假装已连接）", !r2, "");
            Check("失败文案可读", Xray.Short(e2).Length > 0, Xray.Short(e2));

            var gone = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("true", "")
            { UseShellExecute = false, CreateNoWindow = true });
            gone.WaitForExit(3000);
            string e3;
            bool r3 = Xray.WaitReady(gone, 39083, 3000, out e3);
            Check("WaitReady: 进程已退出 -> 立即 false", !r3, Xray.Short(e3));

            Console.WriteLine(fail == 0 ? "ALL PASS" : ("FAILURES: " + fail));
            Environment.Exit(fail == 0 ? 0 : 1);
        }
    }
}
