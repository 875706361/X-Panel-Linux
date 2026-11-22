// 字体系统自检：字号是 96dpi 逻辑像素、随 DpiScale 等比放大，且每个字号都装得进对应槽位。
using System;
using System.Drawing;

namespace XPanelWin
{
    internal static class FontTests
    {
        private static int _fail;

        private static void Ok(string name, bool cond, string detail)
        {
            Console.WriteLine((cond ? "PASS " : "FAIL ") + name + (detail == null ? "" : "  " + detail));
            if (!cond) _fail++;
        }

        public static int Main(string[] args)
        {
            Fonts.Init(1f);
            Console.WriteLine("=== 1. 字体族解析 ===");
            Console.WriteLine("  UI  = " + Fonts.UiFamily);
            Console.WriteLine("  等宽 = " + Fonts.MonoFamily);
            Ok("UI 族非空", !string.IsNullOrEmpty(Fonts.UiFamily), null);
            Ok("等宽族非空", !string.IsNullOrEmpty(Fonts.MonoFamily), null);

            Console.WriteLine("=== 2. 96dpi 行高（逻辑像素）===");
            float[] sizes = { 11f, 13f, 14f, 16f, 20f, 22f, 34f };
            foreach (float s in sizes)
            {
                Font f = Fonts.Ui(s, FontStyle.Bold);
                Console.WriteLine("  " + s + "px -> 行高 " + Fonts.LineHeight(f) +
                                  " 高/字号 " + (Fonts.LineHeight(f) / s).ToString("0.00"));
            }

            Console.WriteLine("=== 3. 槽位装得下（MainForm.CheckFontLayout）===");
            string bad = MainForm.CheckFontLayout();
            Ok("字体/槽位配对", bad == "", bad == "" ? null : bad);

            Console.WriteLine("=== 4. DPI 等比放大 ===");
            Fonts.Init(1f);
            int h1 = Fonts.LineHeight(Fonts.Ui(14));
            Fonts.Init(1.5f);
            int h15 = Fonts.LineHeight(Fonts.Ui(14));
            Fonts.Init(2f);
            int h2 = Fonts.LineHeight(Fonts.Ui(14));
            Console.WriteLine("  14px 行高: 96dpi=" + h1 + "  144dpi=" + h15 + "  192dpi=" + h2);
            Ok("144dpi 放大约 1.5 倍", Math.Abs(h15 - h1 * 1.5) <= 2, null);
            Ok("192dpi 放大约 2 倍", Math.Abs(h2 - h1 * 2.0) <= 3, null);

            Console.WriteLine("=== 5. 关键文案宽度（96dpi）===");
            Fonts.Init(1f);
            try
            {
                using (var g = Graphics.FromHwnd(IntPtr.Zero))
                {
                    string[] texts = {
                        "粘贴节点链接，支持 vless / vmess / trojan / ss / hy2",
                        "在此粘贴节点链接，例如 vless://uuid@host:port?type=tcp&security=reality",
                        "系统代理 · 本地 Socks 10808 / Http 10809",
                        "↑ 0 B/s  ↓ 0 B/s · 客户端 v10.5.3",
                        "1.23 TB"
                    };
                    Font[] fonts = {
                        Fonts.Ui(13), Fonts.Ui(14), Fonts.Ui(13), Fonts.Mono(11), Fonts.Ui(34, FontStyle.Bold)
                    };
                    for (int i = 0; i < texts.Length; i++)
                    {
                        int w = System.Windows.Forms.TextRenderer.MeasureText(g, texts[i], fonts[i],
                            new Size(10000, 200), System.Windows.Forms.TextFormatFlags.NoPadding).Width;
                        Console.WriteLine("  宽 " + w + "px  " + texts[i]);
                    }
                }
            }
            catch (Exception ex) { Console.WriteLine("  测量失败（无图形设备）: " + ex.Message); }

            Console.WriteLine(_fail == 0 ? "ALL PASS" : _fail + " FAILED");
            return _fail == 0 ? 0 : 1;
        }
    }
}
