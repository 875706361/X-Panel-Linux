// 主窗口（第四部分：登录 / 代理开关 / 流量轮询 / 日志）
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.Net;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Win32;
using System.Web.Script.Serialization;
using System.Windows.Forms;

namespace XPanelWin
{
    internal partial class MainForm
    {
        // ---------- 登录 ----------

        private void HandleLogin()
        {
            // 多行粘贴（带换行/空格）在 LinkParser 里逐行 Trim 处理，这里只去掉首尾空白。
            string raw = (_edit.Text ?? "").Trim();
            LogToFile("[LOGIN] 点击登录 page=" + _page + " 长度=" + raw.Length);
            // 点击瞬间就给出可见反馈：Windows 上解析/切页若被别的消息挤到下一帧，用户会
            // 认为按钮没反应。DoEvents 强制先把「正在登录…」画出来，再继续。
            try { _btnLogin.Text = "正在登录…"; Application.DoEvents(); } catch { }
            try
            {
                if (raw.Length == 0)
                {
                    ShowLoginError("请先粘贴节点链接，再点「登录节点」");
                    return;
                }
                NodeInfo n = null;
                try { n = LinkParser.Parse(raw); }
                catch (Exception ex) { LogToFile("[LOGIN] 解析异常: " + ex.Message); }
                if (n == null)
                {
                    // 提示里带上输入头部：用户说「点了没反应」时，日志和界面都能直接看出喂进去的是什么。
                    string brief = raw.Length > 24 ? raw.Substring(0, 24) + "…" : raw;
                    string why = LinkParser.ExplainFailure(raw);
                    LogToFile("[LOGIN] 解析失败 原因=" + why + " 内容=" + brief);
                    ShowLoginError(why);
                    return;
                }
                LogToFile("[LOGIN] 解析成功 proto=" + n.proto + " " + n.address + ":" + n.port + " 切页");
                _loginFailed = false;
                _node = n;
                SaveLink(raw);
                _lblTip.Text = "";
                SwitchPage(2);
            }
            finally
            {
                // 失败态由 ShowLoginError 的定时器负责恢复（红底要留一会儿让用户看见）。
                if (!_loginFailed) RestoreLoginButton();
            }
        }

        // 登录失败：红字 + 红框 + 按钮本身变红并改文案。之前只有一行 13px 小字，用户看不到
        // 就以为「点登录没反应」——按钮是点击目标，反馈必须出现在按钮上。
        private void ShowLoginError(string msg)
        {
            _loginFailed = true;
            _tipIsUpdate = false;
            _lblTip.ForeColor = Theme.C(Theme.DANGER);
            _lblTip.Text = msg;
            InvalidateInput();
            try
            {
                // 保持红色直到用户修改输入：1.6 秒的闪烁会被更新提示/重绘吃掉，
                // 用户看到的就是「点了没反应」。
                _btnLogin.Text = "链接无效，请检查";
                _btnLogin.SetColors(Theme.C(Theme.DANGER), Theme.C(Theme.DANGER_H),
                                    Theme.C(Theme.DANGER_H), Theme.C(Theme.DANGER), Color.White);
            }
            catch { }
        }

        private void RestoreLoginButton()
        {
            try
            {
                _btnLogin.Text = "登录节点";
                _btnLogin.SetColors(Theme.C(Theme.ACCENT), Theme.C(Theme.ACCENT_H),
                                    Theme.C(Theme.ACCENT_P), Theme.C(Theme.ACCENT_P), Color.White);
            }
            catch { }
        }

        // 输入框描边是否进入错误态（登录失败时变红，比一行小字更醒目）
        internal bool LoginFailed { get { return _loginFailed; } }

        // ---------- 重置全部设置 ----------
        private void HandleReset()
        {
            var res = MessageBox.Show("确定要重置全部设置并恢复到初始状态吗？\n将断开连接并清空已保存的节点信息。", "重置确认", MessageBoxButtons.YesNo, MessageBoxIcon.Warning);
            if (res != DialogResult.Yes) return;

            try
            {
                if (_connected) StopProxy();
            }
            catch { }

            // 清理注册表配置
            try
            {
                using (var k = Registry.CurrentUser.OpenSubKey(REG_KEY, true))
                {
                    if (k != null)
                    {
                        k.DeleteValue("link", false);
                        k.DeleteValue("bypass_cn", false);
                    }
                }
            }
            catch { }

            // 恢复内存与界面状态
            _node = null;
            _bypassCn = true;
            ApplyModeStyle();
            _edit.Text = "";
            _lblTip.Text = "";
            _lblStatus.Text = "未连接";
            _lblStatus.ForeColor = Theme.C(Theme.IDLE);
            _lblTraffic.Text = "0.00 MB";
            _lblUp.Text = "0.00 MB";
            _lblDown.Text = "0.00 MB";

            LogToFile("[RESET] 用户重置全部设置，恢复初始状态");
            SwitchPage(1);
        }

        // ---------- 代理开关 ----------

        private void ToggleConnect()
        {
            lock (_stateLock)
            {
                if (_busy) return;
                _busy = true;
            }
            try
            {
                if (_connected) StopProxy();
                else StartProxy();
            }
            finally
            {
                lock (_stateLock) _busy = false;
            }
        }

        private void StartProxy()
        {
            NodeInfo n = LinkParser.Parse(_edit.Text);
            if (n == null)
            {
                SetStatus("链接格式错误", Theme.C(Theme.DANGER));
                return;
            }
            _node = n;
            SetStatus("正在连接...", Theme.C(Theme.ACCENT));

            try { Xray.EnsureFiles(); }
            catch (Exception ex)
            {
                SetStatus("内核文件缺失", Theme.C(Theme.DANGER));
                LogToFile("[XRAY] " + ex.Message);
                return;
            }

            // hysteria2 走 hysteria.exe，bypassCn 由 BuildHy2Config 内部 ACL 处理
            Process p;
            try { p = Xray.Start(n.proto == "hy2" ? Xray.BuildHy2Config(n, _bypassCn) : Xray.BuildConfig(n, _bypassCn), n.proto); }
            catch (Exception ex)
            {
                SetStatus("内核启动失败", Theme.C(Theme.DANGER));
                LogToFile("[XRAY] " + ex.Message);
                return;
            }
            _xray = p;
            SetFooterHint(null, false);   // 上一次的失败原因清掉

            // 不再"Start 没抛异常就算连上"：等内核真的把 10809 端口监听起来再切系统代理。
            // hy2 是先握手成功才监听，所以这一步连认证/obfs/TLS 一起验了；内核秒退时立刻报错，
            // 而不是让用户对着"已连接"却上不了网。
            int gen; lock (_stateLock) gen = ++_connectGen;
            int waitMs = n.proto == "hy2" ? 9000 : 3500;
            string protoTag = n.proto.ToUpper(), nodeName = n.name ?? "";
            var th = new Thread(() =>
            {
                string err;
                bool ok = Xray.WaitReady(p, 10809, waitMs, out err);
                bool stale; lock (_stateLock) stale = (gen != _connectGen);
                if (stale) { Xray.Stop(p); return; }
                if (!ok)
                {
                    Xray.Stop(p);
                    string why = Xray.Friendly(err);
                    LogToFile("[CONN] 连接失败：" + why + " | 内核输出: " + err);
                    Post(() =>
                    {
                        _xray = null;
                        _connected = false;
                        SetConnButton(false);
                        SetFooterHint("✕ " + why, true);
                        SetStatus("连接失败", Theme.C(Theme.DANGER));
                        Invalidate();
                    });
                    return;
                }
                try { SetSystemProxy(true, "127.0.0.1:10809"); } catch { }
                Post(() =>
                {
                    _connected = true;
                    SetConnButton(true);
                    SetFooterHint(null, false);
                    ShowTip("已连接 · " + protoTag + (nodeName.Length > 0 ? " · " + nodeName : ""), false);
                    Invalidate();
                });
            });
            th.IsBackground = true;
            th.Start();
            SetStatus("正在连接...", Theme.C(Theme.ACCENT));
        }

        // UI 线程投递：窗口已销毁时静默丢弃（异步结果可能晚于关闭）
        private void Post(Action a)
        {
            try
            {
                if (!IsHandleCreated) return;
                if (InvokeRequired) BeginInvoke(a);
                else a();
            }
            catch (Exception ex) { LogToFile("[UI] " + ex.Message); }
        }

        // 状态文案同时照顾导入页（只有提示行，没有状态栏）
        private void ShowTip(string text, bool danger)
        {
            if (_page == 1)
            {
                _lblTip.Text = text;
                _lblTip.ForeColor = danger ? Theme.C(Theme.DANGER) : Theme.C(Theme.SUCCESS);
            }
            SetStatus(text, danger ? Theme.C(Theme.DANGER) : Theme.C(Theme.SUCCESS));
        }

        // 失败原因写在仪表盘底部那行（状态位上只有 116px，塞不下"认证失败，检查密码与 obfs …"）
        internal const string FOOTER_DEFAULT = "系统代理 · 本地 Socks 10808 / Http 10809";
        private void SetFooterHint(string text, bool danger)
        {
            if (_lblFooter == null) return;
            _lblFooter.Text = text ?? FOOTER_DEFAULT;
            _lblFooter.ForeColor = danger ? Theme.C(Theme.DANGER) : Theme.C(Theme.FAINT);
        }

        private void StopProxy()
        {
            lock (_stateLock) _connectGen++;   // 作废还没回来的就绪检查
            Xray.Stop(_xray);
            _xray = null;
            SetSystemProxy(false, "");
            _connected = false;
            _lastUp = _lastDown = _speedUp = _speedDown = 0;
            SetConnButton(false);
            SetFooterHint(null, false);
            SetStatus("未连接", Theme.C(Theme.IDLE));
            Invalidate();
        }

        private void SetConnButton(bool connected)
        {
            if (InvokeRequired) { Invoke(new Action<bool>(SetConnButton), connected); return; }
            _btnConn.Text = connected ? "断开连接" : "启动加速";
            // 已连接时"启动加速"变"断开连接"：红底危险态（Go btnStyleFor 同款）
            if (connected) _btnConn.SetDangerStyle();
            else ApplyStyle();
        }

        private void SetStatus(string text, Color clr)
        {
            try
            {
                if (InvokeRequired)
                {
                    if (!IsHandleCreated) return;
                    BeginInvoke(new Action<string, Color>(SetStatus), text, clr);
                    return;
                }
                _lblStatus.Text = text;
                _lblStatus.ForeColor = clr;
            }
            catch (Exception ex) { LogToFile("[UI] " + ex.Message); }
        }

        // 系统代理：写注册表 + 通知 Wininet 刷新，让已打开的浏览器立即生效
        private static void SetSystemProxy(bool enable, string addr)
        {
            try
            {
                using (var k = Registry.CurrentUser.CreateSubKey(
                    "Software\\Microsoft\\Windows\\CurrentVersion\\Internet Settings"))
                {
                    if (enable)
                    {
                        k.SetValue("ProxyEnable", 1, RegistryValueKind.DWord);
                        k.SetValue("ProxyServer", addr, RegistryValueKind.String);
                        k.SetValue("ProxyOverride", "localhost;127.*;10.*;192.168.*;<local>", RegistryValueKind.String);
                    }
                    else
                    {
                        k.SetValue("ProxyEnable", 0, RegistryValueKind.DWord);
                    }
                }
                Native.InternetSetOption(IntPtr.Zero, 39, IntPtr.Zero, 0); // PROXY_SETTINGS_CHANGED
                Native.InternetSetOption(IntPtr.Zero, 37, IntPtr.Zero, 0); // REFRESH
            }
            catch (Exception ex) { LogToFile("[PROXY] " + ex.Message); }
        }

        // ---------- 流量轮询 ----------

        private const int POLL_OK_MS = 2000;      // 正常节奏：2 秒一次
        private const int POLL_FAIL_MS = 8000;    // 查不到时退避，避免超时把轮询拖成"永远在查"
        private const int POLL_TIMEOUT_MS = 1200; // 单个候选的硬超时（含 DNS/TCP 连接阶段）
        private const string LOCAL_HTTP_PROXY = "http://127.0.0.1:10809";

        private async Task PollTrafficLoop()
        {
            // 面板常用自签证书或直接用 IP 访问，证书必然对不上；校验失败会让所有候选都超时。
            ServicePointManager.ServerCertificateValidationCallback = (s, cert, chain, errors) => true;
            while (true)
            {
                int wait = POLL_OK_MS;
                try
                {
                    if (_node != null) _serverUuid = _node.uuid;   // 面板流量接口按 uuid 查（仅 vless 面板节点有效）
                    if (string.IsNullOrEmpty(_serverUuid))
                    {
                        await Task.Delay(500);
                        continue;
                    }

                    string host = _node != null && !string.IsNullOrEmpty(_node.address) ? _node.address : "";
                    string baseUrl;
                    bool panelReachable;
                    // 已连接时走本地 http 入站（127.0.0.1:10809）：面板端口在国内常被干扰/封锁，
                    // 直连查不到是常态；走自己刚建立的隧道才稳定。
                    string body = FetchPanelJson(host, _serverUuid, _panelBase,
                        url => HttpGet(url, POLL_TIMEOUT_MS, _connected), out baseUrl, out panelReachable);
                    if (body == null)
                    {
                        Color gray = ColorTranslator.FromHtml("#7a7a82");
                        // 面板通、但这台机器的入站里没有该 uuid（多数是导入的不是本面板节点/协议不支持查询）
                        SetExpireLabel(panelReachable ? "到期: 节点未登记" : "到期: 查询失败", gray);
                        wait = POLL_FAIL_MS;
                    }
                    else
                    {
                        if (!string.Equals(_panelBase, baseUrl, StringComparison.Ordinal))
                        {
                            _panelBase = baseUrl;
                            SavePanelBase(baseUrl);      // 下次启动直接用命中的地址
                            LogToFile("[POLL] 面板地址命中 " + baseUrl);
                        }

                        var jss = new JavaScriptSerializer();
                        var res = jss.Deserialize<TrafficResp>(body);
                        if (res == null || !res.success || res.obj == null)
                        {
                            SetExpireLabel("到期: 节点未登记", ColorTranslator.FromHtml("#7a7a82"));
                            wait = POLL_FAIL_MS;
                        }
                        else
                        {

                    long up = res.obj.up, down = res.obj.down, total = res.obj.total;
                    if (_lastUp > 0 || _lastDown > 0)
                    {
                        _speedUp = (up - _lastUp) / 2;
                        _speedDown = (down - _lastDown) / 2;
                        if (_speedUp < 0) _speedUp = 0;
                        if (_speedDown < 0) _speedDown = 0;
                    }
                    _lastUp = up; _lastDown = down;

                    string tot = total > 0 ? " / " + FmtBytes(total) : "";
                    SetText(_lblTraffic, FmtBytes(up + down) + tot);
                    SetText(_lblUp, FmtBytes(up));
                    SetText(_lblDown, FmtBytes(down));
                    Color expColor;
                    string expText = ExpireLine(res.obj.expiryTime, res.obj.expiryPending, out expColor);
                    SetExpireLabel(expText, expColor);
                    SetText(_lblSpeed, "↑ " + FmtBytes(_speedUp) + "/s  ↓ " + FmtBytes(_speedDown) +
                                       "/s · 客户端 " + VER);
                        }
                    }
                }
                catch (Exception ex) { LogToFile("[POLL] " + ex.Message); wait = POLL_FAIL_MS; }
                await Task.Delay(wait);
            }
        }

        // 面板查询候选地址。节点域名常与面板本身不同（中转 / CDN / 反代），路径也未必挂在 /clay 下，
        // 所以按「节点→默认面板」逐个试；命中的地址会被后续轮询优先复用（见 _panelBase）。
        // 顺序即代价：每个候选最多 POLL_TIMEOUT_MS，所以 https 在前、明文 http 垫底。
        internal static string[] PanelTrafficUrls(string host, string id, string preferredBase)
        {
            var list = new List<string>();
            if (string.IsNullOrEmpty(id)) return list.ToArray();
            string escapedId = Uri.EscapeDataString(id);
            Action<string> add = baseUrl =>
            {
                string u = baseUrl.TrimEnd('/') + "/panel/pub/traffic/" + escapedId;
                if (!list.Contains(u)) list.Add(u);
            };
            if (!string.IsNullOrEmpty(host))
            {
                // 443 优先，1111 更容易被干扰（更新清单走同一套候选，顺序保持一致）。
                add("https://" + host + "/clay");
                add("https://" + host);
                add("https://" + host + ":1111/clay");
                add("https://" + host + ":1111");
                add("http://" + host + ":1111/clay");
            }
            add(PANEL_BASE);
            if (!string.IsNullOrEmpty(preferredBase))   // 上次命中的地址排最前，省掉整轮试探
            {
                string prefer = preferredBase.TrimEnd('/') + "/panel/pub/traffic/" + escapedId;
                int i = list.IndexOf(prefer);
                if (i > 0) { list.RemoveAt(i); list.Insert(0, prefer); }
                if (i < 0) list.Insert(0, prefer);
            }
            return list.ToArray();
        }

        // 已连接时走本地 http 入站：面板端口（1111）在国内常被干扰，直连必然查不到到期；
        // 未连接则直连，避免 IE 里残留的代理设置把请求绕到不存在的通道上。
        private static IWebProxy PollProxy(bool connected)
        {
            return connected ? new WebProxy(LOCAL_HTTP_PROXY, false) : null;
        }

        // HttpWebRequest.Timeout 管不住 DNS/TCP 连接阶段（SYN 被丢包时能卡几十秒），
        // 这里用 BeginGetResponse + WaitOne 强制硬超时，超时即 Abort。
        private static string HttpGet(string url, int timeoutMs, bool viaLocalProxy)
        {
            HttpWebRequest req = null;
            try
            {
                req = (HttpWebRequest)WebRequest.Create(url);
                req.Method = "GET";
                req.Timeout = timeoutMs;
                req.ReadWriteTimeout = timeoutMs;
                req.KeepAlive = false;
                req.AllowAutoRedirect = true;
                req.Proxy = PollProxy(viaLocalProxy);
                req.ServerCertificateValidationCallback = (s, cert, chain, errors) => true;

                IAsyncResult ar = req.BeginGetResponse(null, null);
                if (!ar.AsyncWaitHandle.WaitOne(timeoutMs))
                {
                    // Abort 本身可能阻塞数百毫秒，扔到线程池，保证一轮探测的耗时可控
                    HttpWebRequest dead = req;
                    System.Threading.ThreadPool.QueueUserWorkItem(delegate { try { dead.Abort(); } catch { } });
                    LogToFile("[POLL] " + url + " -> 超时 " + timeoutMs + "ms");
                    return null;
                }
                using (var resp = req.EndGetResponse(ar))
                using (var sr = new StreamReader(resp.GetResponseStream(), Encoding.UTF8))
                    return sr.ReadToEnd();
            }
            catch (Exception ex)
            {
                if (req != null) { try { req.Abort(); } catch { } }
                LogToFile("[POLL] " + url + " -> " + ex.Message);
                return null;
            }
        }

        // 命中过的面板前缀持久化，下次启动直接复用；也允许运维手工写
        // HKCU\Software\XPanel\panel_base 指定中转/反代地址。
        private void SavePanelBase(string baseUrl)
        {
            try
            {
                using (var k = Registry.CurrentUser.CreateSubKey(REG_KEY))
                    if (k != null) k.SetValue("panel_base", baseUrl);
            }
            catch { }
        }

        private void LoadPanelBase()
        {
            try
            {
                using (var k = Registry.CurrentUser.OpenSubKey(REG_KEY))
                {
                    if (k != null)
                    {
                        string v = (string)k.GetValue("panel_base", "");
                        if (!string.IsNullOrEmpty(v)) _panelBase = v.TrimEnd('/');
                    }
                }
            }
            catch { }
        }

        // 依次试候选地址，返回第一份 success:true 的响应体以及命中的面板前缀；全失败返回 null。
        // reachable = 至少有一个候选返回了 HTTP 响应（即使 success:false），用来区分
        // 「面板不通」和「面板通了但这台机器上没有这个 uuid」，两种情况界面上要能看出区别。
        internal static string FetchPanelJson(string host, string id, string preferredBase,
                                              Func<string, string> fetch, out string hitBase)
        {
            bool reachable;
            return FetchPanelJson(host, id, preferredBase, fetch, out hitBase, out reachable);
        }

        internal static string FetchPanelJson(string host, string id, string preferredBase,
                                              Func<string, string> fetch, out string hitBase, out bool reachable)
        {
            hitBase = null;
            reachable = false;
            foreach (string url in PanelTrafficUrls(host, id, preferredBase))
            {
                string got = fetch(url);
                if (string.IsNullOrEmpty(got)) continue;
                reachable = true;
                if (got.IndexOf("\"success\":true", StringComparison.Ordinal) < 0) continue;
                hitBase = url.Substring(0, url.IndexOf("/panel/pub/traffic/", StringComparison.Ordinal));
                return got;
            }
            return null;
        }

        // 到期文案。语义必须分清楚：0 = 面板真的不限（长期有效），
        // null / 缺字段 / 解析失败 = 没拿到数据（不能冒充「长期有效」），
        // 负值或 expiryPending = 「首连后 N 天」尚未激活。
        private static string ExpireLine(object expiryTime, bool expiryPending, out Color color)
        {
            long exp;
            color = ColorTranslator.FromHtml("#7a7a82");
            if (!TryParseExpiry(expiryTime, out exp)) return "到期: 未获取";

            if (exp < 0)
            {
                // 旧面板原样透传负值：-ms 即「首连后 N 天」，换算成未来时间点再报天数。
                // 个别版本把负值写成秒（-2592000）；面板只按「天」下发，绝对值不足 1 天
                // 毫秒(86400000)的负值必然是秒，不归一会把 30 天算成 43 分钟。
                exp = -exp;
                if (exp < 86400000) exp *= 1000;
                string pd = Math.Ceiling(exp / 86400000.0).ToString("0", CultureInfo.InvariantCulture);
                return "到期: 未激活 (首连起" + pd + "天)";
            }
            if (exp > 1e11) exp /= 1000;          // 毫秒 → 秒
            if (exp > 253402300799L) return "到期: 未获取";
            if (exp == 0)
            {
                color = ColorTranslator.FromHtml("#7a7a82"); // 长期有效 / 0: 灰色
                return "到期: 长期有效";
            }

            DateTime t = new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc).AddSeconds(exp);
            DateTime now = DateTime.UtcNow;
            string day = t.ToLocalTime().ToString("yyyy/MM/dd");
            if (expiryPending)
            {
                int left = (int)Math.Ceiling((t - now).TotalDays);
                if (left < 0) left = 0;
                return "到期: 未激活 (首连起" + left.ToString(CultureInfo.InvariantCulture) + "天)";
            }
            // 过期必须按「时间点」判断：按整天截断会把到期后 12 小时算成 0 天，
            // 界面上显示「今天 (0天)」，看起来像还没到期，而面板已经停用了这个账号。
            TimeSpan leftSpan = t - now;
            if (leftSpan <= TimeSpan.Zero)
            {
                color = ColorTranslator.FromHtml("#757575"); // 已过期: 暗灰
                return "到期: 已过期 (" + day + ")";
            }
            if (leftSpan.TotalHours < 24)         // 最后一天报小时，避免「0天」歧义
            {
                int hours = (int)Math.Ceiling(leftSpan.TotalHours);
                if (hours < 1) hours = 1;
                color = ColorTranslator.FromHtml("#ef5350");
                return "到期: " + day + " (" + hours.ToString(CultureInfo.InvariantCulture) + "小时)";
            }
            int days = (int)leftSpan.TotalDays;
            if (days > 7)
            {
                color = ColorTranslator.FromHtml("#06d6a0"); // >7天: 绿色
            }
            else if (days > 3)
            {
                color = ColorTranslator.FromHtml("#ffb74d"); // 4~7天: 橙色
            }
            else
            {
                color = ColorTranslator.FromHtml("#ef5350"); // <=3天: 红色
            }
            return "到期: " + t.ToLocalTime().ToString("yyyy/MM/dd") + " (" + days + "天)";
        }

        // ---------- 运行日志 ----------

        internal static string LogFilePath
        {
            get { return Path.Combine(Xray.BinDir, "xpanel.log"); }
        }

        private void ShowLogWindow()
        {
            try
            {
                string path = LogFilePath;
                string text = "";
                if (File.Exists(path))
                {
                    var lines = File.ReadAllLines(path, Encoding.UTF8);
                    int start = Math.Max(0, lines.Length - 400);
                    var sb = new StringBuilder();
                    for (int i = start; i < lines.Length; i++) sb.AppendLine(lines[i]);
                    text = sb.ToString();
                }
                else text = "（日志文件还不存在：" + path + "）";
                using (var f = new LogForm(text, path)) f.ShowDialog(this);
            }
            catch (Exception ex)
            {
                MessageBox.Show("读取日志失败：" + ex.Message + "\n\n路径：" + LogFilePath,
                    "运行日志", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
        }

        // ---------- 检查更新 ----------

        // 当前节点的域名：自动更新拼镜像候选地址用（与流量轮询同一套候选）。
        private string NodeHost()
        {
            try
            {
                if (_node != null && !string.IsNullOrEmpty(_node.address)) return _node.address;
                return LinkParser.HostOf(_edit.Text);
            }
            catch { return ""; }
        }

        internal static MainForm Instance;

        // 给「发现新版本」这类状态一个固定落点：底部速率栏每 2 秒被轮询重写，写那里会立刻消失。
        // 导入页没有 _lblUpdate（那是仪表盘的），自动更新又主要发生在启动时还停在导入页的情况下，
        // 所以这里顺带把提示写到导入页的提示行，否则「正在更新」对用户完全不可见。
        internal static void SetUpdateHint(string s)
        {
            var f = Instance;
            if (f == null) return;
            try { f.SetText(f._lblUpdate, s); } catch { }
            try
            {
                if (f._page == 1 && f._lblTip != null)
                {
                    // 登录错误优先级最高：更新检查每 3–60 秒重试一次，绝不能把红字覆盖成
                    // 「自动更新检查失败」——那正是用户看到「点登录没反应」的根因。
                    if (f._loginFailed) return;
                    // 只允许更新线程覆盖它自己写的文本，别抢用户/登录的提示。
                    if (!f._tipIsUpdate && s.Length > 0 && f._lblTip.Text.Length > 0) return;
                    f._tipIsUpdate = s.Length > 0;
                    f._lblTip.ForeColor = Theme.C(Theme.ACCENT);
                    f.SetText(f._lblTip, s);
                }
            }
            catch { }
        }

        private void HandleCheckUpdate()
        {
            string old = _btnCheck.Text;
            _btnCheck.Enabled = false;
            _btnCheck.Text = "检查中…";
            Task.Run(() =>
            {
                string msg;
                Updater.State st = Updater.CheckNow(_panelBase, NodeHost, out msg);
                try
                {
                    BeginInvoke(new Action(() =>
                    {
                        _btnCheck.Text = old;
                        _btnCheck.Enabled = true;
                        if (st == Updater.State.Found)
                        {
                            var r = MessageBox.Show("发现新版本 " + Updater.LatestVersion + "，立即下载并重启客户端？",
                                "检查更新", MessageBoxButtons.YesNo, MessageBoxIcon.Question);
                            if (r == DialogResult.Yes)
                            {
                                _lblVer3.Text = "正在下载 " + Updater.LatestVersion + "…";
                                Task.Run(() =>
                                {
                                    string m2;
                                    Updater.State s2 = Updater.Install(_panelBase, NodeHost, out m2);
                                    try { BeginInvoke(new Action(() => _lblVer3.Text = "更新失败: " + m2)); } catch { }
                                });
                            }
                        }
                        else
                        {
                            _lblVer3.Text = "当前版本 " + VER + " · " + msg;
                            if (st == Updater.State.Failed)
                                MessageBox.Show(msg, "检查更新失败", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                        }
                    }));
                }
                catch { }
            });
        }

        private void SetExpireLabel(string text, Color color)
        {
            try
            {
                if (InvokeRequired)
                {
                    if (!IsHandleCreated) return;
                    BeginInvoke(new Action<string, Color>(SetExpireLabel), text, color);
                    return;
                }
                _lblExpire.Text = text;
                _lblExpire.ForeColor = color;
            }
            catch (Exception ex) { LogToFile("[UI] " + ex.Message); }
        }

        private void SetText(TransparentLabel l, string text)
        {
            try
            {
                if (InvokeRequired)
                {
                    if (!IsHandleCreated) return;
                    BeginInvoke(new Action<TransparentLabel, string>(SetText), l, text);
                    return;
                }
                l.Text = text;
            }
            catch (Exception ex) { LogToFile("[UI] " + ex.Message); }
        }

        private static string FmtBytes(long n)
        {
            if (n >= 1L << 30) return string.Format(CultureInfo.InvariantCulture, "{0:F2} GB", (double)n / (1L << 30));
            if (n >= 1L << 20) return string.Format(CultureInfo.InvariantCulture, "{0:F2} MB", (double)n / (1L << 20));
            if (n >= 1L << 10) return string.Format(CultureInfo.InvariantCulture, "{0:F2} KB", (double)n / (1L << 10));
            return n + " B";
        }

        private class TrafficResp
        {
            public bool success;
            public TrafficObj obj;
        }
        private class TrafficObj
        {
            public long up, down, total;
            public object expiryTime;
            public bool expiryPending;   // 面板标记「首连后 N 天」尚未激活（见 GetPubClientView）
        }

        private static bool TryParseExpiry(object v, out long ms)
        {
            ms = 0;
            if (v == null) return false;
            // JavaScriptSerializer 按 JSON 类型给出 Int64 / Int32 / double / string，统一走文本再解析。
            string s = Convert.ToString(v, CultureInfo.InvariantCulture);
            if (string.IsNullOrEmpty(s)) return false;
            if (long.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out ms)) return true;
            double d;
            if (double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out d) &&
                d > -9.2e18 && d < 9.2e18)
            {
                ms = (long)Math.Round(d);
                return true;
            }
            return false;
        }

        // ---------- 日志 ----------

        private static readonly object _logLock = new object();
        internal static void LogToFile(string msg)
        {
            try
            {
                lock (_logLock)
                {
                    string dir = Xray.BinDir;
                    string path = Path.Combine(dir, "xpanel.log");
                    // 长期运行会累积到几百 MB（轮询/点击诊断都写这里），超过 2MB 只保留最后 2000 行。
                    try
                    {
                        var fi = new FileInfo(path);
                        if (fi.Exists && fi.Length > 2 * 1024 * 1024)
                        {
                            string[] all = File.ReadAllLines(path, Encoding.UTF8);
                            int keep = Math.Min(all.Length, 2000);
                            var sb = new StringBuilder();
                            for (int i = all.Length - keep; i < all.Length; i++) sb.AppendLine(all[i]);
                            File.WriteAllText(path, sb.ToString(), Encoding.UTF8);
                        }
                    }
                    catch { }
                    File.AppendAllText(path,
                        DateTime.Now.ToString("HH:mm:ss.fff") + " " + msg + Environment.NewLine,
                        Encoding.UTF8);
                }
            }
            catch { }
        }
    }
}
