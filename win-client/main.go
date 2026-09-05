package main

import (
	"bytes"
	"encoding/json"
	"fmt"
	"io"
	"net/http"
	"net/url"
	"runtime"
	"strings"
	"time"
	"unsafe"

	_ "github.com/xtls/xray-core/main/distro/all"
	core "github.com/xtls/xray-core/core"
	"golang.org/x/sys/windows"
	"golang.org/x/sys/windows/registry"
)

var (
	kernel32          = windows.NewLazySystemDLL("kernel32.dll")
	procGetModule     = kernel32.NewProc("GetModuleHandleW")

	user32            = windows.NewLazySystemDLL("user32.dll")
	procCreateWindow  = user32.NewProc("CreateWindowExW")
	procDefWindow     = user32.NewProc("DefWindowProcW")
	procRegisterClass = user32.NewProc("RegisterClassExW")
	procGetMessage    = user32.NewProc("GetMessageW")
	procTranslate     = user32.NewProc("TranslateMessage")
	procDispatch      = user32.NewProc("DispatchMessageW")
	procSendMessage   = user32.NewProc("SendMessageW")
	procSetWindowText = user32.NewProc("SetWindowTextW")
	procGetWindowText = user32.NewProc("GetWindowTextW")
	procPostQuit      = user32.NewProc("PostQuitMessage")
	procInvalidate    = user32.NewProc("InvalidateRect")
	procLoadIcon      = user32.NewProc("LoadIconW")
	procBeginPaint    = user32.NewProc("BeginPaint")
	procEndPaint      = user32.NewProc("EndPaint")

	gdi32             = windows.NewLazySystemDLL("gdi32.dll")
	procCreateFont    = gdi32.NewProc("CreateFontW")
	procCreateSolid   = gdi32.NewProc("CreateSolidBrush")
	procSetTextColor  = gdi32.NewProc("SetTextColor")
	procSetBkColor    = gdi32.NewProc("SetBkColor")
	procSetBkMode     = gdi32.NewProc("SetBkMode")
	procRoundRect     = gdi32.NewProc("RoundRect")
	procSelectObject  = gdi32.NewProc("SelectObject")
	procCreatePen     = gdi32.NewProc("CreatePen")

	winInet         = windows.NewLazySystemDLL("wininet.dll")
	procInternetSet = winInet.NewProc("InternetSetOptionW")

	hwndMain, hwndEdit, hwndBtn, hwndStatus, hwndTraffic, hwndDetail windows.HWND
	hFontTitle, hFontSub, hFontEdit, hFontBtn, hFontStat, hFontVal uintptr
	hBrushBg, hBrushCard, hBrushInput, hPenBorder, hPenCard, hPenBtn, hBrushBtn windows.Handle

	xrayServer *core.Instance
	connected  bool
	serverUUID string
)

const (
	WS_OVERLAPPED = 0x00000000
	WS_CAPTION    = 0x00C00000
	WS_SYSMENU    = 0x00080000
	WS_MINIMIZEBOX= 0x00020000
	WS_VISIBLE    = 0x10000000
	WS_CHILD      = 0x40000000
	ES_MULTILINE  = 0x0004
	ES_AUTOVSCROLL= 0x0040
	WM_DESTROY    = 0x0002
	WM_PAINT      = 0x000F
	WM_COMMAND    = 0x0111
	WM_SETFONT    = 0x0030
	WM_CTLCOLORSTATIC = 0x0138
	WM_CTLCOLOREDIT   = 0x0133

	// Theme colors (matching Android mobile layout)
	CLR_BG       = 0x190F0B // #0B0F19
	CLR_CARD     = 0x241913 // #131924
	CLR_BORDER   = 0x36251C // #1C2536
	CLR_INPUT_BG = 0x1F150F // #0F151F
	CLR_BTN      = 0xEB6325 // #2563EB (Brand blue)
	CLR_TXT_MAIN = 0xFFFFFF
	CLR_TXT_MUTED= 0x94A3B8
	CLR_SUCCESS  = 0xA0D606 // #06D6A0 (Neon emerald)
	CLR_DANGER   = 0x5053EF // #EF5350 (Coral red)

	PANEL_BASE   = "http://67.215.255.156:13688"
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

func setSystemProxy(enable bool, proxyAddr string) {
	k, err := registry.OpenKey(registry.CURRENT_USER, `Software\Microsoft\Windows\CurrentVersion\Internet Settings`, registry.SET_VALUE)
	if err != nil {
		return
	}
	defer k.Close()

	if enable {
		k.SetDWordValue("ProxyEnable", 1)
		k.SetStringValue("ProxyServer", proxyAddr)
		k.SetStringValue("ProxyOverride", "localhost;127.*;10.*;192.168.*;<local>")
	} else {
		k.SetDWordValue("ProxyEnable", 0)
	}
	procInternetSet.Call(0, 39, 0, 0)
	procInternetSet.Call(0, 37, 0, 0)
}

func wndProc(hwnd windows.HWND, msg uint32, wParam, lParam uintptr) uintptr {
	switch msg {
	case WM_PAINT:
		var ps PAINTSTRUCT
		hdc, _, _ := procBeginPaint.Call(uintptr(hwnd), uintptr(unsafe.Pointer(&ps)))

		// Draw card backgrounds (Input card & Stats card)
		procSelectObject.Call(hdc, uintptr(hPenCard))
		procSelectObject.Call(hdc, uintptr(hBrushCard))

		// 1. Input card
		procRoundRect.Call(hdc, 24, 75, 416, 205, 12, 12)
		// 2. Stats card
		procRoundRect.Call(hdc, 24, 290, 416, 420, 12, 12)

		procEndPaint.Call(uintptr(hwnd), uintptr(unsafe.Pointer(&ps)))
		return 0

	case WM_COMMAND:
		if lParam == uintptr(hwndBtn) {
			go toggleConnect()
		}
		return 0

	case WM_CTLCOLORSTATIC:
		hdc := wParam
		procSetBkMode.Call(hdc, 1) // TRANSPARENT
		target := windows.HWND(lParam)
		if target == hwndStatus {
			if connected {
				procSetTextColor.Call(hdc, CLR_SUCCESS)
			} else {
				procSetTextColor.Call(hdc, CLR_DANGER)
			}
		} else if target == hwndTraffic {
			procSetTextColor.Call(hdc, CLR_TXT_MAIN)
		} else if target == hwndDetail {
			procSetTextColor.Call(hdc, CLR_TXT_MUTED)
		} else {
			procSetTextColor.Call(hdc, CLR_TXT_MAIN)
		}
		return uintptr(hBrushBg)

	case WM_CTLCOLOREDIT:
		hdc := wParam
		procSetBkMode.Call(hdc, 2)
		procSetBkColor.Call(hdc, CLR_INPUT_BG)
		procSetTextColor.Call(hdc, CLR_TXT_MAIN)
		return uintptr(hBrushInput)

	case WM_DESTROY:
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

func parseVless(raw string) (uuid, address, port, sni, pbk, sid, flow string, err error) {
	raw = strings.TrimSpace(raw)
	if !strings.HasPrefix(raw, "vless://") {
		return "", "", "", "", "", "", "", fmt.Errorf("not vless")
	}
	u, err := url.Parse(raw)
	if err != nil {
		return "", "", "", "", "", "", "", err
	}
	return u.User.Username(), u.Hostname(), u.Port(), u.Query().Get("sni"), u.Query().Get("pbk"), u.Query().Get("sid"), u.Query().Get("flow"), nil
}

func buildXrayConfig(uuid, address, port, sni, pbk, sid, flow string) string {
	return fmt.Sprintf(`{
  "log": { "loglevel": "warning" },
  "inbounds": [
    { "port": 10808, "listen": "127.0.0.1", "protocol": "socks", "settings": { "auth": "noauth", "udp": true } },
    { "port": 10809, "listen": "127.0.0.1", "protocol": "http", "settings": {} }
  ],
  "outbounds": [
    {
      "protocol": "vless",
      "settings": {
        "vnext": [{ "address": "%s", "port": %s, "users": [{ "id": "%s", "encryption": "none", "flow": "%s" }] }]
      },
      "streamSettings": {
        "network": "tcp",
        "security": "reality",
        "realitySettings": { "show": false, "fingerprint": "chrome", "serverName": "%s", "publicKey": "%s", "shortId": "%s", "spiderX": "" }
      }
    }
  ]
}`, address, port, uuid, flow, sni, pbk, sid)
}

func setText(hwnd windows.HWND, text string) {
	ptr, _ := windows.UTF16PtrFromString(text)
	procSetWindowText.Call(uintptr(hwnd), uintptr(unsafe.Pointer(ptr)))
	procInvalidate.Call(uintptr(hwnd), 0, 1)
}

func getText(hwnd windows.HWND) string {
	buf := make([]uint16, 2048)
	procGetWindowText.Call(uintptr(hwnd), uintptr(unsafe.Pointer(&buf[0])), 2048)
	return windows.UTF16ToString(buf)
}

func toggleConnect() {
	if connected {
		stopProxy()
	} else {
		startProxy()
	}
}

func startProxy() {
	raw := getText(hwndEdit)
	uuid, address, port, sni, pbk, sid, flow, err := parseVless(raw)
	if err != nil {
		setText(hwndStatus, "● 链接格式错误 (需 vless://)")
		return
	}
	serverUUID = uuid

	if k, _, err := registry.CreateKey(registry.CURRENT_USER, `Software\XPanel`, registry.ALL_ACCESS); err == nil {
		k.SetStringValue("link", raw)
		k.Close()
	}

	setText(hwndStatus, "● 正在连接...")
	confJSON := buildXrayConfig(uuid, address, port, sni, pbk, sid, flow)

	conf, err := core.LoadConfig("json", bytes.NewReader([]byte(confJSON)))
	if err != nil {
		setText(hwndStatus, "● 配置错误")
		return
	}
	server, err := core.New(conf)
	if err != nil {
		setText(hwndStatus, "● 内核启动失败")
		return
	}
	if err := server.Start(); err != nil {
		setText(hwndStatus, "● 端口被占用")
		return
	}
	xrayServer = server
	setSystemProxy(true, "127.0.0.1:10809")
	connected = true

	setText(hwndBtn, "断开连接")
	setText(hwndStatus, "● 已安全连接 (系统代理: 127.0.0.1:10809)")
}

func stopProxy() {
	if xrayServer != nil {
		xrayServer.Close()
		xrayServer = nil
	}
	setSystemProxy(false, "")
	connected = false
	setText(hwndBtn, "一键连接")
	setText(hwndStatus, "○ 未连接")
}

func pollTraffic() {
	for {
		time.Sleep(2 * time.Second)
		if serverUUID == "" {
			continue
		}
		resp, err := http.Get(PANEL_BASE + "/panel/pub/traffic/" + serverUUID)
		if err != nil {
			continue
		}
		body, _ := io.ReadAll(resp.Body)
		resp.Body.Close()

		var res struct {
			Success bool `json:"success"`
			Obj     struct {
				Up   int64 `json:"up"`
				Down int64 `json:"down"`
			} `json:"obj"`
		}
		if json.Unmarshal(body, &res) == nil && res.Success {
			used := res.Obj.Up + res.Obj.Down
			setText(hwndTraffic, fmt.Sprintf("%.2f MB", float64(used)/1048576.0))
			setText(hwndDetail, fmt.Sprintf("↑ 上行: %.2f MB      ↓ 下行: %.2f MB", float64(res.Obj.Up)/1048576.0, float64(res.Obj.Down)/1048576.0))
		}
	}
}

func createFont(size int, bold bool) uintptr {
	w := 400
	if bold {
		w = 700
	}
	h, _, _ := procCreateFont.Call(uintptr(size), 0, 0, 0, uintptr(w), 0, 0, 0, 1, 0, 0, 0, 0, uintptr(unsafe.Pointer(windows.StringToUTF16Ptr("Segoe UI"))))
	return h
}

func main() {
	runtime.LockOSThread()

	instRaw, _, _ := procGetModule.Call(0)
	inst := windows.Handle(instRaw)
	className, _ := windows.UTF16PtrFromString("XPanelWinV3")
	wndTitle, _ := windows.UTF16PtrFromString("X-Panel")

	hIconRaw, _, _ := procLoadIcon.Call(uintptr(inst), 100)
	hIcon := windows.Handle(hIconRaw)

	hBgRaw, _, _ := procCreateSolid.Call(CLR_BG)
	hBrushBg = windows.Handle(hBgRaw)
	hCardRaw, _, _ := procCreateSolid.Call(CLR_CARD)
	hBrushCard = windows.Handle(hCardRaw)
	hInRaw, _, _ := procCreateSolid.Call(CLR_INPUT_BG)
	hBrushInput = windows.Handle(hInRaw)
	hBtnRaw, _, _ := procCreateSolid.Call(CLR_BTN)
	hBrushBtn = windows.Handle(hBtnRaw)

	hPenCardRaw, _, _ := procCreatePen.Call(0, 1, CLR_BORDER)
	hPenCard = windows.Handle(hPenCardRaw)

	hFontTitle = createFont(22, true)
	hFontSub   = createFont(13, false)
	hFontEdit  = createFont(13, false)
	hFontBtn   = createFont(16, true)
	hFontStat  = createFont(14, true)
	hFontVal   = createFont(24, true)

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

	hwndRaw, _, _ := procCreateWindow.Call(
		0, uintptr(unsafe.Pointer(className)), uintptr(unsafe.Pointer(wndTitle)),
		WS_OVERLAPPED|WS_CAPTION|WS_SYSMENU|WS_MINIMIZEBOX|WS_VISIBLE,
		250, 180, 456, 490, 0, 0, uintptr(inst), 0,
	)
	hwndMain = windows.HWND(hwndRaw)

	staticClass, _ := windows.UTF16PtrFromString("STATIC")
	editClass, _   := windows.UTF16PtrFromString("EDIT")
	btnClass, _    := windows.UTF16PtrFromString("BUTTON")

	// 1. Header: X-Panel title + Subtitle
	t1, _ := windows.UTF16PtrFromString("X-Panel")
	hT1, _, _ := procCreateWindow.Call(0, uintptr(unsafe.Pointer(staticClass)), uintptr(unsafe.Pointer(t1)), WS_CHILD|WS_VISIBLE, 24, 18, 300, 26, uintptr(hwndMain), 0, uintptr(inst), 0)
	procSendMessage.Call(hT1, WM_SETFONT, hFontTitle, 1)

	t2, _ := windows.UTF16PtrFromString("极简安全代理 · 实时流量监测")
	hT2, _, _ := procCreateWindow.Call(0, uintptr(unsafe.Pointer(staticClass)), uintptr(unsafe.Pointer(t2)), WS_CHILD|WS_VISIBLE, 24, 46, 300, 18, uintptr(hwndMain), 0, uintptr(inst), 0)
	procSendMessage.Call(hT2, WM_SETFONT, hFontSub, 1)

	// 2. Input card: Edit field
	hEdit, _, _ := procCreateWindow.Call(0, uintptr(unsafe.Pointer(editClass)), 0, WS_CHILD|WS_VISIBLE|ES_MULTILINE|ES_AUTOVSCROLL, 40, 90, 360, 100, uintptr(hwndMain), 0, uintptr(inst), 0)
	hwndEdit = windows.HWND(hEdit)
	procSendMessage.Call(uintptr(hwndEdit), WM_SETFONT, hFontEdit, 1)

	if k, err := registry.OpenKey(registry.CURRENT_USER, `Software\XPanel`, registry.QUERY_VALUE); err == nil {
		if s, _, err := k.GetStringValue("link"); err == nil && s != "" {
			setText(hwndEdit, s)
		}
		k.Close()
	}

	// 3. Connect Button
	btnText, _ := windows.UTF16PtrFromString("一键连接")
	hBtn, _, _ := procCreateWindow.Call(0, uintptr(unsafe.Pointer(btnClass)), uintptr(unsafe.Pointer(btnText)), WS_CHILD|WS_VISIBLE, 24, 222, 392, 46, uintptr(hwndMain), 0, uintptr(inst), 0)
	hwndBtn = windows.HWND(hBtn)
	procSendMessage.Call(uintptr(hwndBtn), WM_SETFONT, hFontBtn, 1)

	// 4. Stats card content
	stText, _ := windows.UTF16PtrFromString("○ 未连接")
	hStatus, _, _ := procCreateWindow.Call(0, uintptr(unsafe.Pointer(staticClass)), uintptr(unsafe.Pointer(stText)), WS_CHILD|WS_VISIBLE, 40, 304, 360, 22, uintptr(hwndMain), 0, uintptr(inst), 0)
	hwndStatus = windows.HWND(hStatus)
	procSendMessage.Call(uintptr(hwndStatus), WM_SETFONT, hFontStat, 1)

	trText, _ := windows.UTF16PtrFromString("0.00 MB")
	hTraffic, _, _ := procCreateWindow.Call(0, uintptr(unsafe.Pointer(staticClass)), uintptr(unsafe.Pointer(trText)), WS_CHILD|WS_VISIBLE, 40, 332, 360, 34, uintptr(hwndMain), 0, uintptr(inst), 0)
	hwndTraffic = windows.HWND(hTraffic)
	procSendMessage.Call(uintptr(hwndTraffic), WM_SETFONT, hFontVal, 1)

	dtText, _ := windows.UTF16PtrFromString("↑ 上行: 0.00 MB      ↓ 下行: 0.00 MB")
	hDetail, _, _ := procCreateWindow.Call(0, uintptr(unsafe.Pointer(staticClass)), uintptr(unsafe.Pointer(dtText)), WS_CHILD|WS_VISIBLE, 40, 376, 360, 20, uintptr(hwndMain), 0, uintptr(inst), 0)
	hwndDetail = windows.HWND(hDetail)
	procSendMessage.Call(uintptr(hwndDetail), WM_SETFONT, hFontSub, 1)

	go pollTraffic()

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
