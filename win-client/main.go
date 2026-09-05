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

	gdi32             = windows.NewLazySystemDLL("gdi32.dll")
	procCreateFont    = gdi32.NewProc("CreateFontW")
	procCreateSolid   = gdi32.NewProc("CreateSolidBrush")
	procSetTextColor  = gdi32.NewProc("SetTextColor")
	procSetBkMode     = gdi32.NewProc("SetBkMode")

	winInet         = windows.NewLazySystemDLL("wininet.dll")
	procInternetSet = winInet.NewProc("InternetSetOptionW")

	hwndMain, hwndEdit, hwndBtn, hwndTitle, hwndStatus, hwndTraffic, hwndDetail windows.HWND
	hFontBold, hFontRegular, hFontLarge uintptr
	hBrushBg, hBrushCard, hBrushInput windows.Handle

	xrayServer *core.Instance
	connected  bool
	serverUUID string
)

const (
	WS_OVERLAPPEDWINDOW = 0x00CF0000
	WS_VISIBLE          = 0x10000000
	WS_CHILD            = 0x40000000
	WS_BORDER           = 0x00800000
	ES_MULTILINE        = 0x0004
	ES_AUTOVSCROLL      = 0x0040
	WM_DESTROY          = 0x0002
	WM_COMMAND          = 0x0111
	WM_SETFONT          = 0x0030
	WM_CTLCOLORSTATIC   = 0x0138
	WM_CTLCOLOREDIT     = 0x0133
	WM_CTLCOLORBTN      = 0x0135
	COLOR_BG            = 0x190F0B // 0x0B0F19 in BGR
	COLOR_CARD          = 0x261B16 // 0x161B26 in BGR
	COLOR_INPUT         = 0x2E241E // 0x1E242E in BGR
	PANEL_BASE          = "http://67.215.255.156:13688"
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
	case WM_COMMAND:
		if lParam == uintptr(hwndBtn) {
			go toggleConnect()
		}
		return 0

	case WM_CTLCOLORSTATIC:
		hdc := wParam
		procSetBkMode.Call(hdc, 1) // TRANSPARENT
		if windows.HWND(lParam) == hwndStatus {
			if connected {
				procSetTextColor.Call(hdc, 0x00E67E22) // Green/Cyan BGR
			} else {
				procSetTextColor.Call(hdc, 0x005555EF) // Red BGR
			}
		} else if windows.HWND(lParam) == hwndTraffic || windows.HWND(lParam) == hwndDetail {
			procSetTextColor.Call(hdc, 0x00E0E0E0)
		} else {
			procSetTextColor.Call(hdc, 0x00FFFFFF)
		}
		return uintptr(hBrushBg)

	case WM_CTLCOLOREDIT:
		hdc := wParam
		procSetBkMode.Call(hdc, 2)
		procSetTextColor.Call(hdc, 0x00FFFFFF)
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
	uuid = u.User.Username()
	address = u.Hostname()
	port = u.Port()
	q := u.Query()
	sni = q.Get("sni")
	pbk = q.Get("pbk")
	sid = q.Get("sid")
	flow = q.Get("flow")
	return
}

func buildXrayConfig(uuid, address, port, sni, pbk, sid, flow string) string {
	return fmt.Sprintf(`{
  "log": { "loglevel": "warning" },
  "inbounds": [
    {
      "port": 10808,
      "listen": "127.0.0.1",
      "protocol": "socks",
      "settings": { "auth": "noauth", "udp": true }
    },
    {
      "port": 10809,
      "listen": "127.0.0.1",
      "protocol": "http",
      "settings": {}
    }
  ],
  "outbounds": [
    {
      "protocol": "vless",
      "settings": {
        "vnext": [
          {
            "address": "%s",
            "port": %s,
            "users": [
              {
                "id": "%s",
                "encryption": "none",
                "flow": "%s"
              }
            ]
          }
        ]
      },
      "streamSettings": {
        "network": "tcp",
        "security": "reality",
        "realitySettings": {
          "show": false,
          "fingerprint": "chrome",
          "serverName": "%s",
          "publicKey": "%s",
          "shortId": "%s",
          "spiderX": ""
        }
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

	// 持久化链接到注册表，下次打开免输
	if k, err := registry.OpenKey(registry.CURRENT_USER, `Software\XPanel`, registry.ALL_ACCESS); err == nil {
		k.SetStringValue("link", raw)
		k.Close()
	} else if k, _, err := registry.CreateKey(registry.CURRENT_USER, `Software\XPanel`, registry.ALL_ACCESS); err == nil {
		k.SetStringValue("link", raw)
		k.Close()
	}

	setText(hwndStatus, "● 正在连接服务器...")
	confJSON := buildXrayConfig(uuid, address, port, sni, pbk, sid, flow)

	conf, err := core.LoadConfig("json", bytes.NewReader([]byte(confJSON)))
	if err != nil {
		setText(hwndStatus, "● 配置构建异常")
		return
	}
	server, err := core.New(conf)
	if err != nil {
		setText(hwndStatus, "● 内核启动失败")
		return
	}
	if err := server.Start(); err != nil {
		setText(hwndStatus, "● 端口被占用或连接失败")
		return
	}
	xrayServer = server
	setSystemProxy(true, "127.0.0.1:10809")
	connected = true

	setText(hwndBtn, "断开连接")
	setText(hwndStatus, "● 已连接加速 (系统代理: 127.0.0.1:10809)")
}

func stopProxy() {
	if xrayServer != nil {
		xrayServer.Close()
		xrayServer = nil
	}
	setSystemProxy(false, "")
	connected = false
	setText(hwndBtn, "一键连接加速")
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
				Up    int64 `json:"up"`
				Down  int64 `json:"down"`
				Total int64 `json:"total"`
			} `json:"obj"`
		}
		if json.Unmarshal(body, &res) == nil && res.Success {
			used := res.Obj.Up + res.Obj.Down
			txt := fmt.Sprintf("已用流量: %.2f MB", float64(used)/1048576.0)
			setText(hwndTraffic, txt)
			txt2 := fmt.Sprintf("↑ 上行: %.2f MB    ↓ 下行: %.2f MB", float64(res.Obj.Up)/1048576.0, float64(res.Obj.Down)/1048576.0)
			setText(hwndDetail, txt2)
		}
	}
}

func main() {
	// 锁死 OS 线程，杜绝 Windows GUI 消息循环死锁卡死
	runtime.LockOSThread()

	instRaw, _, _ := procGetModule.Call(0)
	inst := windows.Handle(instRaw)
	className, _ := windows.UTF16PtrFromString("XPanelWinV2")
	wndTitle, _ := windows.UTF16PtrFromString("X-Panel")

	hIconRaw, _, _ := procLoadIcon.Call(uintptr(inst), 100)
	hIcon := windows.Handle(hIconRaw)

	hBgRaw, _, _ := procCreateSolid.Call(COLOR_BG)
	hBrushBg = windows.Handle(hBgRaw)
	hCardRaw, _, _ := procCreateSolid.Call(COLOR_CARD)
	hBrushCard = windows.Handle(hCardRaw)
	hInRaw, _, _ := procCreateSolid.Call(COLOR_INPUT)
	hBrushInput = windows.Handle(hInRaw)

	fLg, _, _ := procCreateFont.Call(22, 0, 0, 0, 700, 0, 0, 0, 1, 0, 0, 0, 0, uintptr(unsafe.Pointer(windows.StringToUTF16Ptr("Microsoft YaHei"))))
	hFontLarge = fLg
	fBd, _, _ := procCreateFont.Call(16, 0, 0, 0, 700, 0, 0, 0, 1, 0, 0, 0, 0, uintptr(unsafe.Pointer(windows.StringToUTF16Ptr("Microsoft YaHei"))))
	hFontBold = fBd
	fRg, _, _ := procCreateFont.Call(14, 0, 0, 0, 400, 0, 0, 0, 1, 0, 0, 0, 0, uintptr(unsafe.Pointer(windows.StringToUTF16Ptr("Microsoft YaHei"))))
	hFontRegular = fRg

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
		WS_OVERLAPPEDWINDOW|WS_VISIBLE,
		200, 150, 480, 520, 0, 0, uintptr(inst), 0,
	)
	hwndMain = windows.HWND(hwndRaw)

	// 顶部标题
	staticClass, _ := windows.UTF16PtrFromString("STATIC")
	titleStr, _ := windows.UTF16PtrFromString("X-Panel 客户端")
	hTitle, _, _ := procCreateWindow.Call(
		0, uintptr(unsafe.Pointer(staticClass)), uintptr(unsafe.Pointer(titleStr)),
		WS_CHILD|WS_VISIBLE,
		30, 20, 400, 30, uintptr(hwndMain), 0, uintptr(inst), 0,
	)
	hwndTitle = windows.HWND(hTitle)
	procSendMessage.Call(uintptr(hwndTitle), WM_SETFONT, hFontLarge, 1)

	// 协议链接输入框
	editClass, _ := windows.UTF16PtrFromString("EDIT")
	hEdit, _, _ := procCreateWindow.Call(
		0, uintptr(unsafe.Pointer(editClass)), 0,
		WS_CHILD|WS_VISIBLE|WS_BORDER|ES_MULTILINE|ES_AUTOVSCROLL,
		30, 65, 405, 90, uintptr(hwndMain), 0, uintptr(inst), 0,
	)
	hwndEdit = windows.HWND(hEdit)
	procSendMessage.Call(uintptr(hwndEdit), WM_SETFONT, hFontRegular, 1)

	// 读取已保存的链接
	if k, err := registry.OpenKey(registry.CURRENT_USER, `Software\XPanel`, registry.QUERY_VALUE); err == nil {
		if s, _, err := k.GetStringValue("link"); err == nil && s != "" {
			setText(hwndEdit, s)
		}
		k.Close()
	}

	// 一键连接大按钮
	btnClass, _ := windows.UTF16PtrFromString("BUTTON")
	btnTitle, _ := windows.UTF16PtrFromString("一键连接加速")
	hBtn, _, _ := procCreateWindow.Call(
		0, uintptr(unsafe.Pointer(btnClass)), uintptr(unsafe.Pointer(btnTitle)),
		WS_CHILD|WS_VISIBLE,
		30, 175, 405, 48, uintptr(hwndMain), 0, uintptr(inst), 0,
	)
	hwndBtn = windows.HWND(hBtn)
	procSendMessage.Call(uintptr(hwndBtn), WM_SETFONT, hFontBold, 1)

	// 状态栏
	stTitle, _ := windows.UTF16PtrFromString("○ 未连接")
	hStatus, _, _ := procCreateWindow.Call(
		0, uintptr(unsafe.Pointer(staticClass)), uintptr(unsafe.Pointer(stTitle)),
		WS_CHILD|WS_VISIBLE,
		30, 240, 405, 26, uintptr(hwndMain), 0, uintptr(inst), 0,
	)
	hwndStatus = windows.HWND(hStatus)
	procSendMessage.Call(uintptr(hwndStatus), WM_SETFONT, hFontBold, 1)

	// 流量统计卡片：已用流量大字
	trTitle, _ := windows.UTF16PtrFromString("已用流量: --")
	hTraffic, _, _ := procCreateWindow.Call(
		0, uintptr(unsafe.Pointer(staticClass)), uintptr(unsafe.Pointer(trTitle)),
		WS_CHILD|WS_VISIBLE,
		30, 280, 405, 28, uintptr(hwndMain), 0, uintptr(inst), 0,
	)
	hwndTraffic = windows.HWND(hTraffic)
	procSendMessage.Call(uintptr(hwndTraffic), WM_SETFONT, hFontLarge, 1)

	// 上下行明细
	dtTitle, _ := windows.UTF16PtrFromString("↑ 上行: 0.00 MB    ↓ 下行: 0.00 MB")
	hDetail, _, _ := procCreateWindow.Call(
		0, uintptr(unsafe.Pointer(staticClass)), uintptr(unsafe.Pointer(dtTitle)),
		WS_CHILD|WS_VISIBLE,
		30, 315, 405, 24, uintptr(hwndMain), 0, uintptr(inst), 0,
	)
	hwndDetail = windows.HWND(hDetail)
	procSendMessage.Call(uintptr(hwndDetail), WM_SETFONT, hFontRegular, 1)

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
