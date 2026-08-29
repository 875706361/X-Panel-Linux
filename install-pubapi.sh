#!/bin/bash
# X-Panel 公开流量查询接口 一键安装脚本
# 用法: export GITHUB_TOKEN=xxx && bash install-pubapi.sh
# 从 GitHub 拉源码编译，替换 /usr/local/x-ui/x-ui，保留数据库与配置
set -e

if [ -z "$GITHUB_TOKEN" ]; then
    echo "请先设置 GITHUB_TOKEN: export GITHUB_TOKEN=你的token"
    exit 1
fi

REPO="875706361/X-Panel-Linux"
BRANCH="main"
INSTALL_DIR="/usr/local/x-ui"
BIN="$INSTALL_DIR/x-ui"
SVC="x-ui"
TMP=$(mktemp -d)
trap 'rm -rf "$TMP"' EXIT

# 检查 Go（需要 1.25+）
need_go() {
    if command -v go >/dev/null 2>&1 && [ "$(go version | grep -oE 'go1\.[0-9]+' | grep -oE '[0-9]+' | head -1)" -ge 25 ]; then
        return 0
    fi
    echo "--- 安装 Go 1.25 ---"
    wget -q https://go.dev/dl/go1.25.6.linux-amd64.tar.gz -O "$TMP/go.tar.gz"
    tar -C /usr/local -xzf "$TMP/go.tar.gz"
    export PATH=/usr/local/go/bin:$PATH
}

need_go

echo "--- 1. 拉取源码 (${REPO}@${BRANCH}) ---"
git clone --depth 1 -b "$BRANCH" "https://${GITHUB_TOKEN}@github.com/${REPO}.git" "$TMP/src" >/dev/null 2>&1

echo "--- 2. 编译 (约2-5分钟) ---"
cd "$TMP/src"
go build -o "$TMP/x-ui-new" ./

echo "--- 3. 停止服务并备份 ---"
systemctl stop "$SVC" 2>/dev/null || service "$SVC" stop 2>/dev/null || true
if [ -f "$BIN" ]; then
    cp "$BIN" "$BIN.bak.$(date +%Y%m%d%H%M%S)"
    echo "旧版本已备份"
fi

echo "--- 4. 替换并启动 ---"
cp "$TMP/x-ui-new" "$BIN"
chmod +x "$BIN"
systemctl daemon-reload 2>/dev/null || true
systemctl start "$SVC" 2>/dev/null || service "$SVC" start 2>/dev/null || true
sleep 2

echo "--- 5. 验证 ---"
if systemctl is-active "$SVC" >/dev/null 2>&1 || pgrep -f "$BIN" >/dev/null 2>&1; then
    echo "✅ 安装成功，服务运行中"
else
    echo "❌ 服务未运行，请手动检查: journalctl -u $SVC"
fi

echo ""
echo "========== 验证公开接口 =========="
PORT=$(grep -oE 'port:[0-9]+' /etc/x-ui/x-ui.db 2>/dev/null | head -1 || true)
echo "面板地址: http://$(curl -s ifconfig.me 2>/dev/null):${PORT:-54321}"
echo "用法: http://你的IP:端口/panel/pub/traffic/你的vless链接里的uuid"
echo "无需登录即可返回流量/到期信息"
