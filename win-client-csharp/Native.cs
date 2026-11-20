// X-Panel Windows 客户端 v9.0 — Native P/Invoke 与主题
// 配色常量与 Go v7.2.0 逐项对齐（COLORREF 0x00BBGGRR 直接照搬）
using System;
using System.Drawing;
using System.Runtime.InteropServices;

namespace XPanelWin
{
    internal static class Native
    {
        [DllImport("user32.dll")] internal static extern int SetProcessDPIAware();
        [DllImport("user32.dll")] internal static extern int GetSystemMetrics(int nIndex);
        [DllImport("dwmapi.dll")] internal static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int pvAttribute, int cbAttribute);
        [DllImport("wininet.dll", SetLastError = true)]
        internal static extern bool InternetSetOption(IntPtr hInternet, int dwOption, IntPtr lpBuffer, int dwBufferLength);

        [DllImport("user32.dll")]
        internal static extern IntPtr SendMessage(IntPtr hWnd, int msg, IntPtr wParam, IntPtr lParam);

        internal const int SM_CXSCREEN = 0, SM_CYSCREEN = 1;
        internal const int CS_DROPSHADOW = 0x00020000;
        internal const int DWMWA_WINDOW_CORNER_PREFERENCE = 33; // Win11 圆角
    }

    // Fluent 主题。COLORREF = 0x00BBGGRR。accent #0078D4，与 Go v7.2.0 完全一致。
    internal static class Theme
    {
        public const int ACCENT = 0xD47800, ACCENT_H = 0xBE6E10, ACCENT_P = 0x9E5A00;
        public const int DANGER = 0x1B2BC4, DANGER_H = 0x1B27B0;
        public const int SUCCESS = 0x43A02E, IDLE = 0x8A8A8A;
        public const int TXT = 0x1A1A1A, MUTED = 0x5F5F5F, FAINT = 0x6E6E6E;
        // Mica 基底 + 通透卡片：Go v7.2.0 原值
        public const int BASE = 0xF3F4F7;                       // RGB(247,244,243)
        public const int CARD = 0xF7F8FB, CARD_B = 0xE3E6EC;    // 半透明白叠 Mica 的视觉等价色 / 卡片细描边
        public const int INPUT = 0xFFFFFF, INPUT_B = 0xD2D5DB;  // 输入框微弱内凹
        public const int TITLE_LINE = 0xF0EBE9;                 // （保留兼容，v9 不再画硬线）

        public static Color C(int clr) => Color.FromArgb(clr & 0xFF, (clr >> 8) & 0xFF, (clr >> 16) & 0xFF);
    }
}
