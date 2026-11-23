// 主窗口（第二部分：控件构建与样式）
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using System.Drawing;
using System.Linq;
using System.Windows.Forms;
using Microsoft.Win32;

namespace XPanelWin
{
    internal partial class MainForm
    {
        private TransparentLabel MkLabel(string text, int x, int y, int w, int h, Font f, Color fore)
        {
            var l = new TransparentLabel
            {
                Text = text,
                Location = new Point(S(x), S(y)),
                Size = new Size(S(w), S(h)),
                Font = f,
                ForeColor = fore,
                // 建出来先藏着，由 SwitchPage 统一显示当前页。
                // 否则 OnLoad 构建期间窗口已经显示，首帧会把三页控件全画出来（叠页闪一下甚至有
                // 用户能看到停在那里）；初始化一旦异常，这个状态就永久留下。
                Visible = false
            };
            Controls.Add(l);
            return l;
        }

        private FlatButton MkButton(string text, int x, int y, int w, int h)
        {
            var b = new FlatButton
            {
                Text = text,
                Location = new Point(S(x), S(y)),
                Size = new Size(S(w), S(h)),
                Visible = false
            };
            Controls.Add(b);
            return b;
        }

        // page1：标题 / 副标题 / 节点输入框 / 登录按钮 / 错误提示（Go v7.2.0 同构，无粘贴清空按钮）
        private void BuildPage1()
        {
            _lblTitle1 = MkLabel("导入节点配置", LM, P1TITLE_Y, LCW, P1TITLE_H, FTitle, Theme.C(Theme.TXT));
            _lblSub1 = MkLabel("粘贴节点链接，支持 vless / vmess / trojan / ss / hy2", LM, P1SUB_Y, LCW, P1SUB_H, FSub, Theme.C(Theme.FAINT));

            // 多行粘贴框：vless 链接含长 query，单行会截断滚动；Go 版即 ES_MULTILINE。
            // 高度由槽位决定（多行 EDIT 可自由设高），四周各内缩 1px 给父窗自绘描边让位。
            _edit = new BorderedEdit
            {
                Font = FEdit,
                Multiline = true,
                ForeColor = Theme.C(Theme.TXT),
                BackColor = Color.White,
                ShortcutsEnabled = true,
                Visible = false,   // 同 MkLabel：由 SwitchPage 显示，避免首帧叠页
                // 空框里给一行水印：168px 高的白框什么都不写时，用户会以为控件没渲染出来。
                Placeholder = "在此粘贴节点链接，例如 vless://uuid@host:port?type=tcp&security=reality"
            };
            Controls.Add(_edit);
            // 白底圆角框由窗口自绘（PaintPage1），TextBox 内缩出左右 10 / 上 8 / 下 6 的内边距；
            // 这样光标、水印、粘贴进去的链接都不会贴着边框。
            _edit.SetBounds(
                S(LM) + S(EDIT_PAD_X),
                S(EDIT_Y) + S(EDIT_PAD_TOP),
                S(LCW) - S(EDIT_PAD_X) * 2,
                S(EDIT_H) - S(EDIT_PAD_TOP) - S(EDIT_PAD_BOTTOM));

            _edit.TextChanged += (s, e) =>
            {
                if (_lblTip.Text != "") _lblTip.Text = "";
                _loginFailed = false;
                _tipIsUpdate = false;
                RestoreLoginButton();   // 用户改了链接，红色「链接无效」立刻恢复
                InvalidateInput();
            };
            _edit.Enter += (s, e) => { _editFocused = true; InvalidateInput(); };
            _edit.Leave += (s, e) => { _editFocused = false; InvalidateInput(); };
            // 快捷键兜底：Ctrl+Enter 直接登录（多行输入框里回车只换行）。
            _edit.KeyDown += (s, e) =>
            {
                if (e.Control && (e.KeyCode == Keys.Enter || e.KeyCode == Keys.Return))
                {
                    e.SuppressKeyPress = true;
                    if (!LoginRecentlyHandled()) HandleLogin();
                }
            };

            _btnLogin = MkButton("登录节点", LM, BTN1_Y, LCW, BTN1_H);
            // 与消息过滤器共用防抖：任何一个入口执行后 500ms 内另一个跳过。
            _btnLogin.Click += (s, e) => { if (!LoginRecentlyHandled()) HandleLogin(); };

            _lblTip = MkLabel("", LM, TIP_Y, LCW, TIP_H, Fonts.Ui(13, FontStyle.Bold), Theme.C(Theme.DANGER));

            // 导入页页脚：钉在状态栏上方（NOTE_Y），与主体之间用一条细分隔线（DIV1_Y，见 PaintPage1），
            // 这样按钮以下的留白是「排版留白」而不是「没填满」。
            _lblNote1 = MkLabel("链接只保存在本机注册表，不会上传；已保存后启动直接进入仪表盘",
                LM, NOTE_Y, LCW, NOTE_H, FSub, Theme.C(Theme.FAINT));
            _lblNote1.TextAlign = ContentAlignment.MiddleCenter;

            _page1.AddRange(new Control[] { _lblTitle1, _lblSub1, _edit, _btnLogin, _lblTip, _lblNote1 });
        }

        // page2：状态 / 到期 / 累计流量 / 上下载 / 启动加速 / 更换节点
        private void BuildPage2()
        {
            _lblStatus = MkLabel("未连接", LX0 + 14, STATUS_Y, 116, STATUS_H, FStat, Theme.C(Theme.IDLE));
            // 到期文案最长是「到期: 未激活 (首连起30天)」，右半格 185px 在雅黑 Bold 14px 下会贴边被裁，
            // 状态字串最多 7 个字（正在连接...），把中线左移给它让出宽度。
            _lblExpire = MkLabel("到期: --", LX0 + 130, STATUS_Y, LXW - 130, STATUS_H, FStat, Theme.C(Theme.ACCENT));
            _lblExpire.TextAlign = ContentAlignment.MiddleRight;
            _lblTotal = MkLabel("累计已用流量", LX0, TOTAL_Y, LXW, TOTAL_H, FSub, Theme.C(Theme.MUTED));
            _lblTraffic = MkLabel("0.00 MB", LX0, TRAFFIC_Y, LXW, TRAFFIC_H, FVal, Theme.C(Theme.TXT));
            _lblUpLbl = MkLabel("↑ 累计上传", LX0, COL_LBL_Y, LXW / 2, COL_LBL_H, FSub, Theme.C(Theme.MUTED));
            _lblUp = MkLabel("0.00 MB", LX0, COL_VAL_Y, LXW / 2, COL_VAL_H, FBig, Theme.C(Theme.TXT));
            _lblDownLbl = MkLabel("↓ 累计下载", LX0 + LXW / 2, COL_LBL_Y, LXW / 2, COL_LBL_H, FSub, Theme.C(Theme.MUTED));
            _lblDown = MkLabel("0.00 MB", LX0 + LXW / 2, COL_VAL_Y, LXW / 2, COL_VAL_H, FBig, Theme.C(Theme.TXT));

            _btnConn = MkButton("启动加速", LM, BTN2_Y, LCW, BTN2_H);
            _btnConn.Click += (s, e) => Task.Run(() => ToggleConnect());

            _btnSwitch = MkButton("更换节点", LM, BTN3_Y, LCW, BTN3_H);
            _btnSwitch.Click += (s, e) =>
            {
                if (_connected) StopProxy();
                SwitchPage(1);
            };

            _lblFooter = MkLabel("系统代理 · 本地 Socks 10808 / Http 10809", LM, FOOTER_Y, LCW - 74, FOOTER_H, FSub, Theme.C(Theme.FAINT));
            // 同 _lblSpeed：底部条用不透明底色，避免非整数缩放下透明回填错位露黑底。
            _lblFooter.BackColor = Theme.C(Theme.BASE);
            _btnSettings = MkButton("设置", LM + LCW - 64, FOOTER_Y - 2, 64, 24);
            _btnSettings.Click += (s, e) => SwitchPage(3);

            // 更新提示单独占一行：底部状态栏每 2 秒被轮询改写，挂在那里等于立刻被抹掉。
            _lblUpdate = MkLabel("", LM, FOOTER_Y + 24, LCW, 18, FSub, Theme.C(Theme.ACCENT));
            _lblUpdate.TextAlign = ContentAlignment.MiddleCenter;

            // 模式分段：卡片内底部两枚并排，各占半宽减 4px 中缝
            int half = (LXW - 8) / 2;
            _btnModeBypass = MkButton("绕过大陆", LX0, MODE_Y, half, MODE_H);
            _btnModeGlobal = MkButton("全局", LX0 + half + 8, MODE_Y, half, MODE_H);
            _btnModeBypass.Font = FStat;
            _btnModeGlobal.Font = FStat;
            _btnModeBypass.Click += (s, e) => SetMode(true);
            _btnModeGlobal.Click += (s, e) => SetMode(false);

            _page2.AddRange(new Control[] {
                _lblStatus, _lblExpire, _lblTotal, _lblTraffic,
                _lblUpLbl, _lblUp, _lblDownLbl, _lblDown,
                _btnConn, _btnSwitch, _lblFooter, _btnSettings, _lblUpdate,
                _btnModeBypass, _btnModeGlobal
            });
        }

        // page3：设置 —— 本地监听端口展示与重置
        private void BuildPage3()
        {
            _lblTitle3 = MkLabel("设置", LM, P1TITLE_Y, LCW, P1TITLE_H, FTitle, Theme.C(Theme.TXT));
            _lblSetSocks = MkLabel("Socks5 代理\n127.0.0.1:10808（支持 UDP）", LX0, 110, LXW, 64, FStat, Theme.C(Theme.TXT));
            _lblSetHttp = MkLabel("Http 代理\n127.0.0.1:10809", LX0, 180, LXW, 64, FStat, Theme.C(Theme.TXT));
            _lblSetHint = MkLabel("在支持代理的软件中手动填写以上地址即可使用，需处于已连接状态。", LX0, 246, LXW, 36, FSub, Theme.C(Theme.FAINT));

            // 检查更新：发现新版先弹确认再装，避免像自动更新那样直接退出重启。
            _btnCheck = MkButton("检查更新", LM, 292, LCW, 44);
            _btnCheck.Click += (s, e) => HandleCheckUpdate();

            _btnReset = MkButton("重置全部设置", LM, 346, LCW, 44);
            _btnReset.Click += (s, e) => HandleReset();

            _btnBack = MkButton("返回", LM, 400, LCW, 44);
            _btnBack.Click += (s, e) => SwitchPage(2);

            _lblVer3 = MkLabel("当前版本 " + VER, LM, 462, LCW, 20, FSub, Theme.C(Theme.FAINT));
            _lblVer3.TextAlign = ContentAlignment.MiddleCenter;

            _page3.AddRange(new Control[] {
                _lblTitle3, _lblSetSocks, _lblSetHttp, _lblSetHint,
                _btnCheck, _btnReset, _btnBack, _lblVer3
            });
        }

        // 模式切换：运行中即时生效（重建配置拉起新内核），状态持久化到注册表
        private void SetMode(bool bypass)
        {
            _bypassCn = bypass;
            ApplyModeStyle();
            SaveMode(bypass);
            LogToFile("[MODE] " + (bypass ? "绕过大陆" : "全局"));
            if (_connected && _node != null)
            {
                Xray.Stop(_xray);
                _xray = null;
                try
                {
                    _xray = Xray.Start(_node.proto == "hy2" ? Xray.BuildHy2Config(_node, bypass) : Xray.BuildConfig(_node, bypass), _node.proto);
                }
                catch (Exception ex)
                {
                    LogToFile("[XRAY] " + ex.Message);
                    _connected = false;
                    SetSystemProxy(false, "");
                    SetConnButton(false);
                    ShowTip("切换模式失败：" + ex.Message, true);
                    return;
                }
                // 新内核起不来（配置被拒/内核秒退）就把连接状态收回去，别让"已连接"挂在死端口上
                Process pr = _xray;
                int gen; lock (_stateLock) gen = ++_connectGen;
                int waitMs = _node.proto == "hy2" ? 9000 : 3500;
                var th = new Thread(() =>
                {
                    string err;
                    bool ok = Xray.WaitReady(pr, 10809, waitMs, out err);
                    bool stale; lock (_stateLock) stale = (gen != _connectGen);
                    if (stale || ok) return;
                    Xray.Stop(pr);
                    LogToFile("[CONN] 切换模式后内核未就绪: " + err);
                    Post(() =>
                    {
                        _xray = null;
                        _connected = false;
                        SetSystemProxy(false, "");
                        SetConnButton(false);
                        SetFooterHint("✕ 切换模式失败：" + Xray.Friendly(err), true);
                        SetStatus("连接失败", Theme.C(Theme.DANGER));
                        Invalidate();
                    });
                });
                th.IsBackground = true;
                th.Start();
            }
        }

        // 选中态 accent 浅蓝底+accent 描边，未选中灰底（统一走 SetColors 触发重绘）
        private void ApplyModeStyle()
        {
            Color selFill = Color.FromArgb(0xE6, 0xF2, 0xFB), selBorder = Theme.C(Theme.ACCENT), selText = Theme.C(Theme.ACCENT);
            Color idlFill = Theme.C(0xF4F5F8), idlBorder = Theme.C(0xD0D5DD), idlText = Theme.C(0x5F5F5F);
            if (_bypassCn)
            {
                _btnModeBypass.SetColors(selFill, selFill, selFill, selBorder, selText);
                _btnModeGlobal.SetColors(idlFill, Theme.C(0xECEDF1), Theme.C(0xE2E4E9), idlBorder, idlText);
            }
            else
            {
                _btnModeGlobal.SetColors(selFill, selFill, selFill, selBorder, selText);
                _btnModeBypass.SetColors(idlFill, Theme.C(0xECEDF1), Theme.C(0xE2E4E9), idlBorder, idlText);
            }
        }

        private void LoadMode()
        {
            try
            {
                using (var k = Registry.CurrentUser.OpenSubKey(REG_KEY))
                {
                    if (k != null)
                    {
                        int v;
                        if (int.TryParse((string)k.GetValue("mode", "1"), out v)) _bypassCn = (v != 0);
                    }
                }
            }
            catch { }
            ApplyModeStyle();
        }

        private void SaveMode(bool bypass)
        {
            try
            {
                using (var k = Registry.CurrentUser.CreateSubKey(REG_KEY))
                {
                    if (k != null) k.SetValue("mode", bypass ? "1" : "0");
                }
            }
            catch { }
        }

        private void BuildBottomBar()
        {
            _lblSpeed = MkLabel("↑ 0 B/s  ↓ 0 B/s · 客户端 " + VER, LM, LWH - 27, LCW, 20, FMono, Theme.C(Theme.MUTED));
            // 底部条直接坐在窗口底色上：用不透明底色而不是 Transparent。
            // 透明 Label 依赖父窗口背景回填，缩放比例不是整数（125% / 150%）时
            // 回填区常错位一两像素，mono 上直接露出黑色底（Windows 上则是闪烁/残影）。
            _lblSpeed.BackColor = Theme.C(Theme.BASE);
            _lblSpeed.Visible = true;   // 常驻（不在任何分页列表里，开关页都要显示）
        }

        // Fluent 按钮样式表：accent 纯色三态（hover/press 单独一档）+ 次级灰
        private void ApplyStyle()
        {
            foreach (var b in new[] { _btnLogin, _btnConn })
            {
                b.SetColors(Theme.C(Theme.ACCENT), Theme.C(Theme.ACCENT_H),
                            Theme.C(Theme.ACCENT_P), Theme.C(Theme.ACCENT_P), Color.White);
                b.Font = FBtn;
            }
            _btnSwitch.SetColors(Theme.C(0xF4F5F8), Theme.C(0xECEDF1),
                                 Theme.C(0xE2E4E9), Theme.C(0xD0D5DD), Theme.C(0x2B2B2B));
            _btnSwitch.Font = FStat;
            _btnBack.SetColors(Theme.C(0xF4F5F8), Theme.C(0xECEDF1),
                               Theme.C(0xE2E4E9), Theme.C(0xD0D5DD), Theme.C(0x2B2B2B));
            _btnBack.Font = FStat;
            _btnReset.SetDangerStyle();
            _btnReset.Font = FStat;
            _btnSettings.SetColors(Theme.C(0xF4F5F8), Theme.C(0xECEDF1),
                                   Theme.C(0xE2E4E9), Theme.C(0xD0D5DD), Theme.C(0x5F5F5F));
            _btnSettings.Font = FSub;
            _btnCheck.SetColors(Theme.C(0xF4F5F8), Theme.C(0xECEDF1),
                                Theme.C(0xE2E4E9), Theme.C(0xD0D5DD), Theme.C(0x2B2B2B));
            _btnCheck.Font = FStat;
        }

        // 控件归属自检：除了常驻底部栏，每个 page 控件都必须挂在某个分页列表里。
        // 漏加列表的控件会永远可见（切页时没人隐藏它），是「叠页」最常见的成因。
        private void CheckPageLists()
        {
            try
            {
                var listed = new List<Control>();
                listed.AddRange(_page1);
                listed.AddRange(_page2);
                listed.AddRange(_page3);
                var permanent = new List<Control> { _lblFooter, _btnSettings, _lblSpeed, _lblUpdate };
                foreach (var f in GetType().GetFields(System.Reflection.BindingFlags.Instance |
                                                       System.Reflection.BindingFlags.NonPublic))
                {
                    var c = f.GetValue(this) as Control;
                    if (c == null) continue;
                    // 只看我们自己命名的控件字段；TextBox 基类里的 hscrollbar/vscrollbar 之类不算。
                    if (!f.Name.StartsWith("_")) continue;
                    if (permanent.Contains(c) || listed.Contains(c)) continue;
                    LogToFile("[UI] 控件未归页: " + f.Name + " (" + c.Text + ")");
                }
            }
            catch { }
        }

        private void SwitchPage(int p)
        {
            if (InvokeRequired) { Invoke(new Action<int>(SwitchPage), p); return; }
            _page = p;
            try
            {
                foreach (var c in _page1) c.Visible = (p == 1);
                foreach (var c in _page2) c.Visible = (p == 2);
                foreach (var c in _page3) c.Visible = (p == 3);
            }
            catch (Exception ex)
            {
                // 单个控件设置失败不该让整页切换中断（那会把三页都留在可见状态，直接叠页）。
                LogToFile("[UI] 切页失败: " + ex.Message);
                try { HideAllPageControls(); } catch { }
                foreach (var c in PageList(p)) { try { c.Visible = true; } catch { } }
            }
            CheckSinglePage(p);
            Invalidate();
        }

        private List<Control> PageList(int p)
        {
            if (p == 1) return _page1;
            if (p == 2) return _page2;
            if (p == 3) return _page3;
            return new List<Control>();
        }

        private void HideAllPageControls()
        {
            foreach (var c in _page1) c.Visible = false;
            foreach (var c in _page2) c.Visible = false;
            foreach (var c in _page3) c.Visible = false;
        }

        // 叠页自检：任何时刻只应有一页的控件可见。命中就写日志，便于用户回传定位。
        private void CheckSinglePage(int p)
        {
            try
            {
                List<Control> want = PageList(p);
                int visible = 0;
                foreach (var c in _page1) if (c.Visible) visible++;
                foreach (var c in _page2) if (c.Visible) visible++;
                foreach (var c in _page3) if (c.Visible) visible++;
                if (visible != want.Count)
                    LogToFile("[UI] 叠页告警: page=" + p + " 期望 " + want.Count + " 个可见，实际 " + visible);
            }
            catch { }
        }

        private void LoadSavedLink()
        {
            try
            {
                using (var k = Registry.CurrentUser.OpenSubKey(REG_KEY))
                {
                    if (k != null)
                    {
                        string s = (string)k.GetValue("link", "");
                        if (!string.IsNullOrEmpty(s))
                        {
                            _edit.Text = s;
                            // 不要把整条链接默认全选（蓝底一片，还容易误删）：光标落到末尾即可。
                            try { _edit.SelectionStart = _edit.TextLength; _edit.SelectionLength = 0; } catch { }
                        }
                    }
                }
            }
            catch { }
        }

        private void SaveLink(string link)
        {
            try
            {
                using (var k = Registry.CurrentUser.CreateSubKey(REG_KEY))
                {
                    if (k != null) k.SetValue("link", link);
                }
            }
            catch { }
        }
    }
}
