// X-Panel Windows 客户端 v10.0 — 节点链接解析（vless / vmess / trojan / ss）
using System;
using System.Collections.Generic;
using System.Text;

namespace XPanelWin
{
    // 解析结果：协议 + 出站所需全字段。vmess/ss 复用 sni 字段存 host/ws host。
    internal class NodeInfo
    {
        public string proto;                       // vless | vmess | trojan | ss | hy2
        public string uuid, address, port;         // ss: uuid=method:pass
        public string sni, pbk, sid, flow;         // vless reality / trojan tls
        public string net = "tcp", path = "", type = "", security = ""; // vmess 传输层 / hy2 obfs-password
        public bool insecure;                      // hy2: 跳过证书校验
        public string name = "";                   // 备注
    }

    internal static class LinkParser
    {
        // 入口：按 scheme 分发。多行文本（订阅粘贴）逐行试，取第一行解析成功的。失败返回 null。
        public static NodeInfo Parse(string raw)
        {
            if (string.IsNullOrEmpty(raw)) return null;
            foreach (string line0 in raw.Replace("\r", "\n").Split(new[] { '\n' }))
            {
                string line = line0.Trim();
                if (line.Length == 0) continue;
                NodeInfo n = ParseOne(line);
                if (n != null) return n;
            }
            return null;
        }

        private static NodeInfo ParseOne(string raw)
        {
            if (raw.StartsWith("vless://")) return ParseVless(raw);
            if (raw.StartsWith("trojan://")) return ParseTrojan(raw);
            if (raw.StartsWith("vmess://")) return ParseVmess(raw);
            if (raw.StartsWith("ss://")) return ParseSs(raw);
            if (raw.StartsWith("hy2://") || raw.StartsWith("hysteria2://")) return ParseHy2(raw);
            return null;
        }

        // hysteria2://auth@host:port?sni=..&insecure=1&obfs=salamander&obfs-password=..&pinSHA256=..#tag
        // hy2:// 同格式。auth 可能 URL 编码。
        private static NodeInfo ParseHy2(string raw)
        {
            string scheme = raw.StartsWith("hy2://") ? "hy2://" : "hysteria2://";
            string uuid, address, port, sni, pbk, sid, flow;
            if (!ParseUserHost(raw.Substring(scheme.Length), out uuid, out address, out port, out sni, out pbk, out sid, out flow)) return null;
            int p; if (!int.TryParse(port, out p) || p <= 0 || p > 65535) return null;
            var n = new NodeInfo
            {
                proto = "hy2",
                uuid = Uri.UnescapeDataString(uuid),
                address = address,
                port = port,
                sni = sni,
                name = TagOf(raw)
            };
            // query 里的 hy2 专有参数复用 ParseUserHost 已解析的 pbk=pinSHA256；insecure/obfs-password 单独抓
            int q = raw.IndexOf('?');
            if (q >= 0)
            {
                int h = raw.IndexOf('#', q);
                string query = h < 0 ? raw.Substring(q + 1) : raw.Substring(q + 1, h - q - 1);
                foreach (string kv in query.Split(new[] { '&' }))
                {
                    int eq = kv.IndexOf('=');
                    if (eq < 0) continue;
                    string k = kv.Substring(0, eq), v = Uri.UnescapeDataString(kv.Substring(eq + 1));
                    if (k == "insecure" || k == "allowInsecure") n.insecure = (v == "1" || v == "true");
                    else if (k == "obfs-password") n.type = v;
                    else if (k == "pinSHA256" || k == "pinsha256") n.pbk = v;
                }
            }
            return n;
        }

        internal static string B64Decode(string s)
        {
            s = s.Trim().Replace('-', '+').Replace('_', '/');
            while (s.Length % 4 != 0) s += "=";
            try { return Encoding.UTF8.GetString(Convert.FromBase64String(s)); }
            catch { return null; }
        }

        // vless://uuid@host:port?sni=..&pbk=..&sid=..&flow=..#tag
        private static NodeInfo ParseVless(string raw)
        {
            string uuid, address, port, sni, pbk, sid, flow;
            if (!ParseUserHost(raw.Substring(8), out uuid, out address, out port, out sni, out pbk, out sid, out flow)) return null;
            if (string.IsNullOrEmpty(pbk)) return null;   // 无 pbk 的 vless 不是 reality，本客户端不认
            return new NodeInfo { proto = "vless", uuid = uuid, address = address, port = port, sni = sni, pbk = pbk, sid = sid, flow = flow, name = TagOf(raw) };
        }

        // trojan://password@host:port?sni=..&fp=..#tag
        private static NodeInfo ParseTrojan(string raw)
        {
            string uuid, address, port, sni, pbk, sid, flow;
            if (!ParseUserHost(raw.Substring(9), out uuid, out address, out port, out sni, out pbk, out sid, out flow)) return null;
            return new NodeInfo { proto = "trojan", uuid = Uri.UnescapeDataString(uuid), address = address, port = port, sni = sni, net = "tcp", name = TagOf(raw) };
        }

        // 共用：user@host:port?query#tag
        private static bool ParseUserHost(string body, out string user, out string address, out string port,
                                          out string sni, out string pbk, out string sid, out string flow)
        {
            user = address = port = sni = pbk = sid = flow = "";
            int at = body.IndexOf('@');
            if (at < 0) return false;
            user = body.Substring(0, at);
            string rest = body.Substring(at + 1);
            int q = rest.IndexOfAny(new[] { '?', '#' });
            string hostport = q < 0 ? rest : rest.Substring(0, q);
            string query = "";
            if (q >= 0 && rest[q] == '?')
            {
                int h = rest.IndexOf('#', q);
                query = h < 0 ? rest.Substring(q + 1) : rest.Substring(q + 1, h - q - 1);
            }
            int c = hostport.LastIndexOf(':');
            if (c < 0) return false;
            address = hostport.Substring(0, c);
            port = hostport.Substring(c + 1);
            if (user.Length == 0 || address.Length == 0 || port.Length == 0) return false;
            int p; if (!int.TryParse(port, out p) || p <= 0 || p > 65535) return false;

            foreach (string kv in query.Split(new[] { '&' }))
            {
                int eq = kv.IndexOf('=');
                if (eq < 0) continue;
                string k = kv.Substring(0, eq), v = Uri.UnescapeDataString(kv.Substring(eq + 1));
                if (k == "sni") sni = v;
                else if (k == "pbk") pbk = v;
                else if (k == "sid") sid = v;
                else if (k == "flow") flow = v;
            }
            return true;
        }

        private static string TagOf(string raw)
        {
            int h = raw.IndexOf('#');
            if (h < 0) return "";
            string t = raw.Substring(h + 1);
            int q = t.IndexOf('?');
            if (q >= 0) t = t.Substring(0, q);
            return Uri.UnescapeDataString(t);
        }

        // vmess://base64(JSON{v,ps,add,port,id,aid,scy,net,path,host,tls,sni,fp})
        private static NodeInfo ParseVmess(string raw)
        {
            string json = B64Decode(raw.Substring(8));
            if (json == null) return null;
            var jss = new System.Web.Script.Serialization.JavaScriptSerializer();
            Dictionary<string, object> m;
            try { m = jss.Deserialize<Dictionary<string, object>>(json); }
            catch { return null; }
            if (m == null) return null;
            string add = Str(m, "add"), port = Str(m, "port"), id = Str(m, "id");
            if (add.Length == 0 || port.Length == 0 || id.Length == 0) return null;
            int p; if (!int.TryParse(port, out p) || p <= 0 || p > 65535) return null;
            string sni = Str(m, "sni"); if (sni.Length == 0) sni = Str(m, "host");
            return new NodeInfo
            {
                proto = "vmess",
                uuid = id,
                address = add,
                port = port,
                sni = sni,
                net = Str(m, "net").Length > 0 ? Str(m, "net") : "tcp",
                path = Str(m, "path"),
                type = Str(m, "host"),
                security = Str(m, "tls") == "tls" ? "tls" : "",
                name = Str(m, "ps")
            };
        }

        // ss:// 两种形态：SIP002(base64(method:pass)@host:port#tag) 和全 base64
        private static NodeInfo ParseSs(string raw)
        {
            string body = raw.Substring(5);
            int hash = body.IndexOf('#');
            string tag = hash >= 0 ? Uri.UnescapeDataString(body.Substring(hash + 1)) : "";
            if (hash >= 0) body = body.Substring(0, hash);
            int q = body.IndexOf('?'); if (q >= 0) body = body.Substring(0, q);

            string userinfo, hostport;
            int at = body.IndexOf('@');
            if (at >= 0) { userinfo = body.Substring(0, at); hostport = body.Substring(at + 1); }
            else
            {
                string decoded = B64Decode(body);
                if (decoded == null) return null;
                int a2 = decoded.IndexOf('@');
                if (a2 < 0) return null;
                userinfo = decoded.Substring(0, a2);
                hostport = decoded.Substring(a2 + 1);
            }
            if (userinfo.IndexOf(':') < 0)   // userinfo 本身还是 base64（SIP002 可选）
            {
                string du = B64Decode(userinfo);
                if (du != null && du.IndexOf(':') >= 0) userinfo = du;
            }
            int c = hostport.LastIndexOf(':');
            if (c < 0) return null;
            string address = hostport.Substring(0, c), port = hostport.Substring(c + 1);
            int p; if (!int.TryParse(port, out p) || p <= 0 || p > 65535) return null;
            if (userinfo.IndexOf(':') < 0) return null;
            return new NodeInfo { proto = "ss", uuid = userinfo, address = address, port = port, name = tag };
        }

        private static string Str(Dictionary<string, object> m, string k)
        {
            object v;
            if (m == null || !m.TryGetValue(k, out v) || v == null) return "";
            return v.ToString().Trim();
        }
    }
}
