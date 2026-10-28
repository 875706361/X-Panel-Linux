//go:build windows

// 客户端 Win 版 v7.2：视觉打磨。按钮三态纯色化（渐变字段从未被渲染，删除），
// 输入框聚焦描边变蓝，卡片/输入框/按钮底部阴影改三段渐淡（替代 3px 硬色带），
// 标题栏去副标题与硬分割线，底栏硬线改两段渐淡，速度行降为中性灰。
// 布局栅格与业务层（xray/系统代理/流量轮询）未动。
package main

import (
	"bytes"
	"encoding/json"
	"fmt"
	"io"
	"log"
	"net/http"
	"crypto/tls"
	"net/url"
	"os"
	"path/filepath"
	"runtime"
	"runtime/debug"
	"strconv"
	"strings"
	"sync"
	"time"
	"unicode/utf16"
	"unsafe"

	core "github.com/xtls/xray-core/core"
	_ "github.com/xtls/xray-core/main/distro/all"
	"golang.org/x/sys/windows"
	"golang.org/x/sys/windows/registry"
)

const VER = "v7.2.0"

// 96dpi 逻辑设计稿：与 /root/preview/xpanel-fluent-v3.html 一比一对应
const (
	LWW, LWH     = 450, 560 // 窗口
	LTITLE, LBAR = 48, 36   // 标题栏 / 底栏
	LM           = 22       // 全局统一外边距
	LCW          = LWW - 2*LM
	LP           = 18 // page2 卡片内边距
	LX0          = LM + LP
	LXW          = LCW - 2*LP
)

const (
	// page1：导入节点。输入框直接坐 Mica 上，不再套卡片
	P1TITLE_Y, P1SUB_Y = 70, 100 // 标题 / 副标题（标题栏 48 + 22）
	EDIT_Y, EDIT_H     = 138, 224
	BTN1_Y, BTN1_H     = 378, 48
	TIP_Y              = 444
	// page2：仪表盘。卡片高度按内容收紧
	CARD2_Y, CARD2_H     = 62, 240
	STATUS_Y             = 80
	TOTAL_Y              = 112
	TRAFFIC_Y            = 132
	DIV_Y                = 196
	COL_LBL_Y, COL_VAL_Y = 208, 230
	BTN2_Y, BTN2_H       = 316, 48
	BTN3_Y, BTN3_H       = 374, 48
	FOOTER_Y             = 440
)

const (
	CS_HREDRAW, CS_VREDRAW, CS_DBLCLKS, CS_DROPSHADOW = 0x1, 0x2, 0x8, 0x20000
	WS_POPUP, WS_VISIBLE, WS_CHILD, WS_MINIMIZEBOX    = 0x80000000, 0x10000000, 0x40000000, 0x20000
	WS_CLIPCHILDREN                                   = 0x02000000
	ES_MULTILINE, ES_AUTOVSCROLL                      = 0x4, 0x40
	BS_OWNERDRAW                                      = 0xB
	SW_HIDE, SW_SHOW                                  = 0, 5
	WM_DESTROY, WM_PAINT, WM_COMMAND, WM_DRAWITEM     = 0x2, 0xF, 0x111, 0x2B
	WM_SETFONT, WM_CTLCOLORSTATIC, WM_CTLCOLOREDIT    = 0x30, 0x138, 0x133
	EM_SETMARGINS, EC_LEFTMARGIN, EC_RIGHTMARGIN      = 0x00D3, 0x0001, 0x0002

	WM_NCHITTEST, WM_SYSCOMMAND, WM_LBUTTONDOWN = 0x84, 0x112, 0x201
	WM_LBUTTONUP, WM_CAPTURECHANGED             = 0x202, 0x215
	HTCAPTION, SC_MINIMIZE                      = 2, 0xF020
	DT_CENTER, DT_SINGLELINE, DT_WORDBREAK      = 0x1, 0x200, 0x10
	GWLP_WNDPROC                                = -4
)

// ---------- Fluent 主题 ----------
// COLORREF = 0x00BBGGRR。配色直接取自设计稿 v3，不再沿用 v6 的湛蓝。
const (
	CLR_ACCENT   = 0xD47800 // #0078D4 (Fluent 蓝，BGR)
	CLR_ACCENT_H = 0xBE6E10 // #106EBE hover
	CLR_ACCENT_P = 0x9E5A00 // #005A9E pressed
	CLR_DANGER   = 0x1B2BC4 // #C42B1C (Fluent 红)
	CLR_DANGER_H = 0x1B27B0 // #B3271B
	CLR_SUCCESS  = 0x43A02E // #2EA043
	CLR_IDLE     = 0x8A8A8A

	CLR_TXT   = 0x1A1A1A
	CLR_MUTED = 0x5F5F5F
	CLR_FAINT = 0x6E6E6E

	// Mica 基底：Win10 1607 以下退回这个色
	CLR_BASE = 0xF3F4F7
	// page2 卡片：比 v6 更通透，是半透明白叠在 Mica 上的视觉等价色
	CLR_CARD   = 0xF7F8FB
	CLR_CARD_B = 0xE3E6EC // 卡片细描边
	// 输入框：微弱内凹
	CLR_INPUT   = 0xFFFFFF
	CLR_INPUT_B = 0xD2D5DB
)

// DwmSetWindowAttribute：Win11 圆角
const DWMWA_WINDOW_CORNER_PREFERENCE = 33 // 2 = round

func applyRoundCorners(h windows.HWND) {
	if err := dwmapi.Load(); err != nil {
		return // Win10 及以下没有 dwmapi 导出，静默跳过
	}
	pref := uintptr(2)
	pDwmSetWindowAttr.Call(uintptr(h), DWMWA_WINDOW_CORNER_PREFERENCE, uintptr(unsafe.Pointer(&pref)), 4)
}

const PANEL_BASE = "http://67.215.255.156:13688"

var (
	dwmapi            = windows.NewLazySystemDLL("dwmapi.dll")
	pDwmSetWindowAttr = dwmapi.NewProc("DwmSetWindowAttribute")

	user32   = windows.NewLazySystemDLL("user32.dll")
	gdi32    = windows.NewLazySystemDLL("gdi32.dll")
	kernel32 = windows.NewLazySystemDLL("kernel32.dll")
	wininet  = windows.NewLazySystemDLL("wininet.dll")

	pLoadCursor  = user32.NewProc("LoadCursorW")
	pSetCursor   = user32.NewProc("SetCursor")
	hCursorHand  uintptr
	hCursorArrow uintptr

	pSetWindowLong  = user32.NewProc("SetWindowLongPtrW")
	pCallWindowProc = user32.NewProc("CallWindowProcW")

	hoverMin   bool
	hoverClose bool
	hoverBtn   windows.HWND
	pressBtn   windows.HWND // 鼠标左键当前按在哪颗按钮上（0=没按）
	pressTitle int8         // 0=无 1=最小化 2=关闭
)

var (
	pCreateWindowEx = user32.NewProc("CreateWindowExW")
	pDefWindowProc  = user32.NewProc("DefWindowProcW")
	pRegisterClass  = user32.NewProc("RegisterClassExW")
	pGetMessage     = user32.NewProc("GetMessageW")
	pTranslateMsg   = user32.NewProc("TranslateMessage")
	pDispatchMsg    = user32.NewProc("DispatchMessageW")
	pSendMessage    = user32.NewProc("SendMessageW")
	pSetWindowText  = user32.NewProc("SetWindowTextW")
	pGetWindowText  = user32.NewProc("GetWindowTextW")
	pPostQuit       = user32.NewProc("PostQuitMessage")
	pInvalidate     = user32.NewProc("InvalidateRect")
	pTrackMouse     = user32.NewProc("TrackMouseEvent")
	pLoadIcon       = user32.NewProc("LoadIconW")
	pBeginPaint     = user32.NewProc("BeginPaint")
	pEndPaint       = user32.NewProc("EndPaint")
	pShowWindow     = user32.NewProc("ShowWindow")
	pGetSysMetrics  = user32.NewProc("GetSystemMetrics")
	pGetWindowRect  = user32.NewProc("GetWindowRect")
	pDrawText       = user32.NewProc("DrawTextW")
	pFillRect       = user32.NewProc("FillRect")
	pScreenToClient = user32.NewProc("ScreenToClient")
	pDestroyWindow  = user32.NewProc("DestroyWindow")
	pSetProcessDPI  = user32.NewProc("SetProcessDPIAware")
	pGetDpiForWin   = user32.NewProc("GetDpiForWindow")
	pMoveWindow     = user32.NewProc("MoveWindow")
	pGetCursorPos   = user32.NewProc("GetCursorPos")
	pSetCapture     = user32.NewProc("SetCapture")
	pReleaseCapture = user32.NewProc("ReleaseCapture")

	pCreateFont    = gdi32.NewProc("CreateFontW")
	pCreateSolid   = gdi32.NewProc("CreateSolidBrush")
	pGetStockObj   = gdi32.NewProc("GetStockObject")
	pSetTextColor  = gdi32.NewProc("SetTextColor")
	pSetBkColor    = gdi32.NewProc("SetBkColor")
	pSetBkMode     = gdi32.NewProc("SetBkMode")
	pRoundRect     = gdi32.NewProc("RoundRect")
	pSelectObject  = gdi32.NewProc("SelectObject")
	pCreatePen     = gdi32.NewProc("CreatePen")
	pDeleteObject  = gdi32.NewProc("DeleteObject")
	pMoveToEx      = gdi32.NewProc("MoveToEx")
	pLineTo        = gdi32.NewProc("LineTo")
	pGetTextExtent = gdi32.NewProc("GetTextExtentPoint32W")
	pEllipse       = gdi32.NewProc("Ellipse")

	// 按钮客户区矩形：invalidateSelf 用
	pGetClientRect = user32.NewProc("GetClientRect")

	pGetModule   = kernel32.NewProc("GetModuleHandleW")
	pInternetSet = wininet.NewProc("InternetSetOptionW")
)

type WNDCLASSEX struct {
	CbSize        uint32
	Style         uint32
	LpfnWndProc   uintptr
	CbClsExtra    int32
	CbWndExtra    int32
	HInstance     windows.Handle
	HIcon         windows.Handle
	HCursor       windows.Handle
	HbrBackground windows.Handle
	LpszMenuName  *uint16
	LpszClassName *uint16
	HIconSm       windows.Handle
}

type MSG struct {
	Hwnd    windows.HWND
	Message uint32
	WParam  uintptr
	LParam  uintptr
	Time    uint32
	Pt      struct{ X, Y int32 }
}

type PAINTSTRUCT struct {
	Hdc         uintptr
	FErase      int32
	RcPaint     RECT
	FRestore    int32
	FIncUpdate  int32
	RgbReserved [32]byte
}

type RECT struct{ L, T, R, B int32 }

type DRAWITEMSTRUCT struct {
	CtlType    uint32
	CtlID      uint32
	ItemID     uint32
	ItemAction uint32
	ItemState  uint32
	HwndItem   windows.HWND
	HDC        uintptr
	RcItem     RECT
	ItemData   uintptr
}

var (
	dpiScale float64 = 1.0
	hwndMain windows.HWND

	page       int // 1=导入节点 2=仪表盘
	connected  bool
	serverUUID string
	serverAddress string
	xray       *core.Instance
	stateMu    sync.Mutex
	busy       bool

	speedUp, speedDown, lastUp, lastDown int64

	// page1
	hTitle1, hSub1, hEdit, hBtnLogin, hTip windows.HWND
	// page2
	hStatus, hExpire, hLblTotal, hTraffic, hLblUp, hUp, hLblDown, hDown windows.HWND
	hBtnConn, hBtnSwitch, hFooter                                       windows.HWND
	// shared
	hSpeed windows.HWND

	fAppTitle, fTitle, fSub, fEdit, fBtn, fStat, fVal, fBig, fMono uintptr
	brBase, brCard, brInput                                        windows.Handle

	page1 = []*windows.HWND{}
	page2 = []*windows.HWND{}
)

// S 把逻辑像素换算为物理像素
func S(v int) int32 { return int32(float64(v)*dpiScale + 0.5) }

func createFont(logical int, bold bool, name string) uintptr {
	weight := uintptr(400)
	if bold {
		weight = 700
	}
	h, _, _ := pCreateFont.Call(
		uintptr(S(logical)), 0, 0, 0, weight, 0, 0, 0,
		1 /*DEFAULT_CHARSET*/, 0, 0, 0, 0,
		uintptr(unsafe.Pointer(mustU16(name))))
	return h
}

func mustU16(s string) *uint16 { p, _ := windows.UTF16PtrFromString(s); return p }

// DrawTextW / GetTextExtentPoint32W 收的是 UTF-16 单元数，不是 UTF-8 字节数。
// 中文按钮文案用 len() 会多算近一倍，文字宽度量错导致整体偏移。
func u16Len(s string) int32 { return int32(len(utf16.Encode([]rune(s)))) }

// ---------- 控件工厂：栅格只出现一次，杜绝手写坐标 ----------

func mkStatic(text string, lx, ly, lw, lh int, font uintptr) windows.HWND {
	t := mustU16(text)
	r, _, _ := pCreateWindowEx.Call(0, uintptr(unsafe.Pointer(mustU16("STATIC"))), uintptr(unsafe.Pointer(t)),
		WS_CHILD, uintptr(S(lx)), uintptr(S(ly)), uintptr(S(lw)), uintptr(S(lh)),
		uintptr(hwndMain), 0, 0, 0)
	pSendMessage.Call(r, WM_SETFONT, font, 1)
	return windows.HWND(r)
}

func mkButton(text string, lx, ly, lw, lh int) windows.HWND {
	t := mustU16(text)
	r, _, _ := pCreateWindowEx.Call(0, uintptr(unsafe.Pointer(mustU16("BUTTON"))), uintptr(unsafe.Pointer(t)),
		WS_CHILD|BS_OWNERDRAW, uintptr(S(lx)), uintptr(S(ly)), uintptr(S(lw)), uintptr(S(lh)),
		uintptr(hwndMain), 0, 0, 0)
	// 悬浮/手型光标 subclass：所有按钮共用一个 wndproc
	h := windows.HWND(r)
	// GWLP_WNDPROC = -4，先存变量再转 uintptr，否则常量转换报溢出
	nidx := int32(GWLP_WNDPROC)
	op, _, _ := pSetWindowLong.Call(uintptr(h), uintptr(nidx), uintptr(windows.NewCallback(btnSubclass)))
	btnOrigProcs[h] = op
	return h
}

func mkEdit(lx, ly, lw, lh int) windows.HWND {
	// ponytail: 不用 ES_PASSWORD——多行密码框会吃掉 WM_SETCURSOR 复位，导致光标卡在手型不复位
	r, _, _ := pCreateWindowEx.Call(0, uintptr(unsafe.Pointer(mustU16("EDIT"))), 0,
		WS_CHILD|ES_MULTILINE|ES_AUTOVSCROLL,
		// 控件四周各让 1px 缝给父窗口自绘描边：左右是 x/宽，上下是 y/高。
		// EDIT 是不透明子窗口，上下不让缝就会盖掉描边的顶/底边（老 bug：输入框只有左右两条边线）。
		uintptr(S(lx)+S(1)), uintptr(S(ly)+S(1)), uintptr(S(lw)-S(2)), uintptr(S(lh)-S(2)),
		uintptr(hwndMain), 0, 0, 0)
	pSendMessage.Call(r, WM_SETFONT, fEdit, 1)
	// 输入区左右内边距：文本不贴灰框边缘
	pSendMessage.Call(r, EM_SETMARGINS, EC_LEFTMARGIN|EC_RIGHTMARGIN, uintptr(S(14))|uintptr(S(14))<<16)
	// 挂子类：鼠标在编辑框上移动时，主窗口收不到消息，hover 高亮会粘住不放
	h := windows.HWND(r)
	nidx := int32(GWLP_WNDPROC)
	op, _, _ := pSetWindowLong.Call(uintptr(h), uintptr(nidx), uintptr(windows.NewCallback(editSubclassProc)))
	editOrigProc = op
	return h
}

func setText(h windows.HWND, s string) {
	pSetWindowText.Call(uintptr(h), uintptr(unsafe.Pointer(mustU16(s))))
}
func getText(h windows.HWND) string {
	var buf [8192]uint16
	pGetWindowText.Call(uintptr(h), uintptr(unsafe.Pointer(&buf[0])), 8192)
	return windows.UTF16ToString(buf[:])
}

// ---------- 绘制基件 ----------

func fillRect(hdc uintptr, r *RECT, clr uint32) {
	b, _, _ := pCreateSolid.Call(uintptr(clr))
	pFillRect.Call(hdc, uintptr(unsafe.Pointer(r)), b)
	pDeleteObject.Call(b)
}

func mkPen(clr uint32) uintptr {
	p, _, _ := pCreatePen.Call(0, 1, uintptr(clr))
	return p
}
func mkb(clr uint32) windows.Handle {
	r, _, _ := pCreateSolid.Call(uintptr(clr))
	return windows.Handle(r)
}

// 画一个纯色实心小圆，用于状态指示点。
// ● 字符在不同字体里大小位置都不一样，自绘才能保证像素级对齐。
func dot(hdc uintptr, cx, cy, r int32, clr uint32) {
	// Ellipse 会用 DC 当前笔画轮廓（默认 BLACK_PEN），必须换成 NULL_PEN
	// 只留刷子填充，否则状态点外圈永远套一圈黑线。
	np, _, _ := pGetStockObj.Call(8) // NULL_PEN
	onp, _, _ := pSelectObject.Call(hdc, np)
	br, _, _ := pCreateSolid.Call(uintptr(clr))
	ob, _, _ := pSelectObject.Call(hdc, br)
	pEllipse.Call(hdc, uintptr(cx-r), uintptr(cy-r), uintptr(cx+r+1), uintptr(cy+r+1))
	pSelectObject.Call(hdc, ob, 0)
	pSelectObject.Call(hdc, onp, 0)
	pDeleteObject.Call(br)
}

// 三段渐淡的软阴影：贴在元素下缘，只往下不出左右头。
// 1px 硬色带在浅底上是一条「脏线」，三段过渡才是「浮起」。
func softShadow(hdc uintptr, x, y, w int32) {
	fillRect(hdc, &RECT{x, y, x + w, y + 1}, 0xEDEFF3) // #F3EFED
	fillRect(hdc, &RECT{x, y + 1, x + w, y + 2}, 0xE9EBF0)
	fillRect(hdc, &RECT{x, y + 2, x + w, y + 3}, 0xE2E5EA) // #EAE5E2
}

// 整个窗口只有一个地方决定光标形状：手型覆盖在可交互元素上，其余一律箭头。
// 各处分散 SetCursor 会导致离开热区后光标不复位。
var hoverPointed bool // 当前是否处于手型区域（按钮/标题栏按钮）

func applyCursor() {
	want := hoverBtn != 0 || hoverMin || hoverClose
	if want == hoverPointed {
		return
	}
	hoverPointed = want
	if want {
		if hCursorHand != 0 {
			pSetCursor.Call(hCursorHand)
		}
	} else if hCursorArrow != 0 {
		pSetCursor.Call(hCursorArrow)
	}
}

// ---------- 鼠标日志 ----------
// 坐标全部是相对按钮客户区的物理像素。移动事件做 16px 网格去重，避免刷屏。
var lastMoveLog windows.HWND
var lastMoveX, lastMoveY int32

func mouseLog(h windows.HWND, what string, lParam uintptr) {
	x := int32(int16(lParam & 0xFFFF))
	y := int32(int16(lParam >> 16))
	if what == "移动" {
		if h == lastMoveLog && x>>4 == lastMoveX>>4 && y>>4 == lastMoveY>>4 {
			return
		}
		lastMoveLog, lastMoveX, lastMoveY = h, x, y
	} else {
		lastMoveLog = 0
	}
	log.Printf("[MOUSE] %s 按钮=%s HWND=0x%x 坐标=(%d,%d)", what, btnName(h), uintptr(h), x, y)
}

// 按钮在主窗口客户区里的逻辑矩形，创建时登记一次。
type rectSpec struct {
	h            windows.HWND
	x, y, w, hgt int
}

var btnRects []rectSpec

// 主窗口坐标 → 命中的按钮（不在任何按钮上返回 0）。
// 不查子窗口，因为子窗口被隐藏（切页）时 GetWindowRect 仍返回旧位置。
func btnAt(x, y int32) windows.HWND {
	for _, r := range btnRects {
		visible := false
		if page == 1 && r.h == hBtnLogin {
			visible = true
		}
		if page == 2 && (r.h == hBtnConn || r.h == hBtnSwitch) {
			visible = true
		}
		if !visible {
			continue
		}
		if x >= S(r.x) && x < S(r.x+r.w) && y >= S(r.y) && y < S(r.y+r.hgt) {
			return r.h
		}
	}
	return 0
}

// hover 变化的唯一入口：旧按钮重绘掉高亮，新按钮重绘上高亮。
func setHoverBtn(h windows.HWND) {
	if h == hoverBtn {
		return
	}
	if hoverBtn != 0 {
		invalidateSelf(hoverBtn)
	}
	hoverBtn = h
	if h != 0 {
		invalidateSelf(h)
	}
}

// 编辑框也是子窗口：鼠标在上面移动时主窗口收不到 WM_MOUSEMOVE，
// hover 高亮会粘住不放。这里把坐标换算到主窗口客户区后走同一条 setHoverBtn。
var editOrigProc uintptr
var editFocused bool // 输入框聚焦时描边变 accent，给键入位置一个可见落点

func editSubclassProc(hwnd windows.HWND, msg uint32, wParam, lParam uintptr) uintptr {
	switch msg {
	case 0x0200, 0x02A3: // WM_MOUSEMOVE / WM_MOUSELEAVE
		var rc, mc RECT
		pGetWindowRect.Call(uintptr(hwnd), uintptr(unsafe.Pointer(&rc)))
		pGetWindowRect.Call(uintptr(hwndMain), uintptr(unsafe.Pointer(&mc)))
		px := int32(int16(lParam&0xFFFF)) + rc.L - mc.L
		py := int32(int16(lParam>>16)) + rc.T - mc.T
		if msg == 0x02A3 {
			px, py = -1, -1
		}
		setHoverBtn(btnAt(px, py))
		applyCursor()
	case 0x0007, 0x0008: // WM_SETFOCUS / WM_KILLFOCUS
		editFocused = msg == 0x0007
		// 输入框矩形整条 invalidate，让 paint() 重画描边
		pInvalidate.Call(uintptr(hwndMain), uintptr(unsafe.Pointer(&RECT{S(LM), S(EDIT_Y), S(LM + LCW), S(EDIT_Y + EDIT_H) + S(3)})), 1)
	}
	r, _, _ := pCallWindowProc.Call(editOrigProc, uintptr(hwnd), uintptr(msg), wParam, lParam)
	return r
}

func btnName(h windows.HWND) string {
	switch h {
	case hBtnLogin:
		return "登录节点"
	case hBtnConn:
		return "启动加速"
	case hBtnSwitch:
		return "更换节点"
	}
	return "?"
}

// 鼠标是否落在标题栏按钮区（坐标相对主窗口客户区）
func titleBtnAt(x, y int32) (min, close bool) {
	WW := S(LWW)
	return y < S(LTITLE) && x >= WW-S(88) && x < WW-S(44),
		y < S(LTITLE) && x >= WW-S(44)
}

// logTitleMouse 记录标题栏按钮的鼠标事件，坐标是相对主窗口客户区
func logTitleMouse(what string, x, y int32) {
	min, close := titleBtnAt(x, y)
	name := "窗口区"
	if close {
		name = "关闭"
	} else if min {
		name = "最小化"
	}
	log.Printf("[MOUSE] %s 标题栏=%s 坐标=(%d,%d)", what, name, x, y)
}

// ---------- 按钮交互 ----------

func btnSubclass(hwnd windows.HWND, msg uint32, wParam, lParam uintptr) uintptr {
	switch msg {
	case 0x000F: // WM_PAINT —— 整个按钮自绘，绝不放给标准 BUTTON 类
		// 标准 BUTTON 类在 WM_PAINT 里会先用 COLOR_BTNFACE 刷一遍原生脸，
		// 而且这条路径下它的刷脸时机不受 owner-draw 控制（实测 Wine 上刷在
		// WM_DRAWITEM 之后），结果就是点击后按钮被原生灰盖掉、只剩描边。
		// 稳妥做法：直接在子类里 BeginPaint → 自绘 → EndPaint，压根不给原生机会。
		var ps PAINTSTRUCT
		hdc, _, _ := pBeginPaint.Call(uintptr(hwnd), uintptr(unsafe.Pointer(&ps)))
		var rc RECT
		pGetClientRect.Call(uintptr(hwnd), uintptr(unsafe.Pointer(&rc)))
		drawFlatButton(&DRAWITEMSTRUCT{
			CtlType:  4, // ODT_BUTTON
			HwndItem: hwnd,
			HDC:      hdc,
			RcItem:   rc,
		})
		pEndPaint.Call(uintptr(hwnd), uintptr(unsafe.Pointer(&ps)))
		return 0
	case 0x0014: // WM_ERASEBKGND
		// 标准 BUTTON 类会用 COLOR_BTNFACE 擦背景，这一刷正好是系统按钮灰。
		// 整个按钮都是 owner-draw 自绘的，一行原生像素都不该有，否则点击后
		// 会出现「刷了灰但 WM_DRAWITEM 没跟着来」→ 按钮卡在白底蓝边。
		return 1 // 1 = 已处理，不擦背景
	case 0x0020: // WM_SETCURSOR
		applyCursor()
		return 1
	case 0x0200: // WM_MOUSEMOVE
		if hoverBtn != hwnd {
			hoverBtn = hwnd
			invalidateSelf(hwnd)
			tme := struct {
				cbSize, dwFlags uint32
				hwndTrack       windows.HWND
				dwHoverTime     uint32
			}{16, 0x2, hwnd, 0}
			pTrackMouse.Call(uintptr(unsafe.Pointer(&tme)))
		}
		mouseLog(hwnd, "移动", lParam)
	case 0x0201: // WM_LBUTTONDOWN：按在按钮上，接管 capture，松手前一直收得到鼠标
		pressBtn = hwnd
		pSetCapture.Call(uintptr(hwnd))
		invalidateSelf(hwnd)
		mouseLog(hwnd, "按下", lParam)
	case 0x0202: // WM_LBUTTONUP
		clicked := pressBtn == hwnd
		pressBtn = 0
		pReleaseCapture.Call()
		invalidateSelf(hwnd)
		mouseLog(hwnd, "松开", lParam)
		if clicked {
			mouseLog(hwnd, "点击", lParam)
		}
	case 0x0215: // WM_CAPTURECHANGED：capture 被系统夺走（点击窗口外/弹窗）
		log.Printf("[MOUSE] CAPTURECHANGED 按钮=%s HWND=0x%x 新capture=0x%x", btnName(hwnd), uintptr(hwnd), lParam)
		pressBtn = 0
	case 0x02A3: // WM_MOUSELEAVE
		if hoverBtn == hwnd {
			hoverBtn = 0
			invalidateSelf(hwnd)
		}
		applyCursor()
	}
	r, _, _ := pCallWindowProc.Call(btnOrigProcs[hwnd], uintptr(hwnd), uintptr(msg), wParam, lParam)
	return r
}

// btnStyle 定义一套按钮视觉样式；所有按钮共用同一套渲染路径，杜绝重复 switch。
type btnStyle struct {
	fill, hover, press uint32 // 三态纯色（v7.1 的渐变字段从未被渲染，删除）
	border, txt        uint32
	font               uintptr
}

// 样式表：hover/press 统一在此定义，改主题只动这里。
var btnStyles = map[windows.HWND]btnStyle{}

// 每颗按钮 subclass 前的原 wndproc，回调用
var btnOrigProcs = map[windows.HWND]uintptr{}

// 已连接时“启动加速”变为“断开连接”：红色（危险操作）
func btnStyleFor(h windows.HWND, connected bool) btnStyle {
	st := btnStyles[h]
	if h == hBtnConn && connected {
		st = btnStyle{
			fill: CLR_DANGER, hover: CLR_DANGER_H, press: CLR_DANGER_H,
			border: CLR_DANGER, txt: 0xFFFFFF, font: st.font,
		}
	}
	return st
}

// Fluent 按钮渲染：渐变底 + 1px 内描边 + 顶部高光，三层做出微凸起。
func drawFlatButton(dis *DRAWITEMSTRUCT) {
	label := getText(dis.HwndItem)
	log.Printf("[BTN_RENDER] 按钮=%s HWND=0x%x 宽度=%d 高度=%d 矩形=(%d,%d,%d,%d) scale=%.2f 状态[hover=%v press=%v]",
		label, dis.HwndItem, dis.RcItem.R-dis.RcItem.L, dis.RcItem.B-dis.RcItem.T,
		dis.RcItem.L, dis.RcItem.T, dis.RcItem.R, dis.RcItem.B, dpiScale,
		hoverBtn == dis.HwndItem, pressBtn == dis.HwndItem)

	st := btnStyleFor(dis.HwndItem, connected)
	fill, border, txt := st.fill, st.border, st.txt
	if hoverBtn == dis.HwndItem {
		fill, border = st.hover, st.hover
	}
	press := pressBtn == dis.HwndItem
	if press {
		fill, border = st.press, st.press
	}

	hdc := dis.HDC
	rc := dis.RcItem
	d := S(8) // Fluent 按钮圆角 10px 设计稿，8px 更利落

	// 两步画法，各用各的股件，任何一步都不留 DC 默认黑笔：
	// 1) 实体填充内缩 1px，NULL_PEN（=8，无边框）+ 实心刷，纯填色；
	// 2) 1px 描边画全矩形，HOLLOW_BRUSH（=5，真镂空）+ 边色笔，只留 1px 环。
	// 旧写法两处都踩坑：CreateHatchBrush(1,0) 冒充 BS_NULL（参数其实是 HS_VERTICAL
	// 竖纹样式，出来是黑纹刷）；填充步不换笔，DC 默认 BLACK_PEN 在 B-1 行漏一条黑线。
	const HOLLOW_BRUSH, NULL_PEN = 5, 8
	hb, _, _ := pGetStockObj.Call(HOLLOW_BRUSH)
	np, _, _ := pGetStockObj.Call(NULL_PEN)
	bb, _, _ := pCreateSolid.Call(uintptr(fill))
	onp, _, _ := pSelectObject.Call(hdc, np)
	ob, _, _ := pSelectObject.Call(hdc, bb)
	pRoundRect.Call(hdc, uintptr(rc.L+S(1)), uintptr(rc.T+S(1)), uintptr(rc.R-S(1)), uintptr(rc.B-S(1)), uintptr(d), uintptr(d))
	pSelectObject.Call(hdc, onp, 0)
	pSelectObject.Call(hdc, ob, 0)

	pen, _, _ := pCreatePen.Call(0, 1, uintptr(border))
	op, _, _ := pSelectObject.Call(hdc, hb)
	op2, _, _ := pSelectObject.Call(hdc, pen)
	pRoundRect.Call(hdc, uintptr(rc.L), uintptr(rc.T), uintptr(rc.R), uintptr(rc.B), uintptr(d), uintptr(d))
	pSelectObject.Call(hdc, op2)
	pSelectObject.Call(hdc, op, 0)
	pDeleteObject.Call(pen)
	pDeleteObject.Call(bb)

	// 顶部高光：按钮上缘 1px 亮线，Fluent 微凸起的核心。
	if !press {
		hl := RECT{rc.L + d/2, rc.T + 1, rc.R - d/2, rc.T + 2}
		fillRect(hdc, &hl, 0x00F5E6D9) // #D9E6F5 上缘微亮白
	}

	// 焦点环：键盘 Tab 选中时画一圈内描边
	if dis.ItemState&0x10 != 0 { // ODS_FOCUS
		fp, _, _ := pCreatePen.Call(0, 1, uintptr(uint32(0xFFFFFF)))
		ofp, _, _ := pSelectObject.Call(hdc, fp)
		ir := rc
		ir.L += S(3)
		ir.R -= S(3)
		ir.T += S(3)
		ir.B -= S(3)
		pRoundRect.Call(hdc, uintptr(ir.L), uintptr(ir.T), uintptr(ir.R), uintptr(ir.B), uintptr(d), uintptr(d))
		pSelectObject.Call(hdc, ofp)
		pDeleteObject.Call(fp)
	}

	pSetBkMode.Call(hdc, 1)
	pSetTextColor.Call(hdc, uintptr(txt))
	pSelectObject.Call(hdc, st.font)
	tp := mustU16(label)
	// 垂直居中按实测字高算，不靠 DT_VCENTER（Wine 缺中文字体时它按错误行高对齐）。
	w := u16Len(label)
	var size struct{ Cx, Cy int32 }
	pGetTextExtent.Call(hdc, uintptr(unsafe.Pointer(tp)), uintptr(w), uintptr(unsafe.Pointer(&size)))
	tr := rc
	if press {
		// 1px tactile click offset
		tr.T += S(1)
		tr.B += S(1)
	}
	tr.T = (tr.T+tr.B-size.Cy)/2 + S(1) // 手写垂直居中，S(1) 是视觉补偿
	tr.B = tr.T + size.Cy
	pDrawText.Call(hdc, uintptr(unsafe.Pointer(tp)), uintptr(w), uintptr(unsafe.Pointer(&tr)), DT_CENTER|DT_SINGLELINE)
}

// invalidateSelf 作废整个按钮客户区。
// InvalidateRect 第 1 参是窗口、第 2 参才是 RECT 指针。直接把 HWND 当指针传，
// 系统去读非法地址，调用必然失败，按钮就永远不重绘（悬浮态不变色的根因）。
func invalidateSelf(h windows.HWND) {
	var rc RECT
	pGetClientRect.Call(uintptr(h), uintptr(unsafe.Pointer(&rc)))
	pInvalidate.Call(uintptr(h), uintptr(unsafe.Pointer(&rc)), 1)
}

// ---------- 自绘 ----------

func paint(hdc uintptr) {
	var ps PAINTSTRUCT
	hdc, _, _ = pBeginPaint.Call(uintptr(hwndMain), uintptr(unsafe.Pointer(&ps)))
	WW, WH := S(LWW), S(LWH)
	TL, BA := S(LTITLE), S(LBAR)
	X0, LXW := S(LX0), S(LXW)

	// 基底：Mica 开启时这是兜底色（透出桌面），关闭时就是窗口纯色底
	var bg RECT
	bg.L, bg.T, bg.R, bg.B = 0, 0, WW, WH
	fillRect(hdc, &bg, CLR_BASE)

	// 标题栏：与 Mica 融为一体，不画任何分割线
	tr := RECT{0, 0, WW, TL}
	fillRect(hdc, &tr, CLR_BASE)

	// 左侧微徽标：外圈光晕 + 内核实心，单圆点太平
	iconClr := uint32(CLR_ACCENT)
	haloClr := uint32(0xE8F1FB)
	brush, _, _ := pSelectObject.Call(hdc, uintptr(mkb(haloClr)))
	penHalo, _, _ := pSelectObject.Call(hdc, uintptr(mkPen(haloClr)))
	hx, hy, hsz := S(14), TL/2-S(10), S(20)
	pRoundRect.Call(hdc, uintptr(hx), uintptr(hy), uintptr(hx+hsz), uintptr(hy+hsz), uintptr(hsz), uintptr(hsz))
	pSelectObject.Call(hdc, brush, 0)
	pSelectObject.Call(hdc, penHalo)
	brush, _, _ = pSelectObject.Call(hdc, uintptr(mkb(iconClr)))
	penIn, _, _ := pSelectObject.Call(hdc, uintptr(mkPen(iconClr)))
	ix, iy, isz := S(18), TL/2-S(6), S(12)
	pRoundRect.Call(hdc, uintptr(ix), uintptr(iy), uintptr(ix+isz), uintptr(iy+isz), uintptr(isz), uintptr(isz))
	pSelectObject.Call(hdc, brush, 0)
	pSelectObject.Call(hdc, penIn)

	pSetBkMode.Call(hdc, 1)
	pSetTextColor.Call(hdc, uintptr(CLR_TXT))
	pSelectObject.Call(hdc, fAppTitle)
	// 标题栏文字按实测字高垂直居中，与登录按钮同一套算法。
	var tsize struct{ Cx, Cy int32 }
	titleStr := "客户端"
	tt := mustU16(titleStr)
	tw := u16Len(titleStr)
	pGetTextExtent.Call(hdc, uintptr(unsafe.Pointer(tt)), uintptr(tw), uintptr(unsafe.Pointer(&tsize)))
	ttr := RECT{S(38), (TL-tsize.Cy)/2 + S(1), WW - S(96), 0}
	ttr.B = ttr.T + tsize.Cy
	pDrawText.Call(hdc, uintptr(unsafe.Pointer(tt)), uintptr(tw), uintptr(unsafe.Pointer(&ttr)), DT_SINGLELINE)

	// 最小化按钮区：Fluent 是悬浮微灰、无图标描边变化
	mr := RECT{WW - S(88), 0, WW - S(44), TL}
	if pressTitle == 1 {
		fillRect(hdc, &mr, 0xE5E7EC) // 按下深一档
	} else if hoverMin {
		fillRect(hdc, &mr, 0xEDEFF3) // 悬浮微灰
	}
	minPenClr := uint32(0x4A4A4A)
	tmpen, _, _ := pCreatePen.Call(0, 1, uintptr(minPenClr))
	mop, _, _ := pSelectObject.Call(hdc, tmpen)
	mx1, mx2, my := WW-S(71), WW-S(61), TL/2+S(3)
	pMoveToEx.Call(hdc, uintptr(mx1), uintptr(my), 0)
	pLineTo.Call(hdc, uintptr(mx2), uintptr(my))
	pSelectObject.Call(hdc, mop)
	pDeleteObject.Call(tmpen)

	// 关闭按钮区：悬浮才变红，Fluent 标准行为
	cr := RECT{WW - S(44), 0, WW, TL}
	xPenClr := uint32(0x4A4A4A)
	if pressTitle == 2 {
		fillRect(hdc, &cr, CLR_DANGER_H) // 按下更深红
		xPenClr = 0xFFFFFF
	} else if hoverClose {
		fillRect(hdc, &cr, CLR_DANGER) // 经典关闭红底
		xPenClr = 0xFFFFFF             // 纯白交叉叉号
	}
	xpen, _, _ := pCreatePen.Call(0, 1, uintptr(xPenClr))
	xop, _, _ := pSelectObject.Call(hdc, xpen)
	cx1, cx2 := WW-S(27), WW-S(17)
	cy1, cy2 := TL/2-S(5), TL/2+S(5)
	pMoveToEx.Call(hdc, uintptr(cx1), uintptr(cy1), 0)
	pLineTo.Call(hdc, uintptr(cx2), uintptr(cy2))
	pMoveToEx.Call(hdc, uintptr(cx2), uintptr(cy1), 0)
	pLineTo.Call(hdc, uintptr(cx1), uintptr(cy2))
	pSelectObject.Call(hdc, xop)
	pDeleteObject.Call(xpen)

	// page1：输入框直接坐 Mica 上，不套卡片。
	// 底部三段渐淡软阴影，让输入框有「浮起」的层级感，替代被删掉的卡片。
	if page == 1 {
		softShadow(hdc, S(LM), S(EDIT_Y)+S(EDIT_H), S(LCW))
		// 输入框自绘描边（替代系统 WS_BORDER 凹陷框），聚焦时变 accent
		edgClr := uint32(CLR_INPUT_B)
		if editFocused {
			edgClr = CLR_ACCENT
		}
		ib, _, _ := pCreateSolid.Call(uintptr(CLR_INPUT))
		ip, _, _ := pCreatePen.Call(0, 1, uintptr(edgClr))
		oib, _, _ := pSelectObject.Call(hdc, ib)
		oip, _, _ := pSelectObject.Call(hdc, ip)
		rd := S(10)
		pRoundRect.Call(hdc, uintptr(S(LM)), uintptr(S(EDIT_Y)), uintptr(S(LM+LCW)), uintptr(S(EDIT_Y+EDIT_H)), uintptr(rd), uintptr(rd))
		pSelectObject.Call(hdc, oib)
		pSelectObject.Call(hdc, oip)
		pDeleteObject.Call(ib)
		pDeleteObject.Call(ip)
	}

	// page2：仪表盘卡片
	if page == 2 {
		cx, cy, cw, ch := S(LM), S(CARD2_Y), S(LCW), S(CARD2_H)
		// 卡片下方三段渐淡软阴影，只往下不出左右头
		softShadow(hdc, cx, cy+ch, cw)
		cb, _, _ := pCreateSolid.Call(uintptr(CLR_CARD))
		cp, _, _ := pCreatePen.Call(0, 1, uintptr(CLR_CARD_B))
		ob, _, _ := pSelectObject.Call(hdc, cb)
		opp, _, _ := pSelectObject.Call(hdc, cp)
		rr := S(12)
		pRoundRect.Call(hdc, uintptr(cx), uintptr(cy), uintptr(cx+cw), uintptr(cy+ch), uintptr(rr), uintptr(rr))
		pSelectObject.Call(hdc, ob, 0)
		pSelectObject.Call(hdc, opp)
		pDeleteObject.Call(cb)
		pDeleteObject.Call(cp)

		// 卡内分割线
		pen3, _, _ := pCreatePen.Call(0, 1, uintptr(0xE3E6EC))
		op3, _, _ := pSelectObject.Call(hdc, pen3)
		dy := uintptr(S(DIV_Y))
		pMoveToEx.Call(hdc, uintptr(X0), dy, 0)
		pLineTo.Call(hdc, uintptr(X0+LXW), dy)
		pSelectObject.Call(hdc, op3)
		pDeleteObject.Call(pen3)

		// 状态指示点：自绘实心圆 + 外圈光晕，替代 ● 字符
		dotClr := uint32(CLR_IDLE)
		if connected {
			dotClr = CLR_SUCCESS
		}
		dcx, dcy := S(LX0)+S(5), S(STATUS_Y)+S(11)
		dot(hdc, dcx, dcy, S(7), uint32(0xE3EAF3)) // 外圈光晕
		dot(hdc, dcx, dcy, S(4), dotClr)           // 内核实心
	}

	// 底栏：延续 Mica，不画实色底，两段渐淡过渡代替 1px 硬线
	softShadow(hdc, 0, WH-BA, WW)
	pEndPaint.Call(uintptr(hwndMain), uintptr(unsafe.Pointer(&ps)))
}

// ---------- 窗口过程 ----------

func wndProc(hwnd windows.HWND, msg uint32, wParam, lParam uintptr) uintptr {
	switch msg {
	case 0x0020: // WM_SETCURSOR
		// wParam 是光标所在窗口。
		// page1（导入节点页）整个窗口拦截光标形状，输入框也强制箭头；
		// page2（仪表盘）没有输入框，只在主窗口和按钮上接管，其余交给系统。
		if page == 1 {
			applyCursor()
			return 1
		}
		switch windows.HWND(wParam) {
		case hwndMain, hBtnLogin, hBtnConn, hBtnSwitch:
			applyCursor()
			return 1
		}
		break
	case 0x0200: // WM_MOUSEMOVE
		x := int32(int16(lParam & 0xFFFF))
		y := int32(int16(lParam >> 16))
		WW := S(LWW)
		newMin, newClose := titleBtnAt(x, y)
		if newMin != hoverMin || newClose != hoverClose {
			hoverMin, hoverClose = newMin, newClose
			tr := RECT{WW - S(88), 0, WW, S(LTITLE)}
			pInvalidate.Call(uintptr(hwnd), uintptr(unsafe.Pointer(&tr)), 1)
			if newMin || newClose {
				logTitleMouse("进入", x, y)
			} else if pressTitle != 0 {
				logTitleMouse("拖出取消", x, y)
			}
		}
		tme := struct {
			cbSize, dwFlags uint32
			hwndTrack       windows.HWND
			dwHoverTime     uint32
		}{16, 0x2, hwnd, 0} // TME_LEAVE
		pTrackMouse.Call(uintptr(unsafe.Pointer(&tme)))
		setHoverBtn(btnAt(x, y))
		applyCursor()
		return 0
	case 0x02A3: // WM_MOUSELEAVE
		if hoverMin || hoverClose {
			logTitleMouse("离开", S(LWW)-S(66), S(LTITLE)/2)
			hoverMin, hoverClose = false, false
			WW := S(LWW)
			tr := RECT{WW - S(88), 0, WW, S(LTITLE)}
			pInvalidate.Call(uintptr(hwnd), uintptr(unsafe.Pointer(&tr)), 1)
		}
		setHoverBtn(0)
		applyCursor()
		return 0
	case WM_NCHITTEST:
		pt := struct{ X, Y int32 }{int32(int16(lParam & 0xFFFF)), int32(int16(lParam >> 16))}
		pScreenToClient.Call(uintptr(hwnd), uintptr(unsafe.Pointer(&pt)))
		if pt.Y < S(LTITLE) && pt.X > S(LWW)-S(88) {
			return 1 // 标题栏按钮区返回 HTCLIENT，接收鼠标移动和点击
		}
		if pt.Y < S(LTITLE) {
			return HTCAPTION
		}
		return 1
	case WM_LBUTTONDOWN:
		x := int32(int16(lParam & 0xFFFF))
		y := int32(int16(lParam >> 16))
		if y < S(LTITLE) {
			WW := S(LWW)
			if x > WW-S(44) {
				logTitleMouse("按下", x, y)
				pressTitle = 2
				pSetCapture.Call(uintptr(hwnd))
				pInvalidate.Call(uintptr(hwnd), uintptr(unsafe.Pointer(&RECT{WW - S(44), 0, WW, S(LTITLE)})), 1)
				return 0
			}
			if x > WW-S(88) {
				logTitleMouse("按下", x, y)
				pressTitle = 1
				pSetCapture.Call(uintptr(hwnd))
				pInvalidate.Call(uintptr(hwnd), uintptr(unsafe.Pointer(&RECT{WW - S(88), 0, WW, S(LTITLE)})), 1)
				return 0
			}
		}
		r, _, _ := pDefWindowProc.Call(uintptr(hwnd), uintptr(msg), wParam, lParam)
		return r
	case WM_LBUTTONUP:
		if pressTitle != 0 {
			x := int32(int16(lParam & 0xFFFF))
			y := int32(int16(lParam >> 16))
			WW := S(LWW)
			pReleaseCapture.Call()
			pressed := pressTitle
			pressTitle = 0
			tr := RECT{WW - S(88), 0, WW, S(LTITLE)}
			pInvalidate.Call(uintptr(hwnd), uintptr(unsafe.Pointer(&tr)), 1)
			min, close := titleBtnAt(x, y)
			if close && pressed == 2 {
				logTitleMouse("点击", x, y)
				pDestroyWindow.Call(uintptr(hwnd))
				return 0
			}
			if min && pressed == 1 {
				logTitleMouse("点击", x, y)
				pSendMessage.Call(uintptr(hwnd), WM_SYSCOMMAND, SC_MINIMIZE, 0)
				return 0
			}
			logTitleMouse("松开(取消)", x, y)
		}
		r, _, _ := pDefWindowProc.Call(uintptr(hwnd), uintptr(msg), wParam, lParam)
		return r
	case WM_PAINT:
		paint(0)
		return 0
	case WM_DRAWITEM:
		dis := (*DRAWITEMSTRUCT)(unsafe.Pointer(lParam))
		if dis.CtlType == 4 {
			drawFlatButton(dis)
			return 1
		}
		return 0
	case WM_COMMAND:
		if lParam != 0 {
			log.Printf("[CMD] 命令触发 按钮=%s HWND=0x%x 通知码=%d", btnName(windows.HWND(lParam)), lParam, wParam>>16)
		}
		switch lParam {
		case uintptr(hBtnLogin):
			handleLogin()
		case uintptr(hBtnSwitch):
			if connected {
				stopProxy()
			}
			switchPage(1)
		case uintptr(hBtnConn):
			go toggleConnect()
		}
		return 0
	case WM_CTLCOLORSTATIC:
		hdc := wParam
		pSetBkMode.Call(hdc, 1)
		t := windows.HWND(lParam)
		for _, pair := range []struct {
			h     windows.HWND
			clr   uint32
			brush windows.Handle
		}{
			{hSpeed, CLR_MUTED, brBase},
			{hStatus, pick(connected, CLR_SUCCESS, CLR_IDLE), brBase},
			{hExpire, CLR_ACCENT, brBase},
			{hTraffic, CLR_TXT, brBase},
			{hUp, CLR_TXT, brBase},
			{hDown, CLR_TXT, brBase},
			{hLblTotal, CLR_MUTED, brBase},
			{hLblUp, CLR_MUTED, brBase},
			{hLblDown, CLR_MUTED, brBase},
			{hTip, CLR_DANGER, brBase},
			{hTitle1, CLR_TXT, brBase},
			{hSub1, CLR_FAINT, brBase},
			{hFooter, CLR_FAINT, brBase},
		} {
			if t == pair.h {
				pSetTextColor.Call(hdc, uintptr(pair.clr))
				return uintptr(pair.brush)
			}
		}
		pSetTextColor.Call(hdc, uintptr(CLR_TXT))
		return uintptr(brBase)
	case WM_CTLCOLOREDIT:
		hdc := wParam
		pSetBkMode.Call(hdc, 2)
		pSetBkColor.Call(hdc, uintptr(CLR_INPUT))
		pSetTextColor.Call(hdc, uintptr(CLR_TXT))
		return uintptr(brInput)
	case WM_DESTROY:
		if connected {
			stopProxy()
		}
		pPostQuit.Call(0)
		return 0
	}
	r, _, _ := pDefWindowProc.Call(uintptr(hwnd), uintptr(msg), wParam, lParam)
	return r
}

func pick(cond bool, a, b uint32) uint32 {
	if cond {
		return a
	}
	return b
}

func switchPage(p int) {
	page = p
	show := func(list []*windows.HWND, s uintptr) {
		for _, h := range list {
			pShowWindow.Call(uintptr(*h), s)
		}
	}
	s1, s2 := uintptr(SW_HIDE), uintptr(SW_HIDE)
	if p == 1 {
		s1 = SW_SHOW
	} else {
		s2 = SW_SHOW
	}
	show(page1, s1)
	show(page2, s2)

	// 切页面时当前悬停按钮可能被隐藏，残留 hoverBtn 会让光标卡在手型
	hoverBtn = 0
	pressBtn = 0
	pressTitle = 0
	hoverPointed = false
	pInvalidate.Call(uintptr(hwndMain), 0, 1)
}

// ---------- 业务（沿用 v6，本次重构未动） ----------

func parseVless(raw string) (uuid, address, port, sni, pbk, sid, flow string, err error) {
	raw = strings.TrimSpace(raw)
	if !strings.HasPrefix(raw, "vless://") {
		return "", "", "", "", "", "", "", fmt.Errorf("not vless")
	}
	u, err := url.Parse(raw)
	if err != nil {
		return
	}
	if u.User == nil || u.User.Username() == "" || u.Hostname() == "" || u.Port() == "" {
		return "", "", "", "", "", "", "", fmt.Errorf("missing uuid, host or port")
	}
	if _, err = strconv.Atoi(u.Port()); err != nil {
		return "", "", "", "", "", "", "", fmt.Errorf("invalid port")
	}
	q := u.Query()
	return u.User.Username(), u.Hostname(), u.Port(), q.Get("sni"), q.Get("pbk"), q.Get("sid"), q.Get("flow"), nil
}

func buildXrayConfig(uuid, address, portStr, sni, pbk, sid, flow string) string {
	port, _ := strconv.Atoi(portStr)
	user := map[string]interface{}{"id": uuid, "encryption": "none"}
	if flow != "" {
		user["flow"] = flow
	}
	conf := map[string]interface{}{
		"log": map[string]interface{}{"loglevel": "warning"},
		"inbounds": []map[string]interface{}{
			{"tag": "socks-in", "port": 10808, "listen": "127.0.0.1", "protocol": "socks", "settings": map[string]interface{}{"auth": "noauth", "udp": true}},
			{"tag": "http-in", "port": 10809, "listen": "127.0.0.1", "protocol": "http", "settings": map[string]interface{}{"allowTransparent": false}},
		},
		"outbounds": []interface{}{
			map[string]interface{}{
				"protocol": "vless",
				"settings": map[string]interface{}{"vnext": []map[string]interface{}{{"address": address, "port": port, "users": []map[string]interface{}{user}}}},
				"streamSettings": map[string]interface{}{
					"network":         "tcp",
					"security":        "reality",
					"realitySettings": map[string]interface{}{"show": false, "fingerprint": "chrome", "serverName": sni, "publicKey": pbk, "shortId": sid, "spiderX": ""},
				},
				"tag": "proxy",
			},
			map[string]interface{}{"protocol": "freedom", "tag": "direct"},
		},
	}
	b, _ := json.Marshal(conf)
	return string(b)
}

func setSystemProxy(enable bool, addr string) {
	k, err := registry.OpenKey(registry.CURRENT_USER, `Software\Microsoft\Windows\CurrentVersion\Internet Settings`, registry.SET_VALUE)
	if err != nil {
		log.Printf("[PROXY] 打开注册表失败: %v", err)
		return
	}
	defer k.Close()
	if enable {
		k.SetDWordValue("ProxyEnable", 1)
		k.SetStringValue("ProxyServer", addr)
		k.SetStringValue("ProxyOverride", "localhost;127.*;10.*;192.168.*;<local>")
	} else {
		k.SetDWordValue("ProxyEnable", 0)
	}
	pInternetSet.Call(0, 39, 0, 0)
	pInternetSet.Call(0, 37, 0, 0)
}

func handleLogin() {
	raw := getText(hEdit)
	log.Printf("[LOGIN] 开始解析 长度=%d", len(raw))
	uuid, address, _, _, _, _, _, err := parseVless(raw)
	if err != nil {
		log.Printf("[LOGIN] 解析失败: %v", err)
		setText(hTip, "链接格式错误，请粘贴以 vless:// 开头的完整节点链接")
		return
	}
	log.Printf("[LOGIN] 解析成功，切页")
	serverUUID = uuid
	serverAddress = address
	if k, _, err := registry.CreateKey(registry.CURRENT_USER, `Software\XPanel`, registry.ALL_ACCESS); err == nil {
		k.SetStringValue("link", raw)
		k.Close()
	}
	setText(hTip, "")
	switchPage(2)
}

func toggleConnect() {
	stateMu.Lock()
	if busy {
		stateMu.Unlock()
		return
	}
	busy = true
	stateMu.Unlock()
	go func() {
		defer func() { stateMu.Lock(); busy = false; stateMu.Unlock() }()
		if connected {
			stopProxy()
		} else {
			startProxy()
		}
	}()
}

func startProxy() {
	uuid, address, port, sni, pbk, sid, flow, err := parseVless(getText(hEdit))
	if err != nil {
		setText(hStatus, "链接格式错误")
		return
	}
	serverUUID = uuid
	serverAddress = address
	setText(hStatus, "正在连接...")
	conf, err := core.LoadConfig("json", bytes.NewReader([]byte(buildXrayConfig(uuid, address, port, sni, pbk, sid, flow))))
	if err != nil {
		setText(hStatus, "配置构建错误")
		return
	}
	srv, err := core.New(conf)
	if err != nil {
		setText(hStatus, "内核启动失败")
		return
	}
	if err := srv.Start(); err != nil {
		setText(hStatus, "端口被占用")
		return
	}
	xray = srv
	setSystemProxy(true, "127.0.0.1:10809")
	connected = true
	setText(hBtnConn, "断开连接")
	setText(hStatus, "已连接加速")
	pInvalidate.Call(uintptr(hwndMain), 0, 1) // 状态点变色
}

func stopProxy() {
	if xray != nil {
		xray.Close()
		xray = nil
	}
	setSystemProxy(false, "")
	connected = false
	speedUp, speedDown, lastUp, lastDown = 0, 0, 0, 0
	setText(hBtnConn, "启动加速")
	setText(hStatus, "未连接")
	pInvalidate.Call(uintptr(hwndMain), 0, 1) // 状态点变色
}

func fmtBytes(n int64) string {
	switch {
	case n >= 1<<30:
		return fmt.Sprintf("%.2f GB", float64(n)/(1<<30))
	case n >= 1<<20:
		return fmt.Sprintf("%.2f MB", float64(n)/(1<<20))
	case n >= 1<<10:
		return fmt.Sprintf("%.2f KB", float64(n)/(1<<10))
	}
	return fmt.Sprintf("%d B", n)
}

func pollTraffic() {
	for {
		time.Sleep(2 * time.Second)
		if serverUUID == "" {
			continue
		}
		host := "23.144.20.121"
		if serverAddress != "" {
			host = serverAddress
		}
		url := fmt.Sprintf("https://%s:1111/clay/panel/pub/traffic/%s", host, serverUUID)
		tr := &http.Transport{
			TLSClientConfig: &tls.Config{InsecureSkipVerify: true},
		}
		client := &http.Client{Transport: tr, Timeout: 5 * time.Second}
		resp, err := client.Get(url)
		if err != nil {
			continue
		}
		body, _ := io.ReadAll(resp.Body)
		resp.Body.Close()
		var res struct {
			Success bool `json:"success"`
			Obj     struct {
				Up, Down, Total int64
				ExpiryTime      interface{}
			} `json:"obj"`
		}
		if json.Unmarshal(body, &res) != nil || !res.Success {
			continue
		}
		if lastUp > 0 || lastDown > 0 {
			speedUp, speedDown = (res.Obj.Up-lastUp)/2, (res.Obj.Down-lastDown)/2
			if speedUp < 0 {
				speedUp = 0
			}
			if speedDown < 0 {
				speedDown = 0
			}
		}
		lastUp, lastDown = res.Obj.Up, res.Obj.Down
		tot := ""
		if res.Obj.Total > 0 {
			tot = " / " + fmtBytes(res.Obj.Total)
		}
		setText(hTraffic, fmtBytes(res.Obj.Up+res.Obj.Down)+tot)
		setText(hUp, fmtBytes(res.Obj.Up))
		setText(hDown, fmtBytes(res.Obj.Down))
		expLine := "到期: 长期有效"
		var exp int64
		switch v := res.Obj.ExpiryTime.(type) {
		case float64:
			exp = int64(v)
		case string:
			exp, _ = strconv.ParseInt(v, 10, 64)
		}
		if exp > 1e11 {
			exp /= 1000
		}
		if exp > 0 {
			t := time.Unix(exp, 0)
			expLine = fmt.Sprintf("到期: %s (%d天)", t.Format("2006/01/02"), int64(time.Until(t).Hours()/24))
		}
		setText(hExpire, expLine)
		setText(hSpeed, fmt.Sprintf("↑ %s/s  ↓ %s/s · 客户端 %s", fmtBytes(speedUp), fmtBytes(speedDown), VER))
	}
}

// ---------- main ----------

func main() {
	runtime.LockOSThread()
	if f, err := os.OpenFile(filepath.Join(filepath.Dir(os.Args[0]), "xpanel.log"), os.O_CREATE|os.O_APPEND|os.O_WRONLY, 0666); err == nil {
		log.SetOutput(f)
		defer f.Close()
	}
	log.SetFlags(log.Ldate | log.Ltime | log.Lmicroseconds | log.Lshortfile)
	defer func() {
		if r := recover(); r != nil {
			log.Printf("[PANIC] %v\n%s", r, debug.Stack())
			panic(r)
		}
	}()

	// DPI：优先 per-monitor v2（需要 manifest，未声明时退化为 system aware，两者都不至于让控件被系统位图拉伸变形）
	if ret, _, _ := pSetProcessDPI.Call(); ret == 0 {
		log.Println("[DPI] SetProcessDPIAware 失败")
	}

	instRaw, _, _ := pGetModule.Call(0)
	inst := windows.Handle(instRaw)

	hIconRaw, _, _ := pLoadIcon.Call(uintptr(inst), 100)
	hIcon := windows.Handle(hIconRaw)

	hCursorHand, _, _ = pLoadCursor.Call(0, 32649)  // IDC_HAND
	hCursorArrow, _, _ = pLoadCursor.Call(0, 32512) // IDC_ARROW
	brBase = mkb(CLR_BASE)
	brCard = mkb(CLR_CARD)
	brInput = mkb(CLR_INPUT)

	wc := WNDCLASSEX{
		CbSize: uint32(unsafe.Sizeof(WNDCLASSEX{})), Style: CS_HREDRAW | CS_VREDRAW | CS_DBLCLKS | CS_DROPSHADOW,
		LpfnWndProc: windows.NewCallback(wndProc), HInstance: inst,
		HIcon: hIcon, HIconSm: hIcon, HbrBackground: brBase,
		LpszClassName: mustU16("XPanelWinV7"),
	}
	pRegisterClass.Call(uintptr(unsafe.Pointer(&wc)))

	WW, WH := S(LWW), S(LWH)
	hRaw, _, _ := pCreateWindowEx.Call(0, uintptr(unsafe.Pointer(mustU16("XPanelWinV7"))), uintptr(unsafe.Pointer(mustU16("客户端"))),
		WS_POPUP|WS_VISIBLE|WS_MINIMIZEBOX|WS_CLIPCHILDREN, 0, 0, uintptr(WW), uintptr(WH), 0, 0, uintptr(inst), 0)
	hwndMain = windows.HWND(hRaw)
	applyRoundCorners(hwndMain)

	// DPI 必须在窗口建立后取，所有坐标随之缩放
	dpi, _, _ := pGetDpiForWin.Call(uintptr(hwndMain), 0)
	if dpi >= 96 {
		dpiScale = float64(dpi) / 96.0
	}
	centerWindow(hwndMain)
	log.Printf("[INIT] %s 启动 窗口=%dx%d dpi=%d scale=%.2f", VER, S(LWW), S(LWH), dpi, dpiScale)

	buildFonts()

	// ===== page1 =====
	hTitle1 = mkStatic("导入节点配置", LM, P1TITLE_Y, LCW, 28, fTitle)
	hSub1 = mkStatic("粘贴 vless:// 节点链接，进入仪表盘", LM, P1SUB_Y, LCW, 20, fSub)
	hEdit = mkEdit(LM, EDIT_Y, LCW, EDIT_H)
	hBtnLogin = mkButton("登录节点", LM, BTN1_Y, LCW, BTN1_H)
	hTip = mkStatic("", LM, TIP_Y, LCW, 22, fSub)

	// ===== page2 =====
	hStatus = mkStatic("未连接", LX0+14, STATUS_Y, LXW/2-14, 24, fStat)
	hExpire = mkStatic("到期: 查询中...", LX0+LXW/2, STATUS_Y, LXW/2, 24, fStat)
	hLblTotal = mkStatic("累计已用流量", LX0, TOTAL_Y, LXW, 18, fSub)
	hTraffic = mkStatic("0.00 MB", LX0, TRAFFIC_Y, LXW, 44, fVal)
	hLblUp = mkStatic("↑ 累计上传", LX0, COL_LBL_Y, LXW/2, 18, fSub)
	hUp = mkStatic("0.00 MB", LX0, COL_VAL_Y, LXW/2, 32, fBig)
	hLblDown = mkStatic("↓ 累计下载", LX0+LXW/2, COL_LBL_Y, LXW/2, 18, fSub)
	hDown = mkStatic("0.00 MB", LX0+LXW/2, COL_VAL_Y, LXW/2, 32, fBig)
	hBtnConn = mkButton("启动加速", LM, BTN2_Y, LCW, BTN2_H)
	hBtnSwitch = mkButton("更换节点", LM, BTN3_Y, LCW, BTN3_H)
	hFooter = mkStatic("系统代理 · 本地 Socks 10808 / Http 10809", LM, FOOTER_Y, LCW, 20, fSub)

	// 按钮样式表：Fluent 三态纯色。
	// accent #0078D4，hover #106EBE，press #005A9E。
	btnStyles[hBtnLogin] = btnStyle{
		fill: CLR_ACCENT, hover: CLR_ACCENT_H, press: CLR_ACCENT_P,
		border: CLR_ACCENT_P, txt: 0xFFFFFF, font: fBtn}
	btnStyles[hBtnConn] = btnStyle{
		fill: CLR_ACCENT, hover: CLR_ACCENT_H, press: CLR_ACCENT_P,
		border: CLR_ACCENT_P, txt: 0xFFFFFF, font: fBtn}
	btnStyles[hBtnSwitch] = btnStyle{
		fill: 0xF4F5F8, hover: 0xECEDF1, press: 0xE2E4E9,
		border: 0xD0D5DD, txt: 0x2B2B2B, font: fStat}

	// ===== 底栏速度 =====
	hSpeed = mkStatic(fmt.Sprintf("↑ 0 B/s  ↓ 0 B/s · 客户端 %s", VER), LM, LWH-27, LCW, 20, fMono)
	pShowWindow.Call(uintptr(hSpeed), SW_SHOW)

	page1 = ptrs(&hTitle1, &hSub1, &hEdit, &hBtnLogin, &hTip)
	page2 = ptrs(&hStatus, &hExpire, &hLblTotal, &hTraffic, &hLblUp, &hUp, &hLblDown, &hDown, &hBtnConn, &hBtnSwitch, &hFooter)

	// 按钮命中矩形登记一次，hover 命中判定只读这张表。
	// 之前 hover 依赖子按钮自己发的 WM_MOUSELEAVE，实测移开鼠标后高亮粘住不放。
	btnRects = []rectSpec{
		{hBtnLogin, LM, BTN1_Y, LCW, BTN1_H},
		{hBtnConn, LM, BTN2_Y, LCW, BTN2_H},
		{hBtnSwitch, LM, BTN3_Y, LCW, BTN3_H},
	}

	if k, err := registry.OpenKey(registry.CURRENT_USER, `Software\XPanel`, registry.QUERY_VALUE); err == nil {
		if s, _, err := k.GetStringValue("link"); err == nil && s != "" {
			setText(hEdit, s)
		}
		k.Close()
	}

	switchPage(1)
	go pollTraffic()
	log.Printf("[BTN_LAYOUT] 栅格规划: 窗口=%dx%d, 边距LM=%d, 统一宽度LCW=%d", LWW, LWH, LM, LCW)
	log.Printf("[BTN_LAYOUT] 按钮1 [登录节点]: 逻辑=(%d,%d) %dx%d | 物理=(%d,%d) %dx%d", LM, BTN1_Y, LCW, BTN1_H, S(LM), S(BTN1_Y), S(LCW), S(BTN1_H))
	log.Printf("[BTN_LAYOUT] 按钮2 [启动加速]: 逻辑=(%d,%d) %dx%d | 物理=(%d,%d) %dx%d", LM, BTN2_Y, LCW, BTN2_H, S(LM), S(BTN2_Y), S(LCW), S(BTN2_H))
	log.Printf("[BTN_LAYOUT] 按钮3 [更换节点]: 逻辑=(%d,%d) %dx%d | 物理=(%d,%d) %dx%d", LM, BTN3_Y, LCW, BTN3_H, S(LM), S(BTN3_Y), S(LCW), S(BTN3_H))
	log.Printf("[BTN_LAYOUT] 标题栏控制 [最小化]: 宽=%d 高=%d | [关闭]: 宽=%d 高=%d", S(44), S(LTITLE), S(44), S(LTITLE))

	log.Println("[INIT] UI 就绪")

	var msg MSG
	for {
		r, _, _ := pGetMessage.Call(uintptr(unsafe.Pointer(&msg)), 0, 0, 0)
		if r == 0 || int32(r) == -1 {
			break
		}
		pTranslateMsg.Call(uintptr(unsafe.Pointer(&msg)))
		pDispatchMsg.Call(uintptr(unsafe.Pointer(&msg)))
	}
}

func centerWindow(hwnd windows.HWND) {
	var rc RECT
	pGetWindowRect.Call(uintptr(hwnd), uintptr(unsafe.Pointer(&rc)))
	ww, wh := rc.R-rc.L, rc.B-rc.T
	scrW, _, _ := pGetSysMetrics.Call(0)
	scrH, _, _ := pGetSysMetrics.Call(1)
	x := (int32(scrW) - ww) / 2
	y := (int32(scrH) - wh) / 2
	if x < 0 {
		x = 0
	}
	if y < 0 {
		y = 0
	}
	pMoveWindow.Call(uintptr(hwnd), uintptr(x), uintptr(y), uintptr(ww), uintptr(wh), 1)
	log.Printf("[BTN_LAYOUT] 居中: 窗口=%dx%d 屏幕=%dx%d 位置=(%d,%d)", ww, wh, scrW, scrH, x, y)
}

func buildFonts() {
	Y := "Microsoft YaHei UI"
	fAppTitle = createFont(14, true, Y)
	fTitle = createFont(20, true, Y)
	fSub = createFont(13, false, Y)
	fEdit = createFont(14, false, Y)
	fBtn = createFont(16, true, Y)
	fStat = createFont(14, true, Y)
	fVal = createFont(36, true, Y)
	fBig = createFont(23, true, Y)
	fMono = createFont(11, false, "Consolas")
}

func ptrs(hs ...*windows.HWND) []*windows.HWND { return hs }
