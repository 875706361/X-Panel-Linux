#!/bin/sh
# 只从本仓库 Release 取内核与 geo 数据；不再下载 XTLS/Xray-core 官方包。
set -eu

REPO="${XUI_PANEL_REPO:-875706361/X-Panel-Linux}"

case $1 in
    amd64|x86_64)
        ASSET="amd64"; BIN="amd64"
        ;;
    i386|386)
        ASSET="386"; BIN="386"
        ;;
    armv8|arm64|aarch64)
        ASSET="arm64"; BIN="arm64"
        ;;
    armv7|arm|arm32)
        ASSET="armv7"; BIN="arm"
        ;;
    armv6)
        ASSET="armv6"; BIN="arm"
        ;;
    armv5)
        ASSET="armv5"; BIN="arm"
        ;;
    s390x)
        ASSET="s390x"; BIN="s390x"
        ;;
    *)
        echo "不支持的架构: $1" >&2
        exit 1
        ;;
esac

mkdir -p build/bin
TMP="$(mktemp -d)"
trap 'rm -rf "$TMP"' EXIT

URL="https://github.com/${REPO}/releases/latest/download/x-ui-linux-${ASSET}.tar.gz"
echo "从本仓库下载内核: $URL"
curl -fL --retry 3 -o "$TMP/panel.tar.gz" "$URL"
tar -xzf "$TMP/panel.tar.gz" -C "$TMP"

SRC="$TMP/x-ui/bin"
[ -d "$SRC" ] || { echo "安装包缺少 bin/ 目录" >&2; exit 1; }

CORE="$(find "$SRC" -maxdepth 1 -type f -name 'xray-linux-*' | head -n1)"
[ -n "$CORE" ] || { echo "安装包缺少 xray-linux-* 内核" >&2; exit 1; }

install -m 0755 "$CORE" "build/bin/xray-linux-${BIN}"

# geo 数据同样取自本仓库安装包，避免额外访问第三方仓库
for f in geoip.dat geosite.dat geoip_IR.dat geosite_IR.dat geoip_RU.dat geosite_RU.dat; do
    [ -f "$SRC/$f" ] && install -m 0644 "$SRC/$f" "build/bin/$f"
done

# 校验：补丁版内核横幅必须带 -dirty（宿主架构可直接执行时）
if [ "$ASSET" = "amd64" ] && [ "$(uname -m)" = "x86_64" ]; then
    "build/bin/xray-linux-${BIN}" version | grep -q -- '-dirty' || {
        echo "内核不是补丁版（缺少 -dirty）" >&2
        exit 1
    }
fi

echo "内核就绪: build/bin/xray-linux-${BIN}"
