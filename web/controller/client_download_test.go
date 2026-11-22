package controller

import "testing"

// 面板「下载 Windows 客户端」按钮取第一个存在的路径。镜像目录是唯一随 Release 同步的副本，
// 排第一才不会把安装时写下的旧 exe 一直发出去。
func TestWindowsClientPathsPrefersMirror(t *testing.T) {
	if len(windowsClientPaths) == 0 {
		t.Fatal("windowsClientPaths 不能为空")
	}
	if windowsClientPaths[0] != "/root/xpanel-mirror/XPanel.exe" {
		t.Fatalf("首个候选必须是镜像目录，实际 %q", windowsClientPaths[0])
	}
	seen := map[string]bool{}
	for _, p := range windowsClientPaths {
		if seen[p] {
			t.Errorf("重复路径 %q", p)
		}
		seen[p] = true
	}
	// 镜像目录只能出现一次，且必须在前半段（回退路径都排在它后面）。
	for i, p := range windowsClientPaths {
		if p == "/root/xpanel-mirror/XPanel.exe" && i != 0 {
			t.Errorf("镜像路径重复出现在第 %d 位", i)
		}
	}
}
