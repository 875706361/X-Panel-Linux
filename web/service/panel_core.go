package service

import (
	"archive/tar"
	"compress/gzip"
	"fmt"
	"io"
	"net/http"
	"os"
	"os/exec"
	"path/filepath"
	"runtime"
	"runtime/debug"
	"strings"
	"time"

	"x-ui/logger"
	"x-ui/xray"
)

// PanelRepo 是所有安装/更新下载的唯一来源。
// 需求：一键安装、面板内更新内核、Docker 构建都不允许再走官方仓库，
// 一律从本仓库的 Release 资产取（其中打包的是带 REALITY 兼容补丁的 Xray）。
const defaultPanelRepo = "875706361/X-Panel-Linux"

func PanelRepo() string {
	if v := strings.TrimSpace(os.Getenv("XUI_PANEL_REPO")); v != "" {
		return strings.TrimSuffix(strings.TrimSuffix(v, ".git"), "/")
	}
	return defaultPanelRepo
}

// PanelCoreAssetArch 把运行时架构映射成 Release 里的资产后缀（x-ui-linux-<arch>.tar.gz）。
// ARM 需要 GOARM 才能区分 v5/v6/v7。
func PanelCoreAssetArch() string {
	switch runtime.GOARCH {
	case "amd64", "arm64", "386", "s390x":
		return runtime.GOARCH
	case "arm":
		goarm := ""
		if bi, ok := debug.ReadBuildInfo(); ok {
			for _, s := range bi.Settings {
				if s.Key == "GOARM" {
					goarm = s.Value
				}
			}
		}
		switch goarm {
		case "5":
			return "armv5"
		case "6":
			return "armv6"
		default:
			return "armv7"
		}
	}
	return runtime.GOARCH
}

func PanelCoreAssetURL(version string) string {
	tag := strings.TrimSpace(version)
	if tag == "" {
		tag = "latest"
	} else if !strings.HasPrefix(tag, "v") {
		tag = "v" + tag
	}
	return fmt.Sprintf("https://github.com/%s/releases/download/%s/x-ui-linux-%s.tar.gz",
		PanelRepo(), tag, PanelCoreAssetArch())
}

// PanelReleasesURL 面板内"内核版本"列表的来源，同样只查本仓库。
func PanelReleasesURL() string {
	return fmt.Sprintf("https://api.github.com/repos/%s/releases?per_page=30", PanelRepo())
}

// downloadPanelPackage 下载本仓库的 Release 包到指定文件。
func downloadPanelPackage(version, dst string) error {
	url := PanelCoreAssetURL(version)
	client := &http.Client{Timeout: 10 * time.Minute}
	resp, err := client.Get(url)
	if err != nil {
		return fmt.Errorf("下载 %s 失败: %w", url, err)
	}
	defer resp.Body.Close()
	if resp.StatusCode != http.StatusOK {
		return fmt.Errorf("下载 %s 失败: HTTP %d", url, resp.StatusCode)
	}
	f, err := os.Create(dst)
	if err != nil {
		return err
	}
	defer f.Close()
	if _, err := io.Copy(f, resp.Body); err != nil {
		return err
	}
	return nil
}

// extractCoreFromPanelPackage 从 x-ui-linux-<arch>.tar.gz 里取出 bin/xray-linux-*。
func extractCoreFromPanelPackage(pkg, dst string) error {
	f, err := os.Open(pkg)
	if err != nil {
		return err
	}
	defer f.Close()
	gz, err := gzip.NewReader(f)
	if err != nil {
		return fmt.Errorf("安装包不是有效的 tar.gz: %w", err)
	}
	defer gz.Close()

	tr := tar.NewReader(gz)
	for {
		h, err := tr.Next()
		if err == io.EOF {
			break
		}
		if err != nil {
			return err
		}
		if h.Typeflag != tar.TypeReg {
			continue
		}
		name := filepath.ToSlash(h.Name)
		base := filepath.Base(name)
		if !strings.Contains(name, "/bin/") || !strings.HasPrefix(base, "xray-linux-") {
			continue
		}
		out, err := os.OpenFile(dst, os.O_CREATE|os.O_TRUNC|os.O_WRONLY, 0o755)
		if err != nil {
			return err
		}
		_, copyErr := io.Copy(out, tr)
		closeErr := out.Close()
		if copyErr != nil {
			return copyErr
		}
		if closeErr != nil {
			return closeErr
		}
		return nil
	}
	return fmt.Errorf("安装包中未找到 bin/xray-linux-* 内核")
}

// validatePatchedCore 运行内核 version，确认它是本仓库构建的补丁版（横幅带 -dirty）。
// 这一步保证"绝不会把官方原版内核装进去"。
func validatePatchedCore(path string) error {
	out, err := exec.Command(path, "version").CombinedOutput()
	banner := strings.TrimSpace(string(out))
	if err != nil {
		return fmt.Errorf("内核无法执行: %v (%s)", err, banner)
	}
	if !strings.Contains(banner, "Xray") {
		return fmt.Errorf("内核版本输出异常: %s", banner)
	}
	if !strings.Contains(banner, "-dirty") {
		return fmt.Errorf("拒绝安装未打补丁的内核（版本横幅缺少 -dirty）: %s", banner)
	}
	return nil
}

// extractNamedFileFromPanelPackage 从本仓库 Release 包里取出 bin/ 下的指定数据文件（geoip/geosite）。
func extractNamedFileFromPanelPackage(pkg, baseName, dst string) error {
	f, err := os.Open(pkg)
	if err != nil {
		return err
	}
	defer f.Close()
	gz, err := gzip.NewReader(f)
	if err != nil {
		return err
	}
	defer gz.Close()

	tr := tar.NewReader(gz)
	for {
		h, err := tr.Next()
		if err == io.EOF {
			break
		}
		if err != nil {
			return err
		}
		if h.Typeflag != tar.TypeReg {
			continue
		}
		name := filepath.ToSlash(h.Name)
		if filepath.Base(name) != baseName || !strings.Contains(name, "/bin/") {
			continue
		}
		out, err := os.OpenFile(dst, os.O_CREATE|os.O_TRUNC|os.O_WRONLY, 0o644)
		if err != nil {
			return err
		}
		_, copyErr := io.Copy(out, tr)
		closeErr := out.Close()
		if copyErr != nil {
			return copyErr
		}
		return closeErr
	}
	return fmt.Errorf("安装包中未找到 %s", baseName)
}

// UpdateXray 用本仓库 Release 中的内核替换当前内核：先下载校验，再停服务替换，失败回滚。
func (s *ServerService) UpdateXray(version string) error {
	if runtime.GOOS == "windows" {
		return fmt.Errorf("Windows 面板暂不支持内核在线更新，请使用本仓库 Linux 安装包")
	}
	tmpDir, err := os.MkdirTemp("", "xpanel-core-")
	if err != nil {
		return err
	}
	defer os.RemoveAll(tmpDir)

	pkg := filepath.Join(tmpDir, "panel.tar.gz")
	core := filepath.Join(tmpDir, "xray.new")
	if err := downloadPanelPackage(version, pkg); err != nil {
		return err
	}
	if err := extractCoreFromPanelPackage(pkg, core); err != nil {
		return err
	}
	if err := validatePatchedCore(core); err != nil {
		return err
	}

	target := xray.GetBinaryPath()
	backup := target + ".bak"
	hadOld := false
	if _, err := os.Stat(target); err == nil {
		_ = os.Remove(backup)
		if err := os.Rename(target, backup); err != nil {
			return err
		}
		hadOld = true
	}
	if err := os.Rename(core, target); err != nil {
		if hadOld {
			_ = os.Rename(backup, target)
		}
		return err
	}
	if err := os.Chmod(target, 0o755); err != nil {
		_ = os.Remove(target)
		if hadOld {
			_ = os.Rename(backup, target)
		}
		return err
	}

	if err := s.StopXrayService(); err != nil {
		logger.Warning("failed to stop xray before update:", err)
	}
	if err := s.xrayService.RestartXray(true); err != nil {
		// 回滚到旧内核，避免面板带着不可用内核继续运行
		_ = os.Remove(target)
		if hadOld {
			_ = os.Rename(backup, target)
			_ = s.xrayService.RestartXray(true)
		}
		return err
	}
	return nil
}
