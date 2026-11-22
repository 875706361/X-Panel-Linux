package controller

import (
	"net/http"
	"os"

	"github.com/gin-gonic/gin"
)

// windowsClientPaths 面板可能放置 Windows 客户端的路径，按优先级排列。
//
// 镜像目录必须排第一：它由 scripts/sync_panel_mirror.sh 每 6 小时从 Release 同步，是唯一
// 跟得上客户端发版的副本。安装脚本放在 /root/win-client-csharp/XPanel.exe 的那份只写一次，
// 之后永远不变——排在前面时，面板「下载 Windows 客户端」按钮会一直发旧包，用户拿到的版本
// 与 client.json 相同（甚至更低），自动更新按版本号护栏不会动手，表现为「下载了但升不上去」。
var windowsClientPaths = []string{
	"/root/xpanel-mirror/XPanel.exe",
	"/root/win-client-csharp/XPanel.exe",
	"/root/客户端.exe",
	"/root/XPanel.exe",
	"web/assets/xpanel-windows.exe",
	"/usr/local/x-ui/web/assets/xpanel-windows.exe",
	"/root/win-client-csharp/客户端.exe",
}

// serveWindowsClient 输出 Windows 客户端；公开路由与登录后的下载按钮共用。
func serveWindowsClient(c *gin.Context) {
	c.Header("Cache-Control", "no-cache, no-store, must-revalidate")
	c.Header("Pragma", "no-cache")
	c.Header("Expires", "0")
	for _, p := range windowsClientPaths {
		if st, err := os.Stat(p); err == nil && !st.IsDir() {
			c.FileAttachment(p, "XPanel.exe")
			return
		}
	}
	c.String(http.StatusNotFound, "Windows client not found")
}
