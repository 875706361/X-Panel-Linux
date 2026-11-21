// 自绘控件：无边框按钮、透明标签、描边输入框
using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Text;
using System.Windows.Forms;

namespace XPanelWin
{
    // Fluent 三态按钮：纯色底 + 圆角 + 1px 内描边 + 顶部高光。
    // Go v7.2.0 已把渐变删掉（渐变字段从未被渲染），这里同样只保留纯色三态。
    internal class FlatButton : Control
    {
        public Color Fill, Hover, PressClr, BorderClr, TextClr;
        private bool _hover, _press;

        public FlatButton()
        {
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer |
                     ControlStyles.ResizeRedraw | ControlStyles.UserPaint |
                     ControlStyles.SupportsTransparentBackColor, true);
            BackColor = Color.Transparent;
            Cursor = Cursors.Hand;
            Font = new Font("Microsoft YaHei UI", 12f, FontStyle.Bold);
        }

        // 已连接时"启动加速"变"断开连接"的红底危险态
        public void SetDangerStyle()
        {
            SetColors(Theme.C(Theme.DANGER), Theme.C(Theme.DANGER_H),
                      Theme.C(Theme.DANGER_H), Theme.C(Theme.DANGER), Color.White);
        }

        // 配色统一入口。这些是普通字段，改完不会自动重绘 —— 只有 Text 变化才隐式 Invalidate。
        // 模式按钮切换时正是踩这个坑：字段已换新色，画面还停在旧色，看着像按钮坏了。
        public void SetColors(Color fill, Color hover, Color press, Color border, Color text)
        {
            Fill = fill; Hover = hover;
            PressClr = press; BorderClr = border; TextClr = text;
            Invalidate();
        }

        protected override void OnMouseEnter(EventArgs e) { _hover = true; Invalidate(); base.OnMouseEnter(e); }
        protected override void OnMouseLeave(EventArgs e) { _hover = false; _press = false; Invalidate(); base.OnMouseLeave(e); }
        protected override void OnMouseDown(MouseEventArgs e)
        {
            if (e.Button == MouseButtons.Left) { _press = true; Invalidate(); }
            base.OnMouseDown(e);
        }
        protected override void OnMouseUp(MouseEventArgs e) { _press = false; Invalidate(); base.OnMouseUp(e); }

        protected override void OnPaint(PaintEventArgs e)
        {
            Color fill, border;
            if (_press) { fill = PressClr; border = PressClr; }
            else if (_hover) { fill = Hover; border = Hover; }
            else { fill = Fill; border = BorderClr; }

            Rectangle rc = ClientRectangle;
            int d = Math.Max(4, MainForm.S(MainForm.R_CTRL));
            Graphics g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.TextRenderingHint = TextRenderingHint.AntiAlias;

            using (GraphicsPath path = RRect(rc, d))
            {
                using (var b = new SolidBrush(fill)) g.FillPath(b, path);

                // 顶部高光：Fluent 微凸起的核心（按下时不画）
                if (!_press)
                {
                    RectangleF hl = new RectangleF(rc.Left + d / 2f, rc.Top + 1, rc.Right - rc.Left - d, 1.2f);
                    using (var b = new SolidBrush(Color.FromArgb(0x5F, 0xD9, 0xF5, 0xFF))) g.FillRectangle(b, hl);
                }

                // 1px 描边必须内收 1px 再画：笔刷中心落在路径上，右/下边缘的半个笔宽
                // 会被控件自身的裁剪区吃掉，结果是白按钮只剩左/上两条边、右/下边框消失。
                // 表单直接画（非子控件）的输入框/卡片没这个问题，只有 FlatButton 需要内收。
                using (var p = new Pen(border, 1))
                {
                    Rectangle bi = new Rectangle(rc.X + 1, rc.Y + 1, rc.Width - 2, rc.Height - 2);
                    using (var bpath = RRect(bi, Math.Max(1, d - 1)))
                        g.DrawPath(p, bpath);
                }
            }

            // 文字垂直居中（手算，不依赖 WinForms 对齐在所有字体下都正确）
            TextFormatFlags flags = TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding;
            Rectangle tr = rc;
            if (_press) { tr.Y += 1; tr.Height += 1; } // 1px tactile click offset
            TextRenderer.DrawText(g, Text, Font, tr, TextClr, flags);
        }

        // 圆角矩形路径，所有圆角绘制共用这一个
        internal static GraphicsPath RRect(Rectangle rc, int d)
        {
            GraphicsPath p = new GraphicsPath();
            if (rc.Width <= 0 || rc.Height <= 0) return p;
            d = Math.Min(d, Math.Min(rc.Width, rc.Height) / 2);
            p.AddArc(rc.Left, rc.Top, d, d, 180, 90);
            p.AddArc(rc.Right - d, rc.Top, d, d, 270, 90);
            p.AddArc(rc.Right - d, rc.Bottom - d, d, d, 0, 90);
            p.AddArc(rc.Left, rc.Bottom - d, d, d, 90, 90);
            p.CloseFigure();
            return p;
        }
    }

    // 透明标签：直接画在窗口自绘背景上，不自带系统底色
    internal class TransparentLabel : Label
    {
        public TransparentLabel()
        {
            SetStyle(ControlStyles.SupportsTransparentBackColor, true);
            BackColor = Color.Transparent;
            AutoSize = false;
        }
        protected override CreateParams CreateParams
        {
            get
            {
                CreateParams cp = base.CreateParams;
                cp.ExStyle |= 0x20; // WS_EX_TRANSPARENT
                return cp;
            }
        }
    }

    // 无边框输入框：坐在窗口自绘的圆角白底上，自身保持不透明白底。
    // 不要给它开 SupportsTransparentBackColor —— 透明背景要求父窗口先画好再回填，mono 下回填会抓错父缓冲区的偏移，在输入框两角糊出蓝色块。
    internal class BorderedEdit : TextBox
    {
        // 水印：多行 TextBox 没有原生 placeholder（EM_SETCUEBANNER 只对单行有效），
        // 在 WM_PAINT 之后自己补一段浅灰提示；有内容或聚焦时自然消失。
        // 不用叠一个 Label：WS_EX_TRANSPARENT 只改绘制顺序，照样会吃掉点击，焦点进不了输入框。
        private string _placeholder = "";

        public string Placeholder
        {
            get { return _placeholder; }
            set { _placeholder = value ?? ""; Invalidate(); }
        }

        public BorderedEdit()
        {
            BorderStyle = BorderStyle.None;
            BackColor = Color.White;
        }

        private const int WM_PAINT = 0x000F;
        private const int WM_SETTEXT = 0x000C;

        protected override void WndProc(ref Message m)
        {
            base.WndProc(ref m);
            if (m.Msg == WM_SETTEXT) { Invalidate(); return; }
            if (m.Msg != WM_PAINT || _placeholder.Length == 0) return;
            if (!string.IsNullOrEmpty(Text) || !Visible) return;
            try
            {
                Rectangle rc = ClientRectangle;
                rc.Offset(2, 2);
                rc.Inflate(-2, 0);
                using (var g = Graphics.FromHwnd(Handle))
                    TextRenderer.DrawText(g, _placeholder, Font, rc, Color.FromArgb(0x8B, 0x93, 0xA0),
                        TextFormatFlags.Left | TextFormatFlags.Top | TextFormatFlags.WordBreak |
                        TextFormatFlags.NoPadding | TextFormatFlags.TextBoxControl);
            }
            catch { }
        }
    }
}
