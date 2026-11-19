#!/usr/bin/env bash
# 把本仓库 Release 的安装包/客户端同步到面板 VPS 本地，供国内用户从面板域名下载。
# 用法： bash scripts/sync_panel_mirror.sh [tag] [--all]
#   tag 省略 = 最新 Release；--all = 同步全部 7 个架构（默认只同步本机架构 + XPanel.exe）
# 产物目录：/root/xpanel-mirror（可用 XUI_PKG_MIRROR_DIR 覆盖）
# 面板公开地址：https://<域名>:<端口>/<webBasePath>/download/pkg/<文件名>
set -Eeuo pipefail

REPO="${XUI_PANEL_REPO:-875706361/X-Panel-Linux}"
DIR="${XUI_PKG_MIRROR_DIR:-/root/xpanel-mirror}"
TAG="${1:-}"
ALL="${2:-}"

fail() { echo "同步失败：$*" >&2; exit 1; }
mkdir -p "$DIR"

case "$(uname -m)" in
    x86_64|amd64) HOST_ARCH=amd64 ;;
    aarch64|arm64) HOST_ARCH=arm64 ;;
    armv7l|armv7) HOST_ARCH=armv7 ;;
    armv6l|armv6) HOST_ARCH=armv6 ;;
    armv5l|armv5) HOST_ARCH=armv5 ;;
    i386|i686) HOST_ARCH=386 ;;
    s390x) HOST_ARCH=s390x ;;
    *) fail "不支持的架构: $(uname -m)" ;;
esac

if [[ -z "$TAG" ]]; then
    TAG=$(curl -fsSL -m 20 "https://api.github.com/repos/$REPO/releases/latest" | sed -n 's/.*"tag_name": *"\([^"]*\)".*/\1/p' | head -1)
fi
[[ -n "$TAG" ]] || fail "无法获取 Release tag"

if [[ "$ALL" == "--all" ]]; then
    ARCHES=(amd64 arm64 armv7 armv6 armv5 386 s390x)
else
    ARCHES=("$HOST_ARCH")
fi

echo "仓库: $REPO"
echo "版本: $TAG"
echo "目录: $DIR"
echo "架构: ${ARCHES[*]} (+ XPanel.exe)"

assets=()
for a in "${ARCHES[@]}"; do assets+=("x-ui-linux-$a.tar.gz"); done
assets+=("XPanel.exe")

tmp="$(mktemp -d)"
trap 'rm -rf "$tmp"' EXIT

json_parts=()
for name in "${assets[@]}"; do
    url="https://github.com/$REPO/releases/download/$TAG/$name"
    echo "下载 $name"
    curl -fL --retry 3 -m 300 -o "$tmp/$name" "$url" || fail "下载 $name 失败"
    if [[ "$name" == *.tar.gz ]]; then
        tar -tzf "$tmp/$name" >/dev/null || fail "$name 不是有效的 tar.gz"
    fi
    size=$(stat -c %s "$tmp/$name")
    sha=$(sha256sum "$tmp/$name" | cut -d' ' -f1)
    mv -f "$tmp/$name" "$DIR/$name"
    json_parts+=("  \"$name\": {\"size\": $size, \"sha256\": \"$sha\"}")
done

{
    echo "{"
    echo "  \"tag\": \"$TAG\","
    echo "  \"repo\": \"$REPO\","
    echo "  \"generatedAt\": \"$(date -u +%Y-%m-%dT%H:%M:%SZ)\","
    echo "  \"assets\": {"
    printf '%s,\n' "${json_parts[@]}" | sed '$ s/,$//'
    echo "  }"
    echo "}"
} > "$DIR/latest.json.tmp"
mv -f "$DIR/latest.json.tmp" "$DIR/latest.json"

# 安装/管理脚本也放一份到镜像，国内可用面板域名一键安装
ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
cp -f "$ROOT/install.sh" "$DIR/install.sh"
cp -f "$ROOT/x-ui.sh" "$DIR/x-ui.sh"

echo "完成：$DIR"
ls -lh "$DIR"
echo
echo "公开下载（域名解析变化不影响）："
echo "  https://<域名>:<端口>/<webBasePath>/download/pkg/x-ui-linux-$HOST_ARCH.tar.gz"
echo "  https://<域名>:<端口>/<webBasePath>/download/pkg/XPanel.exe"
