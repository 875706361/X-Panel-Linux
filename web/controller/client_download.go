package controller

import (
	"net/http"
	"os"

	"github.com/gin-gonic/gin"
)

// windowsClientPaths 面板可能放置 Windows 客户端的路径，按优先级排列。
// 安装脚本会把 Release 里的 XPanel.exe 放到前两个位置。
var windowsClientPaths = []string{
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
