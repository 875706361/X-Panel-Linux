// 统一字体工厂：像素单位 + DPI 显式缩放 + 字体族回退。
//
// 为什么不用 pt：Font 的 pt 会按 Graphics.DpiY 再乘一次（96dpi 下 1pt=1.33px），
// 而界面布局的每个坐标都走 MainForm.S() 手动乘过 DpiScale。两者叠加，加上 Form 默认的
// AutoScaleMode.Font 又一次按字体度量缩放控件，就会出现「框放大了 1.5²、字只放大 1.5」
// 的大框小字，以及字被固定高度裁掉半截。这里统一成：
//   逻辑像素(96dpi 坐标系) × DpiScale → 显式 GraphicsUnit.Pixel
// 与布局常量完全同一坐标系，字随框等比缩放，再不会被二次换算。
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Text;

namespace XPanelWin
{
    internal static class Fonts
    {
        // 字体族回退链：Win8+ 有 "Microsoft YaHei UI"，Win7 只有 "Microsoft YaHei"，
        // 英文版系统可能都没有。缺字族时 GDI 会逐字回退，中英混排字重/基线和行高全乱，
        // 所以必须先挑一个系统里真实存在的族。
        private static readonly string[] UiCandidates =
        {
            "Microsoft YaHei UI", "Microsoft YaHei", "Segoe UI", "Microsoft Sans Serif",
            // 非 Windows 渲染环境（mono + Xvfb 跑截图/单测）没有雅黑：文泉驿正黑是黑体，
            // 行高/字宽最接近雅黑；排在最差选择 SimSun 之前，避免截图被宋体带偏。
            "WenQuanYi Zen Hei", "SimSun"
        };
        private static readonly string[] MonoCandidates =
        {
            "Consolas", "Cascadia Mono", "Courier New"
        };

        private static readonly Dictionary<string, Font> Cache = new Dictionary<string, Font>();

        internal static string UiFamily { get; private set; }
        internal static string MonoFamily { get; private set; }
        internal static float Scale { get; private set; }

        internal static void Init(float dpiScale)
        {
            Scale = (dpiScale >= 1f && dpiScale <= 4f) ? dpiScale : 1f;
            UiFamily = Pick(UiCandidates);
            MonoFamily = Pick(MonoCandidates);
            Cache.Clear();
        }

        // px：96dpi 下的逻辑像素，与 MainForm 里的布局常量同一单位。
        internal static Font Ui(float px) { return Ui(px, FontStyle.Regular); }

        internal static Font Ui(float px, FontStyle style) { return Get(UiFamily, px * Scale, style); }

        internal static Font Mono(float px) { return Get(MonoFamily, px * Scale, FontStyle.Regular); }

        internal static Font Mono(float px, FontStyle style) { return Get(MonoFamily, px * Scale, style); }

        private static Font Get(string family, float px, FontStyle style)
        {
            // 取整到整数像素：125% 缩放下 14px×1.25=17.5px 这种半像素字号会让 GDI 先做
            // 亚像素缩放再栅格化，笔画发虚；整数字号能直接命中 hinting 的清晰档位。
            px = (float)Math.Round(px);
            if (px < 1f) px = 1f;
            string key = family + "|" + px.ToString("0.##") + "|" + (int)style;
            Font f;
            if (Cache.TryGetValue(key, out f)) return f;
            f = new Font(family, px, style, GraphicsUnit.Pixel);
            Cache[key] = f;
            return f;
        }

        private static string Pick(string[] candidates)
        {
            try
            {
                var installed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (FontFamily fam in FontFamily.Families)
                {
                    if (!string.IsNullOrEmpty(fam.Name)) installed.Add(fam.Name);
                }
                foreach (string c in candidates)
                {
                    if (installed.Contains(c)) return c;
                }
            }
            catch { }
            // 全家都探测不到（极简 mono/GDI 环境）：给出链尾，由 GDI 自己回退。
            return candidates[candidates.Length - 1];
        }

        // 单行行高（像素，含上下留白）：测试用它断言控件高度装得下对应字号。
        internal static int LineHeight(Font f)
        {
            try { return (int)Math.Ceiling(f.GetHeight()); }
            catch { return 0; }
        }
    }
}
