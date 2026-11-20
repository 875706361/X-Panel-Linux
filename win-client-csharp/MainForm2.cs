// 主窗口（第二部分：控件构建与样式）
using System;
using System.Collections.Generic;
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
                ForeColor = fore
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
                Size = new Size(S(w), S(h))
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
                ShortcutsEnabled = true
            };
            Controls.Add(_edit);
            _edit.SetBounds(S(LM) + 1, S(EDIT_Y) + 1, S(LCW) - 2, S(EDIT_H) - 2);

            _edit.TextChanged += (s, e) => { if (_lblTip.Text != "") _lblTip.Text = ""; };
            _edit.Enter += (s, e) => { _editFocused = true; InvalidateInput(); };
            _edit.Leave += (s, e) => { _editFocused = false; InvalidateInput(); };

            _btnLogin = MkButton("登录节点", LM, BTN1_Y, LCW, BTN1_H);
            _btnLogin.Click += (s, e) => HandleLogin();

            _lblTip = MkLabel("", LM, TIP_Y, LCW, TIP_H, FSub, Theme.C(Theme.DANGER));

            _page1.AddRange(new Control[] { _lblTitle1, _lblSub1, _edit, _btnLogin, _lblTip });
        }

        // page2：状态 / 到期 / 累计流量 / 上下载 / 启动加速 / 更换节点
        private void BuildPage2()
        {
            _lblStatus = MkLabel("未连接", LX0 + 14, STATUS_Y, LXW / 2 - 14, STATUS_H, FStat, Theme.C(Theme.IDLE));
            _lblExpire = MkLabel("到期: --", LX0 + LXW / 2, STATUS_Y, LXW / 2, STATUS_H, FStat, Theme.C(Theme.ACCENT));
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
            _btnSettings = MkButton("设置", LM + LCW - 64, FOOTER_Y - 2, 64, 24);
            _btnSettings.Click += (s, e) => SwitchPage(3);

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
                _btnConn, _btnSwitch, _lblFooter, _btnSettings,
                _btnModeBypass, _btnModeGlobal
            });
        }

        // page3：设置 —— 本地监听端口展示与重置
        private void BuildPage3()
        {
            _lblTitle3 = MkLabel("设置", LM, P1TITLE_Y, LCW, P1TITLE_H, FTitle, Theme.C(Theme.TXT));
            _lblSetSocks = MkLabel("Socks5 代理\n127.0.0.1:10808（支持 UDP）", LX0, 120, LXW, 64, FStat, Theme.C(Theme.TXT));
            _lblSetHttp = MkLabel("Http 代理\n127.0.0.1:10809", LX0, 200, LXW, 64, FStat, Theme.C(Theme.TXT));
            _lblSetHint = MkLabel("在支持代理的软件中手动填写以上地址即可使用，需处于已连接状态。", LX0, 276, LXW, 40, FSub, Theme.C(Theme.FAINT));
            
            _btnReset = MkButton("重置全部设置", LM, BTN2_Y, LCW, BTN2_H);
            _btnReset.Click += (s, e) => HandleReset();

            _btnBack = MkButton("返回", LM, BTN3_Y, LCW, BTN3_H);
            _btnBack.Click += (s, e) => SwitchPage(2);
            _page3.AddRange(new Control[] { _lblTitle3, _lblSetSocks, _lblSetHttp, _lblSetHint, _btnReset, _btnBack });
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
                try { _xray = Xray.Start(_node.proto == "hy2" ? Xray.BuildHy2Config(_node, bypass) : Xray.BuildConfig(_node, bypass), _node.proto); }
                catch (Exception ex) { LogToFile("[XRAY] " + ex.Message); }
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
        }

        private void SwitchPage(int p)
        {
            if (InvokeRequired) { Invoke(new Action<int>(SwitchPage), p); return; }
            _page = p;
            foreach (var c in _page1) c.Visible = (p == 1);
            foreach (var c in _page2) c.Visible = (p == 2);
            foreach (var c in _page3) c.Visible = (p == 3);
            Invalidate();
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
                        if (!string.IsNullOrEmpty(s)) _edit.Text = s;
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
