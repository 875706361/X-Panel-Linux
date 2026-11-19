package controller

import (
	"net/http"
	"os"
	"path/filepath"
	"regexp"
	"strings"

	"github.com/gin-gonic/gin"
)

// 本机镜像目录：由 scripts/sync_panel_mirror.sh 从本仓库 Release 同步到这里，
// 面板公开提供下载，国内用户不必直连 GitHub。
const defaultPkgMirrorDir = "/root/xpanel-mirror"

var pkgMirrorAllow = regexp.MustCompile(`^(x-ui-linux-(amd64|arm64|armv7|armv6|armv5|386|s390x)\.tar\.gz|XPanel\.exe|xpanel-client\.apk|latest\.json|client\.json|install\.sh|x-ui\.sh)$`)

func pkgMirrorDir() string {
	if v := strings.TrimSpace(os.Getenv("XUI_PKG_MIRROR_DIR")); v != "" {
		return v
	}
	return defaultPkgMirrorDir
}

// servePkgMirror 提供安装包/客户端的公开下载：/<basePath>/download/pkg/<file>
// 只允许固定文件名，避免路径穿越；目录里没有时回退到 GitHub Release。
func servePkgMirror(c *gin.Context) {
	name := filepath.Base(c.Param("name"))
	if !pkgMirrorAllow.MatchString(name) {
		c.String(http.StatusNotFound, "not found")
		return
	}
	p := filepath.Join(pkgMirrorDir(), name)
	if st, err := os.Stat(p); err == nil && !st.IsDir() {
		c.Header("Cache-Control", "public, max-age=300")
		c.File(p)
		return
	}
	c.String(http.StatusNotFound, "mirror file not found")
}
