package service

import (
	"archive/tar"
	"compress/gzip"
	"os"
	"path/filepath"
	"runtime"
	"strings"
	"testing"
)

func TestPanelCoreAssetURLStaysOnOwnRepo(t *testing.T) {
	t.Setenv("XUI_PANEL_REPO", "")
	got := PanelCoreAssetURL("26.10.37")
	if !strings.HasPrefix(got, "https://github.com/875706361/X-Panel-Linux/releases/download/v26.10.37/") {
		t.Fatalf("资产地址未指向本仓库: %s", got)
	}
	if !strings.HasSuffix(got, "x-ui-linux-"+PanelCoreAssetArch()+".tar.gz") {
		t.Fatalf("资产名与架构不匹配: %s", got)
	}
	for _, forbidden := range []string{"XTLS/Xray-core", "xeefei", "mhsanaei"} {
		if strings.Contains(got, forbidden) {
			t.Fatalf("资产地址仍引用官方仓库 %s: %s", forbidden, got)
		}
	}
	if !strings.HasPrefix(PanelReleasesURL(), "https://api.github.com/repos/875706361/X-Panel-Linux/releases") {
		t.Fatalf("版本列表未指向本仓库: %s", PanelReleasesURL())
	}
}

func TestPanelRepoOverride(t *testing.T) {
	t.Setenv("XUI_PANEL_REPO", "someone/fork.git")
	if got := PanelRepo(); got != "someone/fork" {
		t.Fatalf("仓库覆盖未生效: %s", got)
	}
}

func TestExtractCoreRejectsPackageWithoutCore(t *testing.T) {
	dir := t.TempDir()
	pkg := filepath.Join(dir, "empty.tar.gz")
	f, err := os.Create(pkg)
	if err != nil {
		t.Fatal(err)
	}
	gz := gzip.NewWriter(f)
	tw := tar.NewWriter(gz)
	body := []byte("hello")
	_ = tw.WriteHeader(&tar.Header{Name: "x-ui/readme.txt", Mode: 0o644, Size: int64(len(body))})
	_, _ = tw.Write(body)
	_ = tw.Close()
	_ = gz.Close()
	_ = f.Close()
	if err := extractCoreFromPanelPackage(pkg, filepath.Join(dir, "core")); err == nil {
		t.Fatal("缺少内核的包应报错")
	}
}

func TestGetXrayVersionsListsOwnReleases(t *testing.T) {
	if os.Getenv("XUI_TEST_NETWORK") == "" {
		t.Skip("XUI_TEST_NETWORK 未设置")
	}
	versions, err := (&ServerService{}).GetXrayVersions()
	if err != nil {
		t.Fatalf("获取版本失败: %v", err)
	}
	if len(versions) == 0 {
		t.Fatal("版本列表为空")
	}
	for _, v := range versions {
		if !strings.HasPrefix(v, "v26.10.") {
			t.Fatalf("版本列表混入了非本仓库版本: %s", v)
		}
	}
}

// 集成测试：设 XUI_TEST_PANEL_PKG 为真实 Release 包路径，验证能取出带 -dirty 的补丁内核。
func TestExtractPatchedCoreFromRealPackage(t *testing.T) {
	pkg := os.Getenv("XUI_TEST_PANEL_PKG")
	if pkg == "" {
		t.Skip("XUI_TEST_PANEL_PKG 未设置")
	}
	if runtime.GOOS != "linux" || PanelCoreAssetArch() != "amd64" {
		t.Skip("仅在 linux/amd64 上执行二进制校验")
	}
	dst := filepath.Join(t.TempDir(), "xray")
	if err := extractCoreFromPanelPackage(pkg, dst); err != nil {
		t.Fatalf("提取内核失败: %v", err)
	}
	if err := validatePatchedCore(dst); err != nil {
		t.Fatalf("内核校验失败: %v", err)
	}

	geo := filepath.Join(t.TempDir(), "geoip.dat")
	if err := extractNamedFileFromPanelPackage(pkg, "geoip.dat", geo); err != nil {
		t.Fatalf("提取 geoip.dat 失败: %v", err)
	}
	st, err := os.Stat(geo)
	if err != nil || st.Size() < 1<<20 {
		t.Fatalf("geoip.dat 大小异常: %v %v", st, err)
	}
}
