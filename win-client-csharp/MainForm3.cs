// 主窗口（第三部分：自绘背景与标题栏交互）
using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Text;
using System.Windows.Forms;

namespace XPanelWin
{
    internal partial class MainForm
    {
        // 标题栏按钮热区（物理像素，随 DPI 缩放）
        private Rectangle MinRect => new Rectangle(S(LWW) - S(88), 0, S(44), S(LTITLE));
        private Rectangle CloseRect => new Rectangle(S(LWW) - S(44), 0, S(44), S(LTITLE));
        private bool InMin(Point pt) => MinRect.Contains(pt);
        private bool InClose(Point pt) => CloseRect.Contains(pt);

        protected override void OnPaint(PaintEventArgs e)
        {
            int WW = S(LWW), WH = S(LWH), TL = S(LTITLE), BA = S(LBAR);
            int X0 = S(LX0), LXW2 = S(LXW);
            Graphics g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.TextRenderingHint = TextRenderingHint.AntiAlias;

            // 基底：Mica 开启时这是兜底色，关闭时就是窗口纯色底
            using (var b = new SolidBrush(Theme.C(Theme.BASE)))
                g.FillRectangle(b, 0, 0, WW, WH);

            // 标题栏：与底色融为一体，不画任何分割线（Go v7.2.0 去掉了硬线）
            using (var b = new SolidBrush(Theme.C(Theme.BASE)))
                g.FillRectangle(b, 0, 0, WW, TL);

            // 左侧微徽标：外圈光晕 + 内核实心，单圆点太平
            using (var b = new SolidBrush(Theme.C(0xE8F1FB)))
            {
                int hx = S(14), hy = TL / 2 - S(10), hsz = S(20);
                g.FillEllipse(b, hx, hy, hsz, hsz);
            }
            using (var b = new SolidBrush(Theme.C(Theme.ACCENT)))
            {
                int ix = S(18), iy = TL / 2 - S(6), isz = S(12);
                g.FillEllipse(b, ix, iy, isz, isz);
            }

            // 标题栏文字：按实测字高垂直居中
            DrawTitleText(g, "客户端", FAppTitle, Theme.C(Theme.TXT), S(38));

            PaintTitleButtons(g, WW, TL);

            if (_page == 1) PaintPage1(g);
            else if (_page == 2) PaintPage2(g, X0, LXW2);

            // 底栏：不画实色底，两段渐淡过渡代替 1px 硬线
            PaintSoftShadow(g, 0, WH - BA, WW);
            base.OnPaint(e);
        }

        private void DrawTitleText(Graphics g, string s, Font f, Color clr, int x)
        {
            int TL = S(LTITLE);
            Size sz = TextRenderer.MeasureText(g, s, f);
            Rectangle rc = new Rectangle(x, (TL - sz.Height) / 2 + 1, S(LWW) - S(96) - x, sz.Height);
            TextRenderer.DrawText(g, s, f, rc, clr, TextFormatFlags.NoPadding);
        }

        // 最小化 / 关闭：悬浮微灰、关闭悬浮变红
        private void PaintTitleButtons(Graphics g, int WW, int TL)
        {
            Rectangle mr = MinRect;
            Color minBg = _pressMin ? Theme.C(0xE5E7EC) : (_hoverMin ? Theme.C(0xEDEFF3) : Theme.C(Theme.BASE));
            using (var b = new SolidBrush(minBg)) g.FillRectangle(b, mr);
            using (var p = new Pen(Theme.C(0x4A4A4A), 1))
            {
                int my = TL / 2 + S(3);
                g.DrawLine(p, WW - S(71), my, WW - S(61), my);
            }

            Rectangle cr = CloseRect;
            Color xBg, xClr;
            if (_pressClose) { xBg = Theme.C(Theme.DANGER_H); xClr = Color.White; }
            else if (_hoverClose) { xBg = Theme.C(Theme.DANGER); xClr = Color.White; }
            else { xBg = Theme.C(Theme.BASE); xClr = Theme.C(0x4A4A4A); }
            using (var b = new SolidBrush(xBg)) g.FillRectangle(b, cr);
            using (var p = new Pen(xClr, 1))
            {
                int cy1 = TL / 2 - S(5), cy2 = TL / 2 + S(5);
                g.DrawLine(p, WW - S(27), cy1, WW - S(17), cy2);
                g.DrawLine(p, WW - S(17), cy1, WW - S(27), cy2);
            }
        }

        // page1：v10 输入框圆角白底（4 行高）+ 1px 描边（聚焦转 accent）+ 三段渐淡软阴影
        private void PaintPage1(Graphics g)
        {
            Rectangle er = new Rectangle(S(LM), S(EDIT_Y), S(LCW), S(EDIT_H));
            PaintSoftShadow(g, S(LM), S(EDIT_Y) + S(EDIT_H), S(LCW));
            using (var path = FlatButton.RRect(er, S(10)))
            {
                using (var b = new SolidBrush(Color.White)) g.FillPath(b, path);
                Color bc = _editFocused ? Theme.C(Theme.ACCENT) : Theme.C(Theme.INPUT_B);
                using (var p = new Pen(bc, 1)) g.DrawPath(p, path);
            }
            // 协议徽标行：输入框下方居中五个协议名，浅灰大写
            string protos = "VLESS   VMESS   TROJAN   SS   HY2";
            using (var f = new Font("Consolas", 8.25f))
            {
                Size sz = TextRenderer.MeasureText(g, protos, f);
                TextRenderer.DrawText(g, protos, f,
                    new Rectangle(S(LM), S(EDIT_Y) + S(EDIT_H) + S(10), S(LCW), sz.Height),
                    Theme.C(0xB9BEC7), TextFormatFlags.HorizontalCenter | TextFormatFlags.NoPadding);
            }
        }

        // 三段渐淡软阴影：贴在元素下缘 3px，只往下不出左右头（Go softShadow 同款，
        // 1px 硬色带在浅底上是一条脏线，三段过渡才是浮起）
        private static void PaintSoftShadow(Graphics g, int x, int y, int w)
        {
            g.FillRectangle(ShadowBrush(0xEDEFF3), x, y, w, 1);
            g.FillRectangle(ShadowBrush(0xE9EBF0), x, y + 1, w, 1);
            g.FillRectangle(ShadowBrush(0xE2E5EA), x, y + 2, w, 1);
        }

        private static SolidBrush ShadowBrush(int rgb) => new SolidBrush(Theme.C(rgb));

        private void InvalidateInput()
        {
            Invalidate(new Rectangle(S(LM) - S(8), S(EDIT_Y) - S(8), S(LCW) + S(16), S(EDIT_H) + S(20)));
        }

        // page2：仪表盘卡片 + 分割线 + 状态指示点
        private void PaintPage2(Graphics g, int X0, int LXW2)
        {
            int cx = S(LM), cy = S(CARD2_Y), cw = S(LCW), ch = S(CARD2_H);
            Rectangle cardRc = new Rectangle(cx, cy, cw, ch);
            PaintSoftShadow(g, cx, cy + ch, cw);
            using (var path = FlatButton.RRect(cardRc, S(R_CARD)))
            {
                using (var b = new SolidBrush(Theme.C(Theme.CARD))) g.FillPath(b, path);
                using (var p = new Pen(Theme.C(Theme.CARD_B), 1)) g.DrawPath(p, path);
            }
            using (var p = new Pen(Theme.C(0xE3E6EC), 1))
                g.DrawLine(p, X0, S(DIV_Y), X0 + LXW2, S(DIV_Y));
            // 状态点：外圈光晕 + 内核实心（替代 ● 字符，不同字体大小位置不一）
            int dcx = X0 + S(5), dcy = S(STATUS_Y) + S(11);
            using (var b = new SolidBrush(Theme.C(0xE3EAF3))) g.FillEllipse(b, dcx - S(7), dcy - S(7), S(14), S(14));
            using (var b = new SolidBrush(_connected ? Theme.C(Theme.SUCCESS) : Theme.C(Theme.IDLE)))
                g.FillEllipse(b, dcx - S(4), dcy - S(4), S(8), S(8));
        }

        // ---------- 标题栏鼠标 ----------

        protected override void OnMouseMove(MouseEventArgs e)
        {
            bool nm = InMin(e.Location), nc = InClose(e.Location);
            if (nm != _hoverMin || nc != _hoverClose)
            {
                _hoverMin = nm; _hoverClose = nc;
                Invalidate(new Rectangle(S(LWW) - S(88), 0, S(88), S(LTITLE)));
            }
            base.OnMouseMove(e);
        }

        protected override void OnMouseLeave(EventArgs e)
        {
            if (_hoverMin || _hoverClose)
            {
                _hoverMin = _hoverClose = false;
                _pressMin = _pressClose = false;
                Invalidate(new Rectangle(S(LWW) - S(88), 0, S(88), S(LTITLE)));
            }
            base.OnMouseLeave(e);
        }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            if (e.Button == MouseButtons.Left)
            {
                if (InClose(e.Location)) { _pressClose = true; Invalidate(CloseRect); return; }
                if (InMin(e.Location)) { _pressMin = true; Invalidate(MinRect); return; }
                // 标题栏空白处拖动窗口
                if (e.Y < S(LTITLE))
                {
                    if (e.X > S(LWW) - S(88)) return;
                    ReleaseCapture();
                    SendMessage(Handle, WM_NCLBUTTONDOWN, HTCAPTION, IntPtr.Zero);
                    return;
                }
            }
            base.OnMouseDown(e);
        }

        protected override void OnMouseUp(MouseEventArgs e)
        {
            if (_pressClose || _pressMin)
            {
                bool wasClose = _pressClose, wasMin = _pressMin;
                _pressClose = _pressMin = false;
                Invalidate(new Rectangle(S(LWW) - S(88), 0, S(88), S(LTITLE)));
                if (wasClose && InClose(e.Location)) { Close(); return; }
                if (wasMin && InMin(e.Location)) { WindowState = FormWindowState.Minimized; return; }
            }
            base.OnMouseUp(e);
        }

        [System.Runtime.InteropServices.DllImport("user32.dll")]
        private static extern bool ReleaseCapture();
        [System.Runtime.InteropServices.DllImport("user32.dll")]
        private static extern int SendMessage(IntPtr hWnd, int msg, int wParam, IntPtr lParam);
        private const int WM_NCLBUTTONDOWN = 0xA1, HTCAPTION = 2;
    }
}
