package main

import (
	"bytes"
	"encoding/json"
	"fmt"
	"io"
	"log"
	"net/http"
	"net/url"
	"os"
	"path/filepath"
	"runtime"
	"runtime/debug"
	"strconv"
	"strings"
	"sync"
	"time"
	"unsafe"

	_ "github.com/xtls/xray-core/main/distro/all"
	core "github.com/xtls/xray-core/core"
	"golang.org/x/sys/windows"
	"golang.org/x/sys/windows/registry"
)

var (
	kernel32      = windows.NewLazySystemDLL("kernel32.dll")
	procGetModule = kernel32.NewProc("GetModuleHandleW")

	user32               = windows.NewLazySystemDLL("user32.dll")
	procCreateWindow     = user32.NewProc("CreateWindowExW")
	procDefWindow        = user32.NewProc("DefWindowProcW")
	procRegisterClass    = user32.NewProc("RegisterClassExW")
	procGetMessage       = user32.NewProc("GetMessageW")
	procTranslate        = user32.NewProc("TranslateMessage")
	procDispatch         = user32.NewProc("DispatchMessageW")
	procSendMessage      = user32.NewProc("SendMessageW")
	procSetWindowText    = user32.NewProc("SetWindowTextW")
	procGetWindowText    = user32.NewProc("GetWindowTextW")
	procPostQuit         = user32.NewProc("PostQuitMessage")
	procInvalidate       = user32.NewProc("InvalidateRect")
	procLoadIcon         = user32.NewProc("LoadIconW")
	procBeginPaint       = user32.NewProc("BeginPaint")
	procEndPaint         = user32.NewProc("EndPaint")
	procShowWindow       = user32.NewProc("ShowWindow")
	procGetSystemMetrics = user32.NewProc("GetSystemMetrics")
	procDrawText         = user32.NewProc("DrawTextW")
	procFillRect         = user32.NewProc("FillRect")
	procScreenToClient   = user32.NewProc("ScreenToClient")
	procDestroyWindow    = user32.NewProc("DestroyWindow")
	procSetDPIAware      = user32.NewProc("SetProcessDPIAware")

	gdi32            = windows.NewLazySystemDLL("gdi32.dll")
	procCreateFont   = gdi32.NewProc("CreateFontW")
	procCreateSolid  = gdi32.NewProc("CreateSolidBrush")
	procSetTextColor = gdi32.NewProc("SetTextColor")
	procSetBkColor   = gdi32.NewProc("SetBkColor")
	procSetBkMode    = gdi32.NewProc("SetBkMode")
	procRoundRect    = gdi32.NewProc("RoundRect")
	procSelectObject = gdi32.NewProc("SelectObject")
	procCreatePen    = gdi32.NewProc("CreatePen")
	procDeleteObject = gdi32.NewProc("DeleteObject")
	procMoveToEx     = gdi32.NewProc("MoveToEx")
	procLineTo       = gdi32.NewProc("LineTo")

	winInet         = windows.NewLazySystemDLL("wininet.dll")
	procInternetSet = winInet.NewProc("InternetSetOptionW")

	hwndMain windows.HWND
	// Page 1 (Login)
	hwndPage1Title, hwndPage1Sub, hwndEdit, hwndBtnLogin, hwndLoginTip windows.HWND
	// Page 2 (Dashboard)
	hwndStatus, hwndExpire, hwndLblTotal, hwndTraffic, hwndLblUp, hwndUp, hwndLblDown, hwndDown windows.HWND
	hwndBtnConnect, hwndBtnSwitch, hwndFooterDesc                                                windows.HWND
	// Shared
	hwndSpeedBar windows.HWND

	hFontAppTitle, hFontTitle, hFontSub, hFontEdit, hFontBtn, hFontStat, hFontVal, hFontBig, hFontMono uintptr
	hBrushBg, hBrushCard, hBrushBar, hBrushEdit                                                        windows.Handle
	hPenCard, hPenBorder                                                                               windows.Handle

	currentPage = 1
	xray_server *core.Instance
	connected   bool
	serverUUID  string
	stateMu     sync.Mutex
	busy        bool
	speedUp, speedDown, lastUp, lastDown int64
)

const (
	WS_POPUP       = 0x80000000
	WS_VISIBLE     = 0x10000000
	WS_CHILD       = 0x40000000
	WS_MINIMIZEBOX = 0x00020000
	BS_OWNERDRAW   = 0x0000000B
	ES_MULTILINE   = 0x0004
	ES_AUTOVSCROLL = 0x0040
	WS_BORDER      = 0x00800000

	WM_DESTROY        = 0x0002
	WM_PAINT          = 0x000F
	WM_COMMAND        = 0x0111
	WM_DRAWITEM       = 0x002B
	WM_SETFONT        = 0x0030
	WM_CTLCOLORSTATIC = 0x0138
	WM_CTLCOLOREDIT   = 0x0133
	WM_NCHITTEST      = 0x0084
	WM_SYSCOMMAND     = 0x0112
	WM_LBUTTONDOWN    = 0x0201
	SW_HIDE           = 0
	SW_SHOW           = 5
	HTCAPTION         = 2

	// 紧凑黄金比例 450x490
	WIN_W   = 450
	WIN_H   = 490
	TITLE_H = 46
	BAR_H   = 36

	// 浅色主题（COLORREF = 0x00BBGGRR，注意 BGR 字节序，勿按 #RRGGBB 直写）
	CLR_BG        = 0xFAF9F8 // #F8F9FA
	CLR_CARD      = 0xFFFFFF
	CLR_BORDER    = 0xEBE7E5 // #E5E7EB
	CLR_INPUT_BG  = 0xF6F4F3 // #F3F4F6
	CLR_TXT_MAIN  = 0x271811 // #111827
	CLR_TXT_MUTED = 0x80726B // #6B7280
	CLR_TXT_FAINT = 0xAFA39C // #9CA3AF
	CLR_ACCENT    = 0xFF765B // #5B76FF v2rayN 紫蓝
	CLR_ACCENT_D  = 0xE85B47 // #475BE8
	CLR_SUCCESS   = 0x81B910 // #10B981
	CLR_DANGER    = 0x4444EF // #EF4444
	CLR_BAR_BG    = 0xFFFFFF

	PANEL_BASE = "http://67.215.255.156:13688"
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
	RcPaint     struct{ Left, Top, Right, Bottom int32 }
	FRestore    int32
	FIncUpdate  int32
	RgbReserved [32]byte
}

type DRAWITEMSTRUCT struct {
	CtlType    uint32
	CtlID      uint32
	ItemID     uint32
	ItemAction uint32
	ItemState  uint32
	HwndItem   windows.HWND
	HDC        uintptr
	RcItem     struct{ Left, Top, Right, Bottom int32 }
	ItemData   uintptr
}

func setSystemProxy(enable bool, proxyAddr string) {
	log.Printf("[PROXY] 配置系统代理开关: enable=%v, addr=%s", enable, proxyAddr)
	k, err := registry.OpenKey(registry.CURRENT_USER, `Software\Microsoft\Windows\CurrentVersion\Internet Settings`, registry.SET_VALUE)
	if err != nil {
		log.Printf("[PROXY] [ERROR] 打开注册表失败: %v", err)
		return
	}
	defer k.Close()
	if enable {
		if err := k.SetDWordValue("ProxyEnable", 1); err != nil {
			log.Printf("[PROXY] [ERROR] 写入 ProxyEnable 失败: %v", err)
		}
		if err := k.SetStringValue("ProxyServer", proxyAddr); err != nil {
			log.Printf("[PROXY] [ERROR] 写入 ProxyServer 失败: %v", err)
		}
		if err := k.SetStringValue("ProxyOverride", "localhost;127.*;10.*;192.168.*;<local>"); err != nil {
			log.Printf("[PROXY] [ERROR] 写入 ProxyOverride 失败: %v", err)
		}
	} else {
		if err := k.SetDWordValue("ProxyEnable", 0); err != nil {
			log.Printf("[PROXY] [ERROR] 禁用 ProxyEnable 失败: %v", err)
		}
	}
	procInternetSet.Call(0, 39, 0, 0)
	procInternetSet.Call(0, 37, 0, 0)
	log.Printf("[PROXY] 系统代理生效设置完成")
}

func switchPage(page int) {
	log.Printf("[UI] 页面切换 -> Page %d", page)
	currentPage = page
	show1, show2 := SW_HIDE, SW_HIDE
	if page == 1 {
		show1 = SW_SHOW
	} else {
		show2 = SW_SHOW
	}
	for _, h := range []windows.HWND{hwndPage1Title, hwndPage1Sub, hwndEdit, hwndBtnLogin, hwndLoginTip} {
		procShowWindow.Call(uintptr(h), uintptr(show1))
	}
	for _, h := range []windows.HWND{hwndStatus, hwndExpire, hwndLblTotal, hwndTraffic, hwndLblUp, hwndUp, hwndLblDown, hwndDown, hwndBtnConnect, hwndBtnSwitch, hwndFooterDesc} {
		procShowWindow.Call(uintptr(h), uintptr(show2))
	}
	procInvalidate.Call(uintptr(hwndMain), 0, 1)
}

func paintCard(hdc uintptr, x, y, w, h int32, radius int32) {
	// 双层柔和阴影，营造悬浮立体卡片质感
	s1, _, _ := procCreateSolid.Call(uintptr(0xF5F1EF)) // #EFF1F5 阴影
	var sr1 struct{ L, T, R, B int32 }
	sr1.L, sr1.T, sr1.R, sr1.B = x-1, y+h, x+w+1, y+h+4
	procFillRect.Call(hdc, uintptr(unsafe.Pointer(&sr1)), s1)
	procDeleteObject.Call(s1)

	s2, _, _ := procCreateSolid.Call(uintptr(0xF0E8E5)) // #E5E8F0 阴影
	var sr2 struct{ L, T, R, B int32 }
	sr2.L, sr2.T, sr2.R, sr2.B = x, y+h, x+w, y+h+2
	procFillRect.Call(hdc, uintptr(unsafe.Pointer(&sr2)), s2)
	procDeleteObject.Call(s2)

	brushRaw, _, _ := procCreateSolid.Call(uintptr(CLR_CARD))
	oldB, _, _ := procSelectObject.Call(hdc, brushRaw)
	oldP, _, _ := procSelectObject.Call(hdc, uintptr(hPenCard))
	procRoundRect.Call(hdc, uintptr(x), uintptr(y), uintptr(x+w), uintptr(y+h), uintptr(radius), uintptr(radius))
	procSelectObject.Call(hdc, oldB)
	procSelectObject.Call(hdc, oldP)
	procDeleteObject.Call(brushRaw)
}

func paintStatusBarBg(hdc uintptr) {
	brushRaw, _, _ := procCreateSolid.Call(uintptr(CLR_BAR_BG))
	var r struct{ L, T, R, B int32 }
	r.L, r.T, r.R, r.B = 0, WIN_H-BAR_H, WIN_W, WIN_H
	procFillRect.Call(hdc, uintptr(unsafe.Pointer(&r)), brushRaw)
	procDeleteObject.Call(brushRaw)

	pen, _, _ := procCreatePen.Call(0, 1, uintptr(CLR_BORDER))
	oldP, _, _ := procSelectObject.Call(hdc, pen)
	procMoveToEx.Call(hdc, 0, WIN_H-BAR_H, 0)
	procLineTo.Call(hdc, WIN_W, WIN_H-BAR_H)
	procSelectObject.Call(hdc, oldP)
	procDeleteObject.Call(pen)
}

// 现代化拟态/扁平质感自绘按钮：带按压反馈、高对比度与圆角
func drawFlatButton(dis *DRAWITEMSTRUCT) {
	var text [256]uint16
	procGetWindowText.Call(uintptr(dis.HwndItem), uintptr(unsafe.Pointer(&text[0])), 256)
	label := windows.UTF16ToString(text[:])

	fill, border, txt := uint32(CLR_ACCENT), uint32(CLR_ACCENT), uint32(0xFFFFFF)
	font := hFontBtn
	radius := int32(10)

	switch dis.HwndItem {
	case hwndBtnSwitch:
		fill, border, txt = CLR_CARD, CLR_BORDER, CLR_TXT_MAIN
		font = hFontStat
	case hwndBtnConnect:
		if connected {
			fill, border = CLR_DANGER, CLR_DANGER
		}
	}

	pressed := dis.ItemState&0x0200 != 0
	if pressed {
		switch dis.HwndItem {
		case hwndBtnLogin, hwndBtnConnect:
			if dis.HwndItem == hwndBtnConnect && connected {
				fill = 0xC23540
			} else {
				fill = CLR_ACCENT_D
			}
			border = fill
		case hwndBtnSwitch:
			fill = 0xF1F3F7
		}
	}

	hdc := dis.HDC

	// 未按下时为主按钮底部绘制轻微阴影，增加可点击质感
	if !pressed && (dis.HwndItem == hwndBtnLogin || dis.HwndItem == hwndBtnConnect) {
		btnShadow, _, _ := procCreateSolid.Call(uintptr(0xD0D7E5))
		var bsr struct{ L, T, R, B int32 }
		bsr.L, bsr.T, bsr.R, bsr.B = dis.RcItem.Left+2, dis.RcItem.Bottom-1, dis.RcItem.Right-2, dis.RcItem.Bottom+3
		procFillRect.Call(hdc, uintptr(unsafe.Pointer(&bsr)), btnShadow)
		procDeleteObject.Call(btnShadow)
	}

	brushRaw, _, _ := procCreateSolid.Call(uintptr(fill))
	penRaw, _, _ := procCreatePen.Call(0, 1, uintptr(border))
	oldB, _, _ := procSelectObject.Call(hdc, brushRaw)
	oldP, _, _ := procSelectObject.Call(hdc, penRaw)
	rc := dis.RcItem
	procRoundRect.Call(hdc, uintptr(rc.Left), uintptr(rc.Top), uintptr(rc.Right), uintptr(rc.Bottom), uintptr(radius), uintptr(radius))
	procSelectObject.Call(hdc, oldB)
	procSelectObject.Call(hdc, oldP)
	procDeleteObject.Call(brushRaw)
	procDeleteObject.Call(penRaw)

	procSetBkMode.Call(hdc, 1)
	procSetTextColor.Call(hdc, uintptr(txt))
	procSelectObject.Call(hdc, font)
	ptr, _ := windows.UTF16PtrFromString(label)
	procDrawText.Call(hdc, uintptr(unsafe.Pointer(ptr)), uintptr(len(label)), uintptr(unsafe.Pointer(&rc)), 0x25)
}

func paintTitleBar(hdc uintptr) {
	tb, _, _ := procCreateSolid.Call(uintptr(CLR_CARD))
	var tr struct{ L, T, R, B int32 }
	tr.L, tr.T, tr.R, tr.B = 0, 0, WIN_W, TITLE_H
	procFillRect.Call(hdc, uintptr(unsafe.Pointer(&tr)), tb)
	procDeleteObject.Call(tb)

	pen, _, _ := procCreatePen.Call(0, 1, uintptr(CLR_BORDER))
	oldP, _, _ := procSelectObject.Call(hdc, pen)
	procMoveToEx.Call(hdc, 0, TITLE_H, 0)
	procLineTo.Call(hdc, WIN_W, TITLE_H)
	procSelectObject.Call(hdc, oldP)
	procDeleteObject.Call(pen)

	bar, _, _ := procCreateSolid.Call(uintptr(CLR_ACCENT))
	var br struct{ L, T, R, B int32 }
	br.L, br.T, br.R, br.B = 0, 0, 4, TITLE_H
	procFillRect.Call(hdc, uintptr(unsafe.Pointer(&br)), bar)
	procDeleteObject.Call(bar)

	procSetBkMode.Call(hdc, 1)
	procSetTextColor.Call(hdc, uintptr(CLR_TXT_MAIN))
	procSelectObject.Call(hdc, hFontAppTitle)
	ttRc := tr
	ttRc.L, ttRc.T, ttRc.R, ttRc.B = 18, 0, WIN_W-120, TITLE_H
	tt, _ := windows.UTF16PtrFromString("客户端")
	procDrawText.Call(hdc, uintptr(unsafe.Pointer(tt)), 3, uintptr(unsafe.Pointer(&ttRc)), 0x24)

	procSelectObject.Call(hdc, hFontStat)
	procSetTextColor.Call(hdc, uintptr(CLR_TXT_MUTED))

	mr := tr
	mr.L, mr.T, mr.R, mr.B = WIN_W-80, 0, WIN_W-40, TITLE_H
	mx, _ := windows.UTF16PtrFromString("一")
	procDrawText.Call(hdc, uintptr(unsafe.Pointer(mx)), 1, uintptr(unsafe.Pointer(&mr)), 0x25)

	cr := tr
	cr.L, cr.T, cr.R, cr.B = WIN_W-40, 0, WIN_W, TITLE_H
	cx, _ := windows.UTF16PtrFromString("✕")
	procDrawText.Call(hdc, uintptr(unsafe.Pointer(cx)), 1, uintptr(unsafe.Pointer(&cr)), 0x25)
}

func wndProc(hwnd windows.HWND, msg uint32, wParam, lParam uintptr) uintptr {
	switch msg {
	case WM_NCHITTEST:
		var pt struct{ X, Y int32 }
		pt.X = int32(int16(lParam & 0xFFFF))
		pt.Y = int32(int16(lParam >> 16))
		procScreenToClient.Call(uintptr(hwnd), uintptr(unsafe.Pointer(&pt)))
		if pt.Y < TITLE_H {
			if pt.X > WIN_W-80 {
				return 1
			}
			return HTCAPTION
		}
		return 1

	case WM_LBUTTONDOWN:
		x := int32(int16(lParam & 0xFFFF))
		y := int32(int16(lParam >> 16))
		if y < TITLE_H {
			if x > WIN_W-40 {
				log.Println("[UI] 用户点击关闭按钮，退出程序")
				procDestroyWindow.Call(uintptr(hwnd))
				return 0
			}
			if x > WIN_W-80 {
				log.Println("[UI] 用户点击最小化按钮")
				procSendMessage.Call(uintptr(hwnd), WM_SYSCOMMAND, 0xF020, 0)
				return 0
			}
		}
		r, _, _ := procDefWindow.Call(uintptr(hwnd), uintptr(msg), wParam, lParam)
		return r

	case WM_PAINT:
		var ps PAINTSTRUCT
		hdc, _, _ := procBeginPaint.Call(uintptr(hwnd), uintptr(unsafe.Pointer(&ps)))
		paintTitleBar(hdc)
		paintStatusBarBg(hdc)

		if currentPage == 1 {
			// Page 1 卡片 (x=24, y=108, w=402, h=200)
			paintCard(hdc, 24, 108, WIN_W-48, 200, 12)
		} else {
			// Page 2 卡片 (x=24, y=58, w=402, h=210)
			paintCard(hdc, 24, 58, WIN_W-48, 210, 12)

			// 分割线 (y=172)
			pen, _, _ := procCreatePen.Call(0, 1, uintptr(CLR_BORDER))
			oldP, _, _ := procSelectObject.Call(hdc, pen)
			procMoveToEx.Call(hdc, 40, 172, 0)
			procLineTo.Call(hdc, WIN_W-40, 172)
			procSelectObject.Call(hdc, oldP)
			procDeleteObject.Call(pen)
		}
		procEndPaint.Call(uintptr(hwnd), uintptr(unsafe.Pointer(&ps)))
		return 0

	case WM_DRAWITEM:
		dis := (*DRAWITEMSTRUCT)(unsafe.Pointer(lParam))
		if dis.CtlType == 4 {
			drawFlatButton(dis)
			return 1
		}
		return 0

	case WM_COMMAND:
		switch lParam {
		case uintptr(hwndBtnLogin):
			handleLogin()
		case uintptr(hwndBtnSwitch):
			if connected {
				stopProxy()
			}
			switchPage(1)
		case uintptr(hwndBtnConnect):
			go toggleConnect()
		}
		return 0

	case WM_CTLCOLORSTATIC:
		hdc := wParam
		procSetBkMode.Call(hdc, 1)
		target := windows.HWND(lParam)
		switch target {
		case hwndSpeedBar:
			procSetTextColor.Call(hdc, CLR_SUCCESS)
			return uintptr(hBrushBar)
		case hwndStatus:
			if connected {
				procSetTextColor.Call(hdc, CLR_SUCCESS)
			} else {
				procSetTextColor.Call(hdc, CLR_DANGER)
			}
			return uintptr(hBrushCard)
		case hwndExpire:
			procSetTextColor.Call(hdc, CLR_ACCENT)
			return uintptr(hBrushCard)
		case hwndTraffic, hwndUp, hwndDown:
			procSetTextColor.Call(hdc, CLR_TXT_MAIN)
			return uintptr(hBrushCard)
		case hwndLoginTip:
			procSetTextColor.Call(hdc, CLR_DANGER)
			return uintptr(hBrushBg)
		case hwndLblTotal, hwndLblUp, hwndLblDown:
			procSetTextColor.Call(hdc, CLR_TXT_MUTED)
			return uintptr(hBrushCard)
		case hwndPage1Title:
			procSetTextColor.Call(hdc, CLR_TXT_MAIN)
			return uintptr(hBrushBg)
		case hwndPage1Sub, hwndFooterDesc:
			procSetTextColor.Call(hdc, CLR_TXT_FAINT)
			return uintptr(hBrushBg)
		default:
			procSetTextColor.Call(hdc, CLR_TXT_MAIN)
			return uintptr(hBrushBg)
		}

	case WM_CTLCOLOREDIT:
		hdc := wParam
		procSetBkMode.Call(hdc, 2)
		procSetBkColor.Call(hdc, CLR_INPUT_BG)
		procSetTextColor.Call(hdc, CLR_TXT_MAIN)
		return uintptr(hBrushEdit)

	case WM_DESTROY:
		log.Println("[CORE] 窗口销毁，准备退出...")
		if connected {
			stopProxy()
		}
		procPostQuit.Call(0)
		return 0

	default:
		r, _, _ := procDefWindow.Call(uintptr(hwnd), uintptr(msg), wParam, lParam)
		return r
	}
}

// ---------- 业务逻辑 ----------

func parseVless(raw string) (uuid, address, port, sni, pbk, sid, flow string, err error) {
	raw = strings.TrimSpace(raw)
	log.Printf("[CONFIG] 解析 VLESS 链接: 长度=%d", len(raw))
	if !strings.HasPrefix(raw, "vless://") {
		log.Printf("[CONFIG] [ERROR] 链接缺少 vless:// 前缀")
		return "", "", "", "", "", "", "", fmt.Errorf("not vless")
	}
	u, err := url.Parse(raw)
	if err != nil {
		log.Printf("[CONFIG] [ERROR] URL 解析失败: %v", err)
		return "", "", "", "", "", "", "", err
	}
	if u.User == nil || u.User.Username() == "" || u.Hostname() == "" || u.Port() == "" {
		log.Printf("[CONFIG] [ERROR] 缺少核心参数 (uuid/host/port)")
		return "", "", "", "", "", "", "", fmt.Errorf("missing uuid, host or port")
	}
	if _, err := strconv.Atoi(u.Port()); err != nil {
		log.Printf("[CONFIG] [ERROR] 端口非数字: %s", u.Port())
		return "", "", "", "", "", "", "", fmt.Errorf("invalid port")
	}
	q := u.Query()
	log.Printf("[CONFIG] 节点解析成功: UUID=%s, Server=%s:%s, SNI=%s", u.User.Username(), u.Hostname(), u.Port(), q.Get("sni"))
	return u.User.Username(), u.Hostname(), u.Port(), q.Get("sni"), q.Get("pbk"), q.Get("sid"), q.Get("flow"), nil
}

func buildXrayConfig(uuid, address, portStr, sni, pbk, sid, flow string) string {
	log.Printf("[CONFIG] 构建 Xray 配置 JSON: Host=%s:%s, SNI=%s, PBK=%s", address, portStr, sni, pbk)
	port, _ := strconv.Atoi(portStr)
	fp := "chrome"
	realityObj := map[string]interface{}{
		"show":        false,
		"fingerprint": fp,
		"serverName":  sni,
		"publicKey":   pbk,
		"shortId":     sid,
		"spiderX":     "",
	}
	streamSettings := map[string]interface{}{
		"network":  "tcp",
		"security": "reality",
		"realitySettings": realityObj,
	}
	userObj := map[string]interface{}{
		"id":         uuid,
		"encryption": "none",
	}
	if flow != "" {
		userObj["flow"] = flow
	}
	vlessOut := map[string]interface{}{
		"protocol": "vless",
		"settings": map[string]interface{}{
			"vnext": []map[string]interface{}{
				{
					"address": address,
					"port":    port,
					"users":   []map[string]interface{}{userObj},
				},
			},
		},
		"streamSettings": streamSettings,
		"tag":            "proxy",
	}
	config := map[string]interface{}{
		"log": map[string]interface{}{"loglevel": "warning"},
		"inbounds": []map[string]interface{}{
			{
				"tag":      "socks-in",
				"port":     10808,
				"listen":   "127.0.0.1",
				"protocol": "socks",
				"settings": map[string]interface{}{"auth": "noauth", "udp": true},
			},
			{
				"tag":      "http-in",
				"port":     10809,
				"listen":   "127.0.0.1",
				"protocol": "http",
				"settings": map[string]interface{}{"allowTransparent": false},
			},
		},
		"outbounds": []interface{}{
			vlessOut,
			map[string]interface{}{"protocol": "freedom", "tag": "direct"},
		},
	}
	b, _ := json.Marshal(config)
	return string(b)
}

func setText(h windows.HWND, text string) {
	ptr, _ := windows.UTF16PtrFromString(text)
	procSetWindowText.Call(uintptr(h), uintptr(unsafe.Pointer(ptr)))
}

func getText(h windows.HWND) string {
	var buf [4096]uint16
	procGetWindowText.Call(uintptr(h), uintptr(unsafe.Pointer(&buf[0])), 4096)
	return windows.UTF16ToString(buf[:])
}

func handleLogin() {
	log.Println("[LOGIN] 开始处理登录...")
	raw := getText(hwndEdit)
	uuid, _, _, _, _, _, _, err := parseVless(raw)
	if err != nil {
		log.Printf("[LOGIN] [WARN] 节点链接错误: %v", err)
		setText(hwndLoginTip, "链接格式错误，请粘贴以 vless:// 开头的完整节点链接")
		return
	}
	serverUUID = uuid
	log.Printf("[LOGIN] 记录绑定 UUID: %s", serverUUID)
	if k, _, err := registry.CreateKey(registry.CURRENT_USER, `Software\XPanel`, registry.ALL_ACCESS); err == nil {
		k.SetStringValue("link", raw)
		k.Close()
		log.Println("[LOGIN] 链接已持久化至注册表")
	} else {
		log.Printf("[LOGIN] [WARN] 注册表写入失败: %v", err)
	}
	setText(hwndLoginTip, "")
	switchPage(2)
}

func toggleConnect() {
	stateMu.Lock()
	if busy {
		log.Println("[CONN] 连接操作中，忽略重复触发")
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
	log.Println("[CORE] 准备启动 Xray 核心代理...")
	raw := getText(hwndEdit)
	uuid, address, port, sni, pbk, sid, flow, err := parseVless(raw)
	if err != nil {
		log.Printf("[CORE] [ERROR] 节点解析失败: %v", err)
		setText(hwndStatus, "● 链接格式错误")
		return
	}
	serverUUID = uuid
	setText(hwndStatus, "● 正在连接...")
	confJSON := buildXrayConfig(uuid, address, port, sni, pbk, sid, flow)
	conf, err := core.LoadConfig("json", bytes.NewReader([]byte(confJSON)))
	if err != nil {
		log.Printf("[CORE] [ERROR] Xray 加载配置对象失败: %v", err)
		setText(hwndStatus, "● 配置构建错误")
		return
	}
	server, err := core.New(conf)
	if err != nil {
		log.Printf("[CORE] [ERROR] Xray 实例初始化失败: %v", err)
		setText(hwndStatus, "● 内核启动失败")
		return
	}
	if err := server.Start(); err != nil {
		log.Printf("[CORE] [ERROR] Xray 服务启动失败 (检查端口 10808/10809): %v", err)
		setText(hwndStatus, "● 端口被占用")
		return
	}
	xray_server = server
	log.Println("[CORE] Xray 核心运行成功，正在设置系统代理 127.0.0.1:10809")
	setSystemProxy(true, "127.0.0.1:10809")
	connected = true
	setText(hwndBtnConnect, "断开连接")
	setText(hwndStatus, "● 已连接加速")
	log.Println("[CORE] 代理状态: 已连接加速")
}

func stopProxy() {
	log.Println("[CORE] 正在停止 Xray 代理...")
	if xray_server != nil {
		xray_server.Close()
		xray_server = nil
		log.Println("[CORE] Xray 实例已释放")
	}
	setSystemProxy(false, "")
	connected = false
	speedUp, speedDown, lastUp, lastDown = 0, 0, 0, 0
	setText(hwndBtnConnect, "启动连接")
	setText(hwndStatus, "● 未连接")
	log.Println("[CORE] 代理状态: 已完全断开")
}

func fmtBytes(n int64) string {
	switch {
	case n >= 1<<30:
		return fmt.Sprintf("%.2f GB", float64(n)/(1<<30))
	case n >= 1<<20:
		return fmt.Sprintf("%.2f MB", float64(n)/(1<<20))
	case n >= 1<<10:
		return fmt.Sprintf("%.2f KB", float64(n)/(1<<10))
	default:
		return fmt.Sprintf("%d B", n)
	}
}

func pollTraffic() {
	log.Println("[MONITOR] 流量与速率轮询协程启动...")
	first := true
	for {
		time.Sleep(2 * time.Second)
		if serverUUID == "" {
			continue
		}
		url := PANEL_BASE + "/panel/pub/traffic/" + serverUUID
		resp, err := http.Get(url)
		if err != nil {
			log.Printf("[MONITOR] [WARN] 获取流量失败 %s: %v", url, err)
			continue
		}
		body, _ := io.ReadAll(resp.Body)
		resp.Body.Close()

		var res struct {
			Success bool `json:"success"`
			Obj     struct {
				Up         int64       `json:"up"`
				Down       int64       `json:"down"`
				Total      int64       `json:"total"`
				ExpiryTime interface{} `json:"expiryTime"`
			} `json:"obj"`
		}
		if json.Unmarshal(body, &res) != nil || !res.Success {
			log.Printf("[MONITOR] [WARN] 数据解析失败: %s", string(body))
			continue
		}
		if !first {
			speedUp = (res.Obj.Up - lastUp) / 2
			speedDown = (res.Obj.Down - lastDown) / 2
			if speedUp < 0 {
				speedUp = 0
			}
			if speedDown < 0 {
				speedDown = 0
			}
		}
		lastUp, lastDown = res.Obj.Up, res.Obj.Down
		first = false

		used := res.Obj.Up + res.Obj.Down
		tot := ""
		if res.Obj.Total > 0 {
			tot = " / " + fmtBytes(res.Obj.Total)
		}
		setText(hwndTraffic, fmtBytes(used)+tot)
		setText(hwndUp, fmtBytes(res.Obj.Up))
		setText(hwndDown, fmtBytes(res.Obj.Down))

		expLine := "到期时间: 长期有效"
		var exp int64
		switch v := res.Obj.ExpiryTime.(type) {
		case float64:
			exp = int64(v)
		case string:
			exp, _ = strconv.ParseInt(v, 10, 64)
		}
		if exp > 0 {
			if exp > 1e11 {
				exp = exp / 1000 // 毫秒转秒
			}
			t := time.Unix(exp, 0)
			days := int64(time.Until(t).Hours() / 24)
			expLine = fmt.Sprintf("到期: %s (%d天)", t.Format("2006/01/02"), days)
		}
		setText(hwndExpire, expLine)

		statusText := fmt.Sprintf("↑ %s/s  ↓ %s/s · 客户端 v5.3.1", fmtBytes(speedUp), fmtBytes(speedDown))
		setText(hwndSpeedBar, statusText)
	}
}

func createFont(size int, bold bool, name string) uintptr {
	w := 400
	if bold {
		w = 700
	}
	n, _ := windows.UTF16PtrFromString(name)
	h, _, _ := procCreateFont.Call(uintptr(size), 0, 0, 0, uintptr(w), 0, 0, 0, 1, 0, 0, 0, 0, uintptr(unsafe.Pointer(n)))
	return h
}

func main() {
	runtime.LockOSThread()

	logPath := filepath.Join(filepath.Dir(os.Args[0]), "xpanel.log")
	f, err := os.OpenFile(logPath, os.O_CREATE|os.O_APPEND|os.O_WRONLY, 0666)
	if err != nil {
		// 备用：若同级目录无写权限，写入用户 Temp 目录
		logPath = filepath.Join(os.TempDir(), "xpanel.log")
		f, err = os.OpenFile(logPath, os.O_CREATE|os.O_APPEND|os.O_WRONLY, 0666)
	}
	if err == nil {
		log.SetOutput(f)
		log.SetFlags(log.Ldate | log.Ltime | log.Lmicroseconds | log.Lshortfile)
		defer f.Close()
	}
	defer func() {
		if r := recover(); r != nil {
			log.Printf("[PANIC] %v\n%s", r, debug.Stack())
			panic(r)
		}
	}()
	log.Println("[INIT] 客户端 v5.3.1 启动中 (紧凑 450x490)...")

	procSetDPIAware.Call()

	instRaw, _, _ := procGetModule.Call(0)
	inst := windows.Handle(instRaw)
	className, _ := windows.UTF16PtrFromString("XPanelWinV52")
	wndTitle, _ := windows.UTF16PtrFromString("客户端")

	hIconRaw, _, _ := procLoadIcon.Call(uintptr(inst), 100)
	hIcon := windows.Handle(hIconRaw)

	hBgRaw, _, _ := procCreateSolid.Call(CLR_BG)
	hBrushBg = windows.Handle(hBgRaw)
	hCardRaw, _, _ := procCreateSolid.Call(CLR_CARD)
	hBrushCard = windows.Handle(hCardRaw)
	hBarRaw, _, _ := procCreateSolid.Call(CLR_BAR_BG)
	hBrushBar = windows.Handle(hBarRaw)
	hEditRaw, _, _ := procCreateSolid.Call(CLR_INPUT_BG)
	hBrushEdit = windows.Handle(hEditRaw)

	hPenRaw, _, _ := procCreatePen.Call(0, 1, CLR_BORDER)
	hPenCard = windows.Handle(hPenRaw)

	YAHEI := "Microsoft YaHei UI"
	hFontAppTitle = createFont(15, true, YAHEI)
	hFontTitle = createFont(17, true, YAHEI)
	hFontSub = createFont(12, false, YAHEI)
	hFontEdit = createFont(13, false, YAHEI)
	hFontBtn = createFont(16, true, YAHEI)
	hFontStat = createFont(13, true, YAHEI)
	hFontVal = createFont(32, true, YAHEI)
	hFontBig = createFont(20, true, YAHEI)
	hFontMono = createFont(11, false, "Consolas")

	wc := WNDCLASSEX{
		CbSize:        uint32(unsafe.Sizeof(WNDCLASSEX{})),
		LpfnWndProc:   windows.NewCallback(wndProc),
		HInstance:     inst,
		HIcon:         hIcon,
		HIconSm:       hIcon,
		HbrBackground: hBrushBg,
		LpszClassName: className,
	}
	procRegisterClass.Call(uintptr(unsafe.Pointer(&wc)))

	scrW, _, _ := procGetSystemMetrics.Call(0)
	scrH, _, _ := procGetSystemMetrics.Call(1)
	posX := (int32(scrW) - WIN_W) / 2
	posY := (int32(scrH) - WIN_H) / 2

	log.Printf("[UI] 屏幕分辨率: %dx%d, 窗口尺寸: %dx%d, 居中坐标: (%d, %d)", scrW, scrH, WIN_W, WIN_H, posX, posY)

	hwndRaw, _, _ := procCreateWindow.Call(
		0, uintptr(unsafe.Pointer(className)), uintptr(unsafe.Pointer(wndTitle)),
		WS_POPUP|WS_VISIBLE|WS_MINIMIZEBOX,
		uintptr(posX), uintptr(posY), WIN_W, WIN_H, 0, 0, uintptr(inst), 0,
	)
	hwndMain = windows.HWND(hwndRaw)

	staticClass, _ := windows.UTF16PtrFromString("STATIC")
	editClass, _ := windows.UTF16PtrFromString("EDIT")
	btnClass, _ := windows.UTF16PtrFromString("BUTTON")
	mk := func(text string, x, y, w, h int32, font uintptr) windows.HWND {
		t, _ := windows.UTF16PtrFromString(text)
		r, _, _ := procCreateWindow.Call(0, uintptr(unsafe.Pointer(staticClass)), uintptr(unsafe.Pointer(t)), WS_CHILD, uintptr(x), uintptr(y), uintptr(w), uintptr(h), uintptr(hwndMain), 0, uintptr(inst), 0)
		procSendMessage.Call(r, WM_SETFONT, font, 1)
		return windows.HWND(r)
	}

	// ==================== PAGE 1（登录/导入节点） ====================
	hwndPage1Title = mk("导入节点配置", 24, 62, 300, 24, hFontTitle)
	hwndPage1Sub = mk("粘贴 vless:// 节点链接，进入仪表盘", 24, 86, 402, 18, hFontSub)

	// 输入卡片 (x=24, y=108, w=402, h=200) -> 文本框 (x=38, y=122, w=374, h=172)
	hEdit, _, _ := procCreateWindow.Call(0, uintptr(unsafe.Pointer(editClass)), 0, WS_CHILD|WS_VISIBLE|WS_BORDER|ES_MULTILINE|ES_AUTOVSCROLL, 38, 122, WIN_W-76, 172, uintptr(hwndMain), 0, uintptr(inst), 0)
	hwndEdit = windows.HWND(hEdit)
	procSendMessage.Call(uintptr(hwndEdit), WM_SETFONT, hFontEdit, 1)

	btnLgText, _ := windows.UTF16PtrFromString("登 录 节 点")
	hBtnLg, _, _ := procCreateWindow.Call(0, uintptr(unsafe.Pointer(btnClass)), uintptr(unsafe.Pointer(btnLgText)), WS_CHILD|WS_VISIBLE|BS_OWNERDRAW, 24, 330, WIN_W-48, 48, uintptr(hwndMain), 0, uintptr(inst), 0)
	hwndBtnLogin = windows.HWND(hBtnLg)

	hwndLoginTip = mk("", 24, 390, WIN_W-48, 20, hFontSub)

	// ==================== PAGE 2（仪表盘主面板） ====================
	hwndStatus = mk("● 未连接", 40, 72, 150, 20, hFontStat)
	hwndExpire = mk("到期: 查询中...", 230, 72, 180, 20, hFontStat)

	hwndLblTotal = mk("累计已用流量", 40, 100, 120, 16, hFontSub)

	hTrafficR, _ := windows.UTF16PtrFromString("0.00 MB")
	hTraffic, _, _ := procCreateWindow.Call(0, uintptr(unsafe.Pointer(staticClass)), uintptr(unsafe.Pointer(hTrafficR)), WS_CHILD, 40, 118, 370, 40, uintptr(hwndMain), 0, uintptr(inst), 0)
	hwndTraffic = windows.HWND(hTraffic)
	procSendMessage.Call(uintptr(hwndTraffic), WM_SETFONT, hFontVal, 1)

	// 下半部双列统计 (y=184)
	hwndLblUp = mk("↑ 累计上传", 40, 184, 140, 16, hFontSub)
	hwndUp = mk("0.00 MB", 40, 202, 180, 28, hFontBig)

	hwndLblDown = mk("↓ 累计下载", 230, 184, 140, 16, hFontSub)
	hwndDown = mk("0.00 MB", 230, 202, 180, 28, hFontBig)

	// 连接主按钮
	btnConnText, _ := windows.UTF16PtrFromString("启 动 加 速")
	hBtnConn, _, _ := procCreateWindow.Call(0, uintptr(unsafe.Pointer(btnClass)), uintptr(unsafe.Pointer(btnConnText)), WS_CHILD|BS_OWNERDRAW, 24, 288, WIN_W-48, 50, uintptr(hwndMain), 0, uintptr(inst), 0)
	hwndBtnConnect = windows.HWND(hBtnConn)

	// 次要按钮：切换节点
	btnSwText, _ := windows.UTF16PtrFromString("更换节点")
	hBtnSw, _, _ := procCreateWindow.Call(0, uintptr(unsafe.Pointer(btnClass)), uintptr(unsafe.Pointer(btnSwText)), WS_CHILD|BS_OWNERDRAW, 24, 348, WIN_W-48, 40, uintptr(hwndMain), 0, uintptr(inst), 0)
	hwndBtnSwitch = windows.HWND(hBtnSw)

	// 底部辅助说明
	hwndFooterDesc = mk("系统代理 · 本地 Socks 10808 / Http 10809", 24, 402, WIN_W-48, 18, hFontSub)

	// 底部速度栏
	hSpeedR, _ := windows.UTF16PtrFromString("↑ 0 B/s  ↓ 0 B/s · 客户端 v5.3.1")
	hSpeed, _, _ := procCreateWindow.Call(0, uintptr(unsafe.Pointer(staticClass)), uintptr(unsafe.Pointer(hSpeedR)), WS_CHILD|WS_VISIBLE, 14, WIN_H-27, WIN_W-28, 18, uintptr(hwndMain), 0, uintptr(inst), 0)
	hwndSpeedBar = windows.HWND(hSpeed)
	procSendMessage.Call(uintptr(hwndSpeedBar), WM_SETFONT, hFontMono, 1)

	// 历史节点恢复
	if k, err := registry.OpenKey(registry.CURRENT_USER, `Software\XPanel`, registry.QUERY_VALUE); err == nil {
		if s, _, err := k.GetStringValue("link"); err == nil && s != "" {
			setText(hwndEdit, s)
			log.Println("[INIT] 成功恢复上次使用的节点链接")
		}
		k.Close()
	}

	switchPage(1)
	go pollTraffic()
	log.Println("[INIT] UI 界面初始化完成，进入消息主循环")

	var msg MSG
	for {
		r, _, _ := procGetMessage.Call(uintptr(unsafe.Pointer(&msg)), 0, 0, 0)
		if r == 0 || int32(r) == -1 {
			break
		}
		procTranslate.Call(uintptr(unsafe.Pointer(&msg)))
		procDispatch.Call(uintptr(unsafe.Pointer(&msg)))
	}
}
