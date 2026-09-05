package main

import (
	"bytes"
	"encoding/json"
	"fmt"
	"io"
	"net/http"
	"net/url"
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

	gdi32          = windows.NewLazySystemDLL("gdi32.dll")
	procCreateFont = gdi32.NewProc("CreateFontW")

	winInet         = windows.NewLazySystemDLL("wininet.dll")
	procInternetSet = winInet.NewProc("InternetSetOptionW")

	hwndMain, hwndEdit, hwndBtn, hwndStatus, hwndTraffic windows.HWND
	hFont uintptr

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
			toggleConnect()
		}
		return 0
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
		setText(hwndStatus, "状态: 链接格式错误 (需 vless://)")
		return
	}
	serverUUID = uuid
	confJSON := buildXrayConfig(uuid, address, port, sni, pbk, sid, flow)

	conf, err := core.LoadConfig("json", bytes.NewReader([]byte(confJSON)))
	if err != nil {
		setText(hwndStatus, "状态: 配置解析失败")
		return
	}
	server, err := core.New(conf)
	if err != nil {
		setText(hwndStatus, "状态: Xray 初始化失败: "+err.Error())
		return
	}
	if err := server.Start(); err != nil {
		setText(hwndStatus, "状态: 启动失败")
		return
	}
	xrayServer = server
	setSystemProxy(true, "127.0.0.1:10809")
	connected = true
	setText(hwndBtn, "断开连接")
	setText(hwndStatus, "状态: ● 已连接 (系统代理: 127.0.0.1:10809)")
}

func stopProxy() {
	if xrayServer != nil {
		xrayServer.Close()
		xrayServer = nil
	}
	setSystemProxy(false, "")
	connected = false
	setText(hwndBtn, "连接加速")
	setText(hwndStatus, "状态: ○ 未连接")
}

func pollTraffic() {
	for {
		time.Sleep(3 * time.Second)
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
			txt := fmt.Sprintf("已用: %.2f MB  |  上行: %.2f MB  |  下行: %.2f MB",
				float64(used)/1048576.0, float64(res.Obj.Up)/1048576.0, float64(res.Obj.Down)/1048576.0)
			setText(hwndTraffic, txt)
		}
	}
}

func main() {
	instRaw, _, _ := procGetModule.Call(0)
	inst := windows.Handle(instRaw)
	className, _ := windows.UTF16PtrFromString("XPanelWin")
	wndTitle, _ := windows.UTF16PtrFromString("X-Panel 客户端")

	hFontRaw, _, _ := procCreateFont.Call(16, 0, 0, 0, 400, 0, 0, 0, 1, 0, 0, 0, 0, uintptr(unsafe.Pointer(windows.StringToUTF16Ptr("Microsoft YaHei"))))
	hFont = hFontRaw

	wc := WNDCLASSEX{
		CbSize:        uint32(unsafe.Sizeof(WNDCLASSEX{})),
		LpfnWndProc:   windows.NewCallback(wndProc),
		HInstance:     inst,
		HbrBackground: windows.Handle(6),
		LpszClassName: className,
	}
	procRegisterClass.Call(uintptr(unsafe.Pointer(&wc)))

	hwndRaw, _, _ := procCreateWindow.Call(
		0, uintptr(unsafe.Pointer(className)), uintptr(unsafe.Pointer(wndTitle)),
		WS_OVERLAPPEDWINDOW|WS_VISIBLE,
		100, 100, 440, 260, 0, 0, uintptr(inst), 0,
	)
	hwndMain = windows.HWND(hwndRaw)

	editClass, _ := windows.UTF16PtrFromString("EDIT")
	hEdit, _, _ := procCreateWindow.Call(
		0, uintptr(unsafe.Pointer(editClass)), 0,
		WS_CHILD|WS_VISIBLE|WS_BORDER|ES_MULTILINE|ES_AUTOVSCROLL,
		20, 20, 385, 75, uintptr(hwndMain), 0, uintptr(inst), 0,
	)
	hwndEdit = windows.HWND(hEdit)
	procSendMessage.Call(uintptr(hwndEdit), WM_SETFONT, hFont, 1)

	btnClass, _ := windows.UTF16PtrFromString("BUTTON")
	btnTitle, _ := windows.UTF16PtrFromString("连接加速")
	hBtn, _, _ := procCreateWindow.Call(
		0, uintptr(unsafe.Pointer(btnClass)), uintptr(unsafe.Pointer(btnTitle)),
		WS_CHILD|WS_VISIBLE,
		20, 105, 385, 36, uintptr(hwndMain), 0, uintptr(inst), 0,
	)
	hwndBtn = windows.HWND(hBtn)
	procSendMessage.Call(uintptr(hwndBtn), WM_SETFONT, hFont, 1)

	staticClass, _ := windows.UTF16PtrFromString("STATIC")
	stTitle, _ := windows.UTF16PtrFromString("状态: ○ 未连接 (请粘贴 vless:// 链接)")
	hStatus, _, _ := procCreateWindow.Call(
		0, uintptr(unsafe.Pointer(staticClass)), uintptr(unsafe.Pointer(stTitle)),
		WS_CHILD|WS_VISIBLE,
		20, 150, 385, 22, uintptr(hwndMain), 0, uintptr(inst), 0,
	)
	hwndStatus = windows.HWND(hStatus)
	procSendMessage.Call(uintptr(hwndStatus), WM_SETFONT, hFont, 1)

	trTitle, _ := windows.UTF16PtrFromString("已用: 0.00 MB  |  上行: 0.00 MB  |  下行: 0.00 MB")
	hTraffic, _, _ := procCreateWindow.Call(
		0, uintptr(unsafe.Pointer(staticClass)), uintptr(unsafe.Pointer(trTitle)),
		WS_CHILD|WS_VISIBLE,
		20, 175, 385, 22, uintptr(hwndMain), 0, uintptr(inst), 0,
	)
	hwndTraffic = windows.HWND(hTraffic)
	procSendMessage.Call(uintptr(hwndTraffic), WM_SETFONT, hFont, 1)

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
