#!/usr/bin/env bash
set -Eeuo pipefail

REPO="875706361/X-Panel-Linux"
INSTALL_DIR="/usr/local/x-ui"
SERVICE="/etc/systemd/system/x-ui.service"
TMP="$(mktemp -d)"
trap 'rm -rf "$TMP"' EXIT

fail() { echo "安装失败：$*" >&2; exit 1; }
[[ $EUID -eq 0 ]] || fail "请使用 root 运行：sudo bash install-one-click.sh"
command -v curl >/dev/null || fail "缺少 curl，请先安装 curl"
command -v tar >/dev/null || fail "缺少 tar，请先安装 tar"
command -v systemctl >/dev/null || fail "需要 systemd"

. /etc/os-release
case "${ID:-}" in
  ubuntu|debian|armbian) command -v apt-get >/dev/null || fail "apt-get 不可用" ;;
  centos|rhel|almalinux|rocky|fedora|amzn) command -v dnf >/dev/null || command -v yum >/dev/null || fail "dnf/yum 不可用" ;;
  *) echo "警告：未验证发行版 ${ID:-unknown}，继续尝试安装。" ;;
esac

arch="$(uname -m)"
case "$arch" in
  x86_64|amd64) asset_arch=amd64 ;;
  aarch64|arm64) asset_arch=arm64 ;;
  i386|i686) asset_arch=386 ;;
  armv7l|armv7) asset_arch=armv7 ;;
  armv6l|armv6) asset_arch=armv6 ;;
  armv5l|armv5) asset_arch=armv5 ;;
  s390x) asset_arch=s390x ;;
  *) fail "不支持的架构：$arch" ;;
esac

if command -v apt-get >/dev/null; then
  apt-get update -qq
  DEBIAN_FRONTEND=noninteractive apt-get install -y -qq curl ca-certificates tar gzip unzip tzdata
elif command -v dnf >/dev/null; then
  dnf install -y -q curl ca-certificates tar gzip unzip tzdata
elif command -v yum >/dev/null; then
  yum install -y -q curl ca-certificates tar gzip unzip tzdata
fi

api="https://api.github.com/repos/${REPO}/releases/latest"
json="$(curl --fail --silent --show-error --location --retry 3 "$api")" || fail "读取 GitHub Release 失败。当前仓库没有 Release 时不能安装。"
asset="x-ui-linux-${asset_arch}.tar.gz"
url="$(printf '%s' "$json" | python3 -c 'import json,sys; d=json.load(sys.stdin); n=sys.argv[1]; print(next((a["browser_download_url"] for a in d.get("assets",[]) if a.get("name")==n),""))' "$asset" 2>/dev/null || true)"
[[ -n "$url" ]] || fail "最新 Release 缺少资源：$asset"

echo "下载 X-Panel ${asset} ..."
curl --fail --silent --show-error --location --retry 3 "$url" -o "$TMP/package.tar.gz"
tar -tzf "$TMP/package.tar.gz" >/dev/null || fail "安装包校验失败"

systemctl stop x-ui 2>/dev/null || true
rm -rf "$INSTALL_DIR"
tar -xzf "$TMP/package.tar.gz" -C /usr/local
[[ -x "$INSTALL_DIR/x-ui" ]] || fail "安装包缺少 x-ui 二进制"
chmod +x "$INSTALL_DIR/x-ui" "$INSTALL_DIR/x-ui.sh" "$INSTALL_DIR/bin/"* 2>/dev/null || true
ln -sf "$INSTALL_DIR/x-ui.sh" /usr/bin/x-ui

if [[ ! -f /etc/x-ui/x-ui.db ]]; then
  mkdir -p /etc/x-ui
  "$INSTALL_DIR/x-ui" setting -username "admin" -password "$(tr -dc 'A-Za-z0-9' </dev/urandom | head -c 20)" >/dev/null 2>&1 || true
fi
cp "$INSTALL_DIR/x-ui.service" "$SERVICE"
systemctl daemon-reload
systemctl enable --now x-ui
sleep 2
systemctl is-active --quiet x-ui || { systemctl status x-ui --no-pager; fail "x-ui 启动失败"; }

port="$($INSTALL_DIR/x-ui setting -show true 2>/dev/null | grep -Eo 'port（端口号）: [0-9]+' | awk '{print $2}' | head -n1 || true)"
port="${port:-2053}"
echo "安装完成：x-ui 已运行，端口 ${port}。"
echo "查看状态：systemctl status x-ui"
echo "进入管理：x-ui"
