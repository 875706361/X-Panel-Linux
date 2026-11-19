package controller

import "testing"

func TestPkgMirrorAllowlist(t *testing.T) {
	allow := []string{
		"x-ui-linux-amd64.tar.gz",
		"XPanel.exe",
		"xpanel-client.apk",
		"latest.json",
		"client.json",
		"install.sh",
		"x-ui.sh",
	}
	for _, name := range allow {
		if !pkgMirrorAllow.MatchString(name) {
			t.Errorf("应允许 %q", name)
		}
	}
	deny := []string{
		"..%2Fetc%2Fpasswd",
		"../../../etc/passwd",
		"x-ui-linux-mips.tar.gz",
		"client.json.bak",
		"XPanel.exe.exe",
		"",
	}
	for _, name := range deny {
		if pkgMirrorAllow.MatchString(name) {
			t.Errorf("不应允许 %q", name)
		}
	}
}
