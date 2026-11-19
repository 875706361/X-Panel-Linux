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

# 同版本且文件齐全时直接跳过，避免每 6 小时重复下载 ~100MB
if [[ "${FORCE:-0}" != "1" && -f "$DIR/latest.json" ]]; then
    cur_tag=$(sed -n 's/.*"tag"[[:space:]]*:[[:space:]]*"\([^"]*\)".*/\1/p' "$DIR/latest.json" | head -1)
    if [[ "$cur_tag" == "$TAG" ]]; then
        all_ok=1
        for f in "x-ui-linux-$HOST_ARCH.tar.gz" XPanel.exe client.json; do
            [[ -s "$DIR/$f" ]] || all_ok=0
        done
        if [[ $all_ok == 1 ]]; then
            echo "镜像已是 $TAG，跳过下载"
            exit 0
        fi
    fi
fi

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

# Android APK 可选：发布里有就同步，没有则跳过（不影响 Windows/面板）
apk_url="https://github.com/$REPO/releases/download/$TAG/xpanel-client.apk"
if curl -fL --retry 2 -m 300 -o "$tmp/xpanel-client.apk" "$apk_url"; then
    mv -f "$tmp/xpanel-client.apk" "$DIR/xpanel-client.apk"
    echo "已同步 xpanel-client.apk"
else
    rm -f "$DIR/xpanel-client.apk"
    echo "Release 无 xpanel-client.apk，跳过安卓客户端"
fi

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

# 客户端自动更新清单（按文件哈希比对，客户端无需懂版本号）
{
    echo "{"
    echo "  \"windows\": {\"file\": \"XPanel.exe\", \"size\": $(stat -c %s "$DIR/XPanel.exe"), \"sha256\": \"$(sha256sum "$DIR/XPanel.exe" | cut -d' ' -f1)\"}"
    if [[ -s "$DIR/xpanel-client.apk" ]]; then
        echo "  ,"
        echo "  \"android\": {\"file\": \"xpanel-client.apk\", \"size\": $(stat -c %s "$DIR/xpanel-client.apk"), \"sha256\": \"$(sha256sum "$DIR/xpanel-client.apk" | cut -d' ' -f1)\"}"
    fi
    echo "}"
} > "$DIR/client.json.tmp"
mv -f "$DIR/client.json.tmp" "$DIR/client.json"

# 安装/管理脚本也放一份到镜像，国内可用面板域名一键安装
# 从刚下载的安装包里取，避免依赖仓库工作目录
pkg="$DIR/x-ui-linux-$HOST_ARCH.tar.gz"
if [[ -f "$pkg" ]]; then
    tar -xzf "$pkg" -C "$tmp" x-ui/x-ui.sh 2>/dev/null || true
    tar -xzf "$pkg" -C "$tmp" x-ui/install.sh 2>/dev/null || true
    if [[ -f "$tmp/x-ui/x-ui.sh" ]]; then cp -f "$tmp/x-ui/x-ui.sh" "$DIR/x-ui.sh"; fi
    if [[ -f "$tmp/x-ui/install.sh" ]]; then cp -f "$tmp/x-ui/install.sh" "$DIR/install.sh"; fi
fi

echo "完成：$DIR"
ls -lh "$DIR"
echo
echo "公开下载（域名解析变化不影响）："
echo "  https://<域名>:<端口>/<webBasePath>/download/pkg/x-ui-linux-$HOST_ARCH.tar.gz"
echo "  https://<域名>:<端口>/<webBasePath>/download/pkg/XPanel.exe"
