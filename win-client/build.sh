#!/bin/bash
# 客户端 Win 版构建：GUI 子系统（-H windowsgui），不弹 cmd 黑窗
export CGO_ENABLED=0
set -e
cd "$(dirname "$0")/.."
OUT=web/assets/xpanel-windows.exe
GOOS=windows GOARCH=amd64 go build -ldflags "-H windowsgui -s -w" -o "$OUT" ./win-client/
echo "OK $OUT ($(du -h "$OUT" | cut -f1))"
