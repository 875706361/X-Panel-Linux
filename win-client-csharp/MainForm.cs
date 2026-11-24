// X-Panel Windows 客户端 v10.0 — 主窗口（第一部分：窗口骨架与字段）
// 450x560、Fluent 蓝三态纯色按钮、自绘标题栏；v10 四协议导入 + 仪表盘重排。
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Text;
using System.IO;
using System.Linq;
using System.Net;
using System.Text;
using System.Threading.Tasks;
using System.Windows.Forms;
using Microsoft.Win32;

namespace XPanelWin
{
    internal partial class MainForm : Form, IMessageFilter
    {
        // 96dpi 逻辑设计稿：与 Go v7.2.0 的栅格一比一（450x560）。
        const int LWW = 450, LWH = 560;
        const int LTITLE = 48, LBAR = 36;
        const int LM = 22;
        const int LCW = LWW - 2 * LM;
        const int LP = 18;
        const int LX0 = LM + LP;
        const int LXW = LCW - 2 * LP;

        // page1：v10.5 栅格 —— 标题紧贴标题栏、输入框加高到 160、协议徽标与按钮之间留足气口，
        // 提示行/说明文字各自独立成行（旧版 tip 376..398 与 note 396..416 有 2px 重叠）。
        // 底部说明改成「页脚」式：细分隔线 + 居中说明，钉在状态栏上方，
        // 让按钮以下的留白读起来是排版，而不是内容没填满。
        const int P1TITLE_Y = 62, P1TITLE_H = 28;
        const int P1SUB_Y = 96, P1SUB_H = 20;
        const int EDIT_Y = 126, EDIT_H = 200;
        // 输入框白底由窗口自绘，TextBox 本身往里内缩，做出真正的内边距：
        // 之前只内缩 1px，光标和水印紧贴左边框，视觉上像被裁掉半个字。
        const int EDIT_PAD_X = 10, EDIT_PAD_TOP = 8, EDIT_PAD_BOTTOM = 6;
        const int CHIP_Y = EDIT_Y + EDIT_H + 12, CHIP_H = 16;
        const int BTN1_Y = 364, BTN1_H = 48;
        const int TIP_Y = 418, TIP_H = 20;
        const int DIV1_Y = 458;
        const int NOTE_Y = 466, NOTE_H = 20;

        // 导入页栅格的硬约束：元素顺序固定、相邻块之间至少 4px、页脚不压底栏。
        // 上一版 tip(376..398) 与 note(396..416) 就叠了 2px——文字一多就糊成一片，
        // 所以做成可单测的判定，后面再调间距不会又把两块叠回去。
        internal static string CheckPage1Grid()
        {
            string[] names = { "title", "sub", "edit", "chips", "button", "tip", "note" };
            int[] tops = { P1TITLE_Y, P1SUB_Y, EDIT_Y, CHIP_Y, BTN1_Y, TIP_Y, NOTE_Y };
            int[] heights = { P1TITLE_H, P1SUB_H, EDIT_H, CHIP_H, BTN1_H, TIP_H, NOTE_H };
            for (int i = 0; i < names.Length; i++)
            {
                if (heights[i] <= 0) return names[i] + " height <= 0";
                if (i > 0 && tops[i] < tops[i - 1] + heights[i - 1] + 4)
                    return names[i] + " overlaps or crowds " + names[i - 1];
            }
            if (NOTE_Y + NOTE_H > LWH - LBAR - 8) return "note runs into the bottom bar";
            if (DIV1_Y <= TIP_Y + TIP_H || DIV1_Y >= NOTE_Y) return "footer divider out of place";
            return "";
        }

        // page2：卡片 62..302，仪表盘按 Go v7.2.0 栅格（v9.1 卡片内重排，底部让位模式分段）
        const int CARD2_Y = 62, CARD2_H = 240;
        const int STATUS_Y = 80, STATUS_H = 24;
        const int TOTAL_Y = 110, TOTAL_H = 18;
        const int TRAFFIC_Y = 128, TRAFFIC_H = 42;
        const int DIV_Y = 186;
        const int COL_LBL_Y = 196, COL_LBL_H = 18;
        const int COL_VAL_Y = 216, COL_VAL_H = 30;
        // 模式分段（卡片内底部）：绕过大陆 / 全局
        const int MODE_Y = 258, MODE_H = 32;
        const int BTN2_Y = 316, BTN2_H = 48;
        const int BTN3_Y = 374, BTN3_H = 48;
        const int FOOTER_Y = 440, FOOTER_H = 20;

        internal const string VER = "v10.5.20";

        // 字体与槽位配对自检。两条规则，都不依赖当前系统装了什么字体：
        //  1) 单行行高按「字号 × 1.35」估算——雅黑实测 1.32、宋体 1.18，按最差情况留量，
        //     保证没装雅黑的机器上也不会把字裁掉半截。
        //  2) 关键文案必须能在一行内放下（Label 不换行，超了直接截断）。
        // 改字号或栅格后跑 win-client-csharp/tests/run.sh（FontTests 会调它）。
        internal static string CheckFontLayout()
        {
            Fonts.Init(Fonts.Scale);   // 未初始化时切成 1.0，保证宽度测量有字体可用
            const double MAX_LINE_RATIO = 1.35;
            string[] names = {
                "导入页标题 20px", "副标题 13px", "协议 chips 11px", "提示行 13px", "页脚说明 13px",
                "仪表盘状态 14px", "累计流量 30px", "上传/下载 22px", "模式按钮 14px", "底部速率 11px"
            };
            int[] px = { 20, 13, 11, 13, 13, 14, 30, 22, 14, 11 };
            int[] slots = { P1TITLE_H, P1SUB_H, CHIP_H, TIP_H, NOTE_H, STATUS_H, TRAFFIC_H, COL_VAL_H, MODE_H, 20 };
            for (int i = 0; i < px.Length; i++)
            {
                int need = (int)Math.Ceiling(px[i] * MAX_LINE_RATIO);
                if (need > slots[i])
                    return names[i] + " 需要 " + need + "px 高，槽位只有 " + slots[i];
            }
            // 关键文案必须能在一行内放下（Label 不会自动换行，超了就截断）
            try
            {
                using (var g = Graphics.FromHwnd(IntPtr.Zero))
                {
                    string sub = "粘贴节点链接，支持 vless / vmess / trojan / ss / hy2";
                    int w = TextRenderer.MeasureText(g, sub, Fonts.Ui(13), new Size(10000, 100),
                        TextFormatFlags.NoPadding).Width;
                    if (w > LCW) return "副标题宽度 " + w + " > 内容宽 " + LCW;
                }
            }
            catch { }   // 无图形设备（纯 CI 容器）时跳过宽度检查
            return "";
        }

        // 圆角两档：控件 8 / 卡片 12（Go v7.2.0 同值）
        internal const int R_CTRL = 8, R_CARD = 12;

        internal const string PANEL_BASE = "https://888888666.xyz:1111/clay";
        const string REG_KEY = "Software\\XPanel";

        internal static float DpiScale = 1f;

        private int _page = 1;
        private bool _connected, _busy;
        // 自动更新用来决定「运行期能不能装」和「能不能走本地代理拉清单」。
        internal bool IsConnected { get { return _connected; } }
        // 每次发起/取消连接都自增：异步就绪检查回来时若代数已变，说明用户已经断开或又点了
        // 一次，这次结果必须丢弃，否则会把系统代理又打开。
        private int _connectGen;
        private string _serverUuid = "";
        private string _panelBase = "";   // 最近一次命中 success:true 的面板前缀，轮询优先复用
        private NodeInfo _node;   // 当前登录节点（四协议统一）
        private System.Diagnostics.Process _xray;
        private long _lastUp, _lastDown, _speedUp, _speedDown;
        private readonly object _stateLock = new object();

        private TransparentLabel _lblTitle1, _lblSub1, _lblTip, _lblNote1;
        private BorderedEdit _edit;
        private bool _editFocused;
        private bool _loginFailed;   // 登录解析失败：输入框描边转红、提示行加粗
        internal bool _tipIsUpdate;  // 提示行当前显示的是更新状态（可被更新线程改写）
        private long _lastLoginHandled;   // 登录入口防抖：Click 与消息过滤器只执行一次
        private FlatButton _btnLogin;

        private TransparentLabel _lblStatus, _lblExpire, _lblTotal, _lblTraffic;
        private TransparentLabel _lblUpLbl, _lblUp, _lblDownLbl, _lblDown, _lblFooter, _lblUpdate;
        private FlatButton _btnConn, _btnSwitch, _btnSettings, _btnBack, _btnReset, _btnCheck, _btnLog, _btnLog2;
        // 粘贴完整链接后自动登录：绕开「按钮点不动」这种客户端环境问题。
        private System.Windows.Forms.Timer _autoLoginTimer;
        private bool _suppressAutoLogin;
        // page3 设置页
        private TransparentLabel _lblTitle3, _lblSetSocks, _lblSetHttp, _lblSetHint, _lblVer3;
        private TransparentLabel _lblSpeed;
        // 模式分段：两枚并排扁平按钮，选中态 accent 描边+浅蓝底
        private FlatButton _btnModeBypass, _btnModeGlobal;
        private bool _bypassCn = true;

        private readonly List<Control> _page1 = new List<Control>();
        private readonly List<Control> _page2 = new List<Control>();
        private readonly List<Control> _page3 = new List<Control>();

        // 标题栏自绘按钮状态
        private bool _hoverMin, _hoverClose, _pressMin, _pressClose;

        // 托盘图标：点叉隐藏到托盘，托盘菜单退出
        private NotifyIcon _tray;
        private bool _realExit, _trayTipShown;

        private static Font FAppTitle, FTitle, FSub, FEdit, FBtn, FStat, FVal, FBig, FMono;

        public MainForm()
        {
            Instance = this;
            // 布局的每个坐标都由 S() 手动乘 DpiScale。WinForms 默认 AutoScaleMode.Font 会在
            // 高 DPI 下再按字体度量缩放一遍控件（框 ×1.5²、字只 ×1.5），这就是「大框小字」的根。
            AutoScaleMode = AutoScaleMode.None;
            Text = "客户端";
            FormBorderStyle = FormBorderStyle.None;
            StartPosition = FormStartPosition.CenterScreen;
            ClientSize = new Size(S(LWW), S(LWH));
            BackColor = Theme.C(Theme.BASE);
            DoubleBuffered = true;
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer |
                     ControlStyles.ResizeRedraw | ControlStyles.UserPaint, true);
            Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath);
            MaximizeBox = false;
            MinimizeBox = false;
            // 登录按钮的兜底：部分 Windows 环境/缩放下 FlatButton 收不到 WM_LBUTTONUP，
            // 表现为「点登录节点完全没反应」。消息过滤器直接看鼠标位置，命中按钮就执行登录。
            Application.AddMessageFilter(this);
        }

        internal static int S(int v) => (int)Math.Round(v * DpiScale);

        protected override CreateParams CreateParams
        {
            get
            {
                CreateParams cp = base.CreateParams;
                cp.ClassStyle |= Native.CS_DROPSHADOW;
                return cp;
            }
        }

        protected override void OnLoad(EventArgs e)
        {
            base.OnLoad(e);
            // Win11 圆角（Go applyRoundCorners 同款）；dwmapi 缺失或 mono 下静默跳过
            try { int pref = 2; Native.DwmSetWindowAttribute(Handle, Native.DWMWA_WINDOW_CORNER_PREFERENCE, ref pref, 4); } catch { }

            // 构建与恢复分两段：任何一步抛异常都不能让界面停在「三页控件全部可见」的叠页状态
            // —— 用户看到的就是输入框、启动加速、登录节点、设置标题糊在同一屏。
            NodeInfo saved = null;
            try
            {
                BuildFonts();
                BuildPage1();
                BuildPage2();
                BuildPage3();
                BuildBottomBar();
                ApplyStyle();
                LoadSavedLink();
                LoadPanelBase();
                LoadMode();
                // 有已保存且能解析的节点时直接进仪表盘：老版本每次启动都停在导入页，
                // 把整条 vless 链接（含 uuid）摊在输入框里，既难看又得再点一次「登录节点」。
                try { saved = LinkParser.Parse(_edit.Text ?? ""); } catch { }
            }
            catch (Exception ex)
            {
                LogToFile("[INIT] 构建异常: " + ex);
            }
            CheckPageLists();

            // 不管上面成没成功，这里都先全部隐藏再显示目标页：叠页只在「切换从未执行」时出现，
            // 而 SwitchPage 之前控件默认都是可见的。
            try
            {
                HideAllPageControls();
                if (saved != null)
                {
                    _node = saved;
                    SwitchPage(2);
                    LogToFile("[INIT] 恢复上次节点 " + saved.proto + " " + saved.address + ":" + saved.port + "，进入仪表盘");
                }
                else
                {
                    SwitchPage(1);
                }
            }
            catch (Exception ex)
            {
                LogToFile("[INIT] 切页异常: " + ex);
                try { HideAllPageControls(); } catch { }
            }
            BuildTray();
            // 控件句柄就绪后再起轮询：直接在 OnLoad 里 Task.Run 时，跨线程 Invoke 可能落在
            // 句柄创建完成之前抛异常，异常被循环吞掉，界面就永远停在初始文案（"到期: 查询中"）。
            BeginInvoke(new Action(() =>
            {
                Task.Run(PollTrafficLoop);
                // 启动查一次（发现新版直接装），之后每 30 分钟复查。老版本只在 OnLoad 查一次，
                // 客户端常驻托盘好几天也不会升级。
                Task.Run(() => Updater.StartAutoLoop(_panelBase, NodeHost));
            }));

            // 上次替换脚本失败会留下这个文件：提示用户并清掉，避免永远重复同一个失败。
            try
            {
                string err = Path.Combine(Path.GetTempPath(), "xpanel-update-error.log");
                if (File.Exists(err))
                {
                    string info = File.ReadAllText(err).Trim();
                    File.Delete(err);
                    LogToFile("[UPDATE] 上次替换失败: " + info);
                    SetUpdateHint("上次自动更新失败（文件被占用），请在设置里手动检查更新");
                }
            }
            catch { }

            LogToFile("[INIT] " + VER + " 启动 窗口=" + S(LWW) + "x" + S(LWH) + " dpi=" + (int)(DpiScale * 96) + " scale=" + DpiScale.ToString("0.00"));
            LogToFile("[BTN_LAYOUT] 栅格规划: 窗口=" + LWW + "x" + LWH + ", 边距LM=" + LM + ", 统一宽度LCW=" + LCW);
            LogToFile("[BTN_LAYOUT] 按钮1 [登录节点]: 逻辑=(" + LM + "," + BTN1_Y + ") " + LCW + "x" + BTN1_H + " | 物理=(" + S(LM) + "," + S(BTN1_Y) + ") " + S(LCW) + "x" + S(BTN1_H));
            LogToFile("[BTN_LAYOUT] 按钮2 [启动加速]: 逻辑=(" + LM + "," + BTN2_Y + ") " + LCW + "x" + BTN2_H + " | 物理=(" + S(LM) + "," + S(BTN2_Y) + ") " + S(LCW) + "x" + S(BTN2_H));
            LogToFile("[BTN_LAYOUT] 按钮3 [更换节点]: 逻辑=(" + LM + "," + BTN3_Y + ") " + LCW + "x" + BTN3_H + " | 物理=(" + S(LM) + "," + S(BTN3_Y) + ") " + S(LCW) + "x" + S(BTN3_H));
            LogToFile("[BTN_LAYOUT] 标题栏控制 [最小化]: 宽=" + S(44) + " 高=" + S(LTITLE) + " | [关闭]: 宽=" + S(44) + " 高=" + S(LTITLE));
            LogToFile("[INIT] UI 就绪");
        }

        private void BuildFonts()
        {
            // 字号是「96dpi 逻辑像素」，不是 pt：见 Fonts.cs 顶部注释。
            Fonts.Init(DpiScale);
            FAppTitle = Fonts.Ui(14, FontStyle.Bold);
            FTitle = Fonts.Ui(20, FontStyle.Bold);
            FSub = Fonts.Ui(13);
            FEdit = Fonts.Ui(14);
            FBtn = Fonts.Ui(16, FontStyle.Bold);
            FStat = Fonts.Ui(14, FontStyle.Bold);
            FVal = Fonts.Ui(30, FontStyle.Bold);   // 累计流量大数字：30px×1.35=41 ≤ TRAFFIC_H(42)
            FBig = Fonts.Ui(22, FontStyle.Bold);   // 上传/下载，22px 行高 ~26 < COL_VAL_H(30)
            FMono = Fonts.Mono(11);
            LogToFile("[FONT] 族=" + Fonts.UiFamily + " 等宽=" + Fonts.MonoFamily +
                      " scale=" + Fonts.Scale.ToString("0.00") +
                      " 行高 标题=" + Fonts.LineHeight(FTitle) + " 副=" + Fonts.LineHeight(FSub) +
                      " 值=" + Fonts.LineHeight(FVal) + " 按钮=" + Fonts.LineHeight(FBtn));
        }

        // ---------- 托盘 ----------

        private void BuildTray()
        {
            var menu = new ContextMenuStrip();
            menu.Items.Add("打开主界面", null, (s, e) => ShowFromTray());
            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add("退出", null, (s, e) => ExitFromTray());

            _tray = new NotifyIcon
            {
                Icon = Icon,
                Text = "客户端 " + VER,
                ContextMenuStrip = menu,
                Visible = true
            };
            _tray.MouseClick += (s, e) => { if (e.Button == MouseButtons.Left) ShowFromTray(); };
        }

        private void ShowFromTray()
        {
            Show();
            WindowState = FormWindowState.Normal;
            Activate();
        }

        private void ExitFromTray()
        {
            _realExit = true;
            Close();
        }

        internal void ShowFromTrayPublic() { ShowFromTray(); }

        // 点叉到底算不算「退出」：停在导入页就真退出，其它页面（仪表盘/设置）收进托盘。
        // 单拎出来是为了能单测——「导入页点叉必须真的结束进程」是这轮的回归点。
        internal static bool ShouldQuitOnClose(int page, bool connected)
        {
            if (connected) return false;       // 代理还在跑，收进托盘继续工作
            return page == 1;                  // 还没登录节点的导入页：没有任何东西需要留在后台
        }

        // 第二个实例启动时，把已经在跑的窗口叫到前台（见 Program.Main）。
        // UI 线程上调用，隐藏状态下 Show 出来并激活。
        internal static void ActivateMain()
        {
            var f = Instance;
            if (f == null) return;
            try
            {
                if (f.InvokeRequired) { f.BeginInvoke(new Action(ActivateMain)); return; }
                if (!f.Visible) f.ShowFromTrayPublic();
                else
                {
                    if (f.WindowState == FormWindowState.Minimized) f.WindowState = FormWindowState.Normal;
                    f.Activate();
                    f.BringToFront();
                }
            }
            catch { }
        }

        // 点叉：已登录/已保存节点时隐藏到托盘（代理还要继续跑）；
        // 但停在导入页时没有任何在跑的东西，这时候「隐藏到托盘」等于骗用户已经关了：
        // 再从桌面点图标会被单实例挡掉，看起来就是「点了叉就再也打不开」。导入页一律真退出。
        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            base.OnFormClosing(e);
            bool idle = ShouldQuitOnClose(_page, _connected);
            if (_realExit || idle || e.CloseReason == CloseReason.ApplicationExitCall || e.CloseReason == CloseReason.WindowsShutDown)
            {
                // idle 也走真退出的收尾，避免托盘图标残留成僵尸
                _realExit = true;
                // 真退出：断开代理并清理系统代理设置
                try { if (_connected) StopProxy(); } catch { }
                if (_tray != null) { _tray.Visible = false; _tray.Dispose(); _tray = null; }
                return;
            }
            e.Cancel = true;
            Hide();
            if (!_trayTipShown)
            {
                _trayTipShown = true;
                try { _tray.ShowBalloonTip(2000, "客户端", "程序已最小化到托盘，右下角图标可退出", ToolTipIcon.Info); } catch { }
            }
        }
    }
}
