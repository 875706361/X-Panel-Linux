// 运行日志窗口：只读展示 xpanel.log，支持复制与打开所在文件夹。
// 用户报告「点登录没反应」时，让他打开这个窗口直接截图/复制最后几行，比让他找 %TEMP% 容易得多。
using System;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Text;
using System.Windows.Forms;

namespace XPanelWin
{
    internal class LogForm : Form
    {
        private readonly TextBox _box;

        public LogForm(string text, string path)
        {
            Text = "运行日志 - " + path;
            StartPosition = FormStartPosition.CenterParent;
            ClientSize = new Size(780, 540);
            BackColor = Color.White;
            AutoScaleMode = AutoScaleMode.None;
            MinimizeBox = false;
            MaximizeBox = true;
            ShowIcon = false;

            var bar = new Panel { Dock = DockStyle.Bottom, Height = 46, BackColor = Color.FromArgb(0xF4, 0xF5, 0xF8) };
            Controls.Add(bar);

            _box = new TextBox
            {
                Multiline = true,
                ReadOnly = true,
                ScrollBars = ScrollBars.Both,
                WordWrap = false,
                Dock = DockStyle.Fill,
                Font = new Font("Consolas", 9.5f),
                BackColor = Color.White,
                Text = text ?? ""
            };
            Controls.Add(_box);

            var bCopy = new Button { Text = "复制全部", Left = 12, Top = 9, Width = 110, Height = 28 };
            bCopy.Click += (s, e) =>
            {
                try { Clipboard.SetText(_box.Text); bCopy.Text = "已复制"; }
                catch { bCopy.Text = "复制失败"; }
            };
            var bOpen = new Button { Text = "打开日志文件夹", Left = 130, Top = 9, Width = 140, Height = 28 };
            bOpen.Click += (s, e) =>
            {
                try { Process.Start("explorer.exe", "/select,\"" + path + "\""); }
                catch { try { Process.Start("explorer.exe", Path.GetDirectoryName(path)); } catch { } }
            };
            var bClose = new Button { Text = "关闭", Left = 278, Top = 9, Width = 80, Height = 28 };
            bClose.Click += (s, e) => Close();
            bar.Controls.AddRange(new Control[] { bCopy, bOpen, bClose });

            // 打开就滚到末尾：用户要看的永远是最新几行。
            Shown += (s, e) => { _box.SelectionStart = _box.TextLength; _box.ScrollToCaret(); };
        }
    }
}
