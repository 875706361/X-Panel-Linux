using System;
using System.Drawing;
using System.Reflection;
using System.Web.Script.Serialization;
namespace XPanelWin {
internal static class ExpiryTests {
    static int failures;

    // 反射调用 ExpireLine(object expiryTime, bool expiryPending, out Color color)。
    static string Invoke(object expiry, bool pending) {
        var m = typeof(MainForm).GetMethod("ExpireLine", BindingFlags.NonPublic | BindingFlags.Static, null,
            new[] { typeof(object), typeof(bool), typeof(Color).MakeByRefType() }, null);
        return (string)m.Invoke(null, new[] { expiry, (object)pending, Color.Empty });
    }

    static void Check(string name, object value, string expected) {
        CheckPending(name, value, false, expected);
    }

    static void CheckEquals(string name, string actual, string expected) {
        if (actual == expected) Console.WriteLine("PASS " + name);
        else { failures++; Console.WriteLine("FAIL " + name + ": expected '" + expected + "', got '" + actual + "'"); }
    }

    static void CheckPending(string name, object value, bool pending, string expected) {
        try {
            string actual = Invoke(value, pending);
            if (!actual.Contains(expected)) throw new Exception("expected '" + expected + "', got '" + actual + "'");
            Console.WriteLine("PASS " + name);
        } catch (Exception ex) { failures++; Console.WriteLine("FAIL " + name + ": " + ex.Message); }
    }

    // 端到端：走真实 TrafficResp/TrafficObj 反序列化，再渲染到期文案。
    static void CheckResponse(string name, string json, string expected) {
        try {
            var respType = typeof(MainForm).GetNestedType("TrafficResp", BindingFlags.NonPublic);
            var resp = new JavaScriptSerializer().Deserialize(json, respType);
            var obj = respType.GetField("obj").GetValue(resp);
            var objType = obj.GetType();
            object expiry = objType.GetField("expiryTime").GetValue(obj);
            var pendingField = objType.GetField("expiryPending");
            bool pending = pendingField != null && (bool)pendingField.GetValue(obj);
            string actual = Invoke(expiry, pending);
            if (!actual.Contains(expected)) throw new Exception("expected '" + expected + "', got '" + actual + "'");
            Console.WriteLine("PASS " + name);
        } catch (Exception ex) { failures++; Console.WriteLine("FAIL " + name + ": " + ex.Message); }
    }

    // 连接建立但服务端不回包时，HttpGet 必须在硬超时内返回 null；
    // 面板端口被丢包时要靠这个机制，否则一轮轮询能卡几十秒（界面就一直停在"查询中"）。
    static void CheckHardTimeout()
    {
        System.Net.Sockets.TcpListener l = null;
        try
        {
            l = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0);
            l.Start();
            int port = ((System.Net.IPEndPoint)l.LocalEndpoint).Port;
            var m = typeof(MainForm).GetMethod("HttpGet", BindingFlags.NonPublic | BindingFlags.Static);
            var sw = System.Diagnostics.Stopwatch.StartNew();
            object r = m.Invoke(null, new object[] { "http://127.0.0.1:" + port + "/hang", 800, false });
            sw.Stop();
            if (r != null) throw new Exception("expected null, got " + r);
            if (sw.ElapsedMilliseconds > 2500) throw new Exception("blocked " + sw.ElapsedMilliseconds + "ms");
            Console.WriteLine("PASS hard timeout (" + sw.ElapsedMilliseconds + "ms)");
        }
        catch (Exception ex) { failures++; Console.WriteLine("FAIL hard timeout: " + ex.Message); }
        finally { try { if (l != null) l.Stop(); } catch { } }
    }

    // 自动更新必须只升不降：镜像里还是旧包时，不能把已修好的新版换回去。
    static void CheckUpdateVersionGuard()
    {
        var m = typeof(Updater).GetMethod("CompareVersion", BindingFlags.NonPublic | BindingFlags.Static);
        Func<string, string, int> cmp = (a, b) => (int)m.Invoke(null, new object[] { a, b });
        CheckEquals("same version is not newer", cmp("v10.4.3", "10.4.3") == 0 ? "same" : "diff", "same");
        CheckEquals("older release is rejected", cmp("v10.4.2", "v10.4.3") < 0 ? "older" : "not-older", "older");
        CheckEquals("newer release is accepted", cmp("v10.5.0", "v10.4.3") > 0 ? "newer" : "not-newer", "newer");
        CheckEquals("two digit segment compares numerically", cmp("v10.10.0", "v10.9.9") > 0 ? "newer" : "not-newer", "newer");
    }

    public static int Main() {
        // 面板地址探测（节点域名常不等于面板域名，路径也未必在 /clay）
        string[] urls = MainForm.PanelTrafficUrls("node.example.com", "abc", "");
        string[] expected = {
            "https://node.example.com/clay/panel/pub/traffic/abc",
            "https://node.example.com/panel/pub/traffic/abc",
            "https://node.example.com:1111/clay/panel/pub/traffic/abc",
            "https://node.example.com:1111/panel/pub/traffic/abc",
            "https://888888666.xyz:1111/clay/panel/pub/traffic/abc",
        };
        CheckEquals("candidate order", string.Join("|", urls), string.Join("|", expected));
        CheckEquals("no duplicate candidates", urls.Length.ToString(), new System.Collections.Generic.HashSet<string>(urls).Count.ToString());
        CheckEquals("no plain-http candidate on TLS port 1111",
            "0",
            System.Array.FindAll(urls, u => u.StartsWith("http://", System.StringComparison.Ordinal)
                                            && u.IndexOf(":1111", System.StringComparison.Ordinal) > 0).Length.ToString());

        CheckEquals("empty id yields no requests", MainForm.PanelTrafficUrls("h", "", "").Length.ToString(), "0");
        CheckEquals("known base goes first",
            "https://node.example.com:1111/panel/pub/traffic/abc",
            MainForm.PanelTrafficUrls("node.example.com", "abc", "https://node.example.com:1111")[0]);

        // 探测循环：只有第二档 /panel 有面板时仍能命中
        string hit;
        bool reach;
        string body = MainForm.FetchPanelJson("node.example.com", "abc", "", url =>
            url.IndexOf(":1111/panel/pub", StringComparison.Ordinal) > 0 ? "{\"success\":true}" : null, out hit, out reach);
        CheckEquals("fallback probe hits second candidate", hit, "https://node.example.com:1111");
        CheckEquals("fallback probe returns body", body, "{\"success\":true}");
        CheckEquals("fallback probe marks reachable", reach ? "true" : "false", "true");

        // success:false = 面板通了但没有这个 uuid；必须能和「面板完全不通」区分开
        body = MainForm.FetchPanelJson("node.example.com", "abc", "", url => "{\"success\":false}", out hit, out reach);
        CheckEquals("not-found body yields null", body == null ? "null" : body, "null");
        CheckEquals("not-found still marks reachable", reach ? "true" : "false", "true");
        body = MainForm.FetchPanelJson("node.example.com", "abc", "", url => null, out hit, out reach);
        CheckEquals("unreachable not marked reachable", reach ? "true" : "false", "false");

        // 全失败（success:false / 连不上）必须返回 null，让界面显示「未获取」而不是「长期有效」
        body = MainForm.FetchPanelJson("node.example.com", "abc", "", url => "{\"success\":false}", out hit);
        CheckEquals("all candidates failing yields null", body == null ? "null" : body, "null");
        body = MainForm.FetchPanelJson("node.example.com", "abc", "", url => null, out hit);
        CheckEquals("unreachable yields null", body == null ? "null" : body, "null");

        // 文案语义
        Check("missing expiry is unknown", null, "未获取");
        Check("invalid expiry is unknown", "bad", "未获取");
        Check("raw negative duration is pending", -2592000000L, "未激活");
        Check("explicit zero is unlimited", 0L, "长期有效");
        Check("seconds timestamp", 1893456000L, "2030/01/01");
        Check("milliseconds timestamp", 1893456000000L, "2030/01/01");
        Check("numeric string timestamp", "1893456000000", "2030/01/01");
        Check("float timestamp", 1893456000000d, "2030/01/01");
        Check("expired timestamp", 1000000000000L, "已过期");

        // 回归：到期后不足一天曾被截断成「今天 (0天)」，界面看起来还没到期，
        // 但面板此时已经停用了账号；最后一天要按小时报。
        long nowMs = (long)(DateTime.UtcNow - new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc)).TotalMilliseconds;
        Check("expired two hours ago reads expired", nowMs - 2 * 3600 * 1000L, "已过期");
        Check("last day reports hours", nowMs + 5 * 3600 * 1000L, "小时");
        Check("just under 24h reports hours", nowMs + 23 * 3600 * 1000L, "小时");
        Check("thirty days left stays in days", nowMs + 29L * 86400000L + 3600 * 1000L, "29天");
        // 负值以秒存储的旧面板：-2592000 = 30 天，不能算成 43 分钟。
        Check("negative seconds duration", -2592000L, "首连起30天");

        // 端到端（真实面板响应形态）
        CheckResponse("panel: pending activation flag",
            "{\"success\":true,\"obj\":{\"expiryTime\":1893456000000,\"expiryPending\":true}}", "未激活");
        CheckResponse("panel: fixed expiry",
            "{\"success\":true,\"obj\":{\"expiryTime\":1893456000000,\"expiryPending\":false}}", "2030/01/01");
        CheckResponse("panel: unlimited (0)",
            "{\"success\":true,\"obj\":{\"expiryTime\":0,\"expiryPending\":false}}", "长期有效");
        CheckResponse("panel: field missing must not read unlimited",
            "{\"success\":true,\"obj\":{\"up\":1,\"down\":2,\"total\":0}}", "未获取");
        CheckResponse("panel: null field must not read unlimited",
            "{\"success\":true,\"obj\":{\"expiryTime\":null}}", "未获取");

        CheckHardTimeout();
        CheckUpdateVersionGuard();
        CheckManifestBases();
        CheckHostOf();
        CheckNormalize();
        CheckCloseOnImportPage();
        CheckPage1Grid();

        Console.WriteLine("Failures: " + failures);
        return failures == 0 ? 0 : 1;
    }

    // 自动更新的镜像候选：上次命中优先，节点域名兜底，内置面板最后，且不重复。
    static void CheckManifestBases() {
        var bases = Updater.ManifestBases("node.example.com", "https://panel.example.com:1111/clay");
        CheckEquals("manifest base prefers last hit", bases[0], "https://panel.example.com:1111/clay");
        if (Array.IndexOf(bases, "https://node.example.com:1111/clay") < 0) {
            failures++; Console.WriteLine("FAIL manifest base missing node candidate");
        } else Console.WriteLine("PASS manifest base has node candidate");
        if (Array.IndexOf(bases, MainForm.PANEL_BASE) < 0) {
            failures++; Console.WriteLine("FAIL manifest base missing built-in panel");
        } else Console.WriteLine("PASS manifest base has built-in panel");
        var uniq = new System.Collections.Generic.HashSet<string>(bases);
        CheckEquals("manifest bases deduped", uniq.Count.ToString(), bases.Length.ToString());
        // 没有节点域名、也没有上次命中时，第一个候选是面板域名的 443（国内最稳）
        CheckEquals("manifest base falls back to panel 443",
            Updater.ManifestBases("", "")[0], "https://888888666.xyz/clay");
        // 1111 是 TLS 端口：候选里不能再有明文 http://...:1111，否则每轮白等一个超时
        CheckEquals("manifest bases drop plain-http on 1111", "0",
            Array.FindAll(bases, b => b.StartsWith("http://", StringComparison.Ordinal)
                                      && b.IndexOf(":1111", StringComparison.Ordinal) > 0).Length.ToString());
        // 运行期记住上次命中的清单地址，下一轮把它提到最前（少试 1–2 个超时候选）
        var reordered = Updater.PreferBase(new[] { "https://a", "https://b", "https://c" }, "https://c");
        CheckEquals("prefer base moves hit to front", string.Join("|", reordered), "https://c|https://a|https://b");
        CheckEquals("prefer base keeps order when absent",
            string.Join("|", Updater.PreferBase(new[] { "https://a", "https://b" }, "https://zzz")),
            "https://a|https://b");
        CheckEquals("prefer base ignores empty hint",
            string.Join("|", Updater.PreferBase(new[] { "https://a", "https://b" }, "")),
            "https://a|https://b");
        // 替换脚本的两条底线：旧文件用绝对路径；失败分支必须还原并启动旧版，
        // 否则「改名成功 + 覆盖失败」会把用户电脑上的软件直接变没。
        string sh = Updater.BuildReplaceScript(1, "C:\\tmp\\new.exe", "C:\\app\\XPanel.exe");
        CheckEquals("replace script uses absolute old path", "true",
            sh.Contains("del \"C:\\app\\XPanel.old.exe\"") ? "true" : "false");
        CheckEquals("replace script restores old exe on failure", "true",
            sh.Contains("if exist \"C:\\app\\XPanel.old.exe\" if not exist \"C:\\app\\XPanel.exe\" ren") ? "true" : "false");
        CheckEquals("replace script relaunches on failure", "true",
            sh.Contains("if exist \"C:\\app\\XPanel.exe\" start \"\" \"C:\\app\\XPanel.exe\"") ? "true" : "false");
    }

    static void CheckHostOf() {
        CheckEquals("host of vless link",
            LinkParser.HostOf("vless://11111111-2222-3333-4444-555555555555@1.2.3.4:443?encryption=none&security=reality&pbk=aGVsbG93b3JsZA&type=tcp#t"), "1.2.3.4");
        CheckEquals("host of garbage is empty", LinkParser.HostOf("not a link"), "");
    }

    // 网页复制污染：&amp; 实体、零宽字符、全角符号都必须能救回来，否则用户看到「格式错误」
    // 却坚信链接没错（真实 case：面板网页把 & 输出成 &amp;，pbk 的 key 变成 amp;pbk）。
    static void CheckNormalize() {
        var a = LinkParser.Parse("vless://u@h.example:443?type=tcp&amp;security=reality&amp;pbk=abc&amp;sid=01#t");
        CheckEquals("&amp; escaped link parses", a != null && a.pbk == "abc" ? "ok" : "fail", "ok");
        var b = LinkParser.Parse("vless://u@h.example:443?type=tcp\u200b&security=reality&pbk=abc&sid=01#t");
        CheckEquals("zero width space parses", b != null && b.port == "443" ? "ok" : "fail", "ok");
        var c = LinkParser.Parse("vless://u@h.example：443?type=tcp&security=reality&pbk=abc&sid=01#t");
        CheckEquals("full width colon parses", c != null && c.port == "443" ? "ok" : "fail", "ok");
        CheckEquals("explain missing pbk",
            LinkParser.ExplainFailure("vless://u@h.example:443?type=tcp&security=tls#t"),
            "vless 缺少 pbk（reality 公钥），参数可能被截断");
    }

    // 导入页点叉必须真退出：老版本一律隐藏到托盘，用户以为关了，再点图标被单实例挡掉，
    // 只能去任务管理器杀进程。
    static void CheckPage1Grid() {
        CheckEquals("page1 grid has no overlap", MainForm.CheckPage1Grid(), "");
    }

    static void CheckCloseOnImportPage() {
        CheckEquals("close on import page quits", MainForm.ShouldQuitOnClose(1, false).ToString(), "True");
        CheckEquals("close on dashboard hides to tray", MainForm.ShouldQuitOnClose(2, false).ToString(), "False");
        CheckEquals("close on settings hides to tray", MainForm.ShouldQuitOnClose(3, false).ToString(), "False");
        CheckEquals("close while connected hides to tray", MainForm.ShouldQuitOnClose(1, true).ToString(), "False");
    }
}}
