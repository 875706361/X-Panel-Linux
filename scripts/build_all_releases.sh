#!/bin/bash
# X-Panel Linux全自动编译打包脚本 - 纯Linux版本

set -e

XRAY_VER="v26.2.2"
LD_FLAGS="-s -w"
PROJECT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
OUTPUT_DIR="$PROJECT_DIR/releases"

mkdir -p "$OUTPUT_DIR"

echo "========================================"
echo "X-Panel Linux打包工具"
echo "========================================"

download_deps() {
    local platform=$1
    local target_dir=$2
    local repo="${XUI_PANEL_REPO:-875706361/X-Panel-Linux}"
    local tmp
    tmp="$(mktemp -d)"
    trap 'rm -rf "$tmp"' RETURN

    mkdir -p "$target_dir/bin"

    # 内核与 geo 数据只从本仓库 Release 取，不再访问 XTLS/Xray-core 官方仓库。
    local pkg="https://github.com/$repo/releases/latest/download/x-ui-linux-$platform.tar.gz"
    echo "下载本仓库内核包: $pkg"
    curl -fL --retry 3 -o "$tmp/panel.tar.gz" "$pkg"
    tar -xzf "$tmp/panel.tar.gz" -C "$tmp"

    local src="$tmp/x-ui/bin"
    local core
    core="$(find "$src" -maxdepth 1 -type f -name 'xray-linux-*' | head -n1)"
    [ -n "$core" ] || { echo "安装包缺少内核" >&2; exit 1; }
    install -m 0755 "$core" "$target_dir/bin/xray-linux-$platform"

    local f
    for f in geoip.dat geosite.dat geoip_IR.dat geosite_IR.dat geoip_RU.dat geosite_RU.dat; do
        [ -f "$src/$f" ] && install -m 0644 "$src/$f" "$target_dir/bin/$f"
    done
}

build_linux() {
    local arch=$1
    local go_arch=$arch
    local go_arm=""
    
    [ "$arch" == "armv5" ] && { go_arch="arm"; go_arm="5"; }
    [ "$arch" == "armv6" ] && { go_arch="arm"; go_arm="6"; }
    [ "$arch" == "armv7" ] && { go_arch="arm"; go_arm="7"; }

    local pkg_name="x-ui-linux-$arch.tar.gz"
    local temp_dir="/tmp/x-panel-build-$arch"
    
    echo ""
    echo "Building Linux $arch ..."
    
    rm -rf "$temp_dir" && mkdir -p "$temp_dir/x-ui"
    
    local cgo=1
    [ "$arch" != "amd64" ] && cgo=0
    
    GOOS=linux GOARCH=$go_arch GOARM=$go_arm CGO_ENABLED=$cgo go build -ldflags "$LD_FLAGS" -o "$temp_dir/x-ui/x-ui" "$PROJECT_DIR/main.go"
    
    cp "$PROJECT_DIR/x-ui.service" "$temp_dir/x-ui/" 2>/dev/null || true
    cp "$PROJECT_DIR/x-ui.sh" "$temp_dir/x-ui/" 2>/dev/null || true
    
    download_deps "$arch" "$temp_dir/x-ui"
    
    cd "$temp_dir"
    tar -zcf "$OUTPUT_DIR/$pkg_name" x-ui
    cd - > /dev/null
    
    rm -rf "$temp_dir"
    echo "[SUCCESS] $pkg_name"
}

if [ "$1" == "" ]; then
    build_linux "amd64"
    build_linux "arm64"
    build_linux "386"
    build_linux "armv7"
    build_linux "armv6"
    build_linux "armv5"
    build_linux "s390x"
else
    build_linux "$1"
fi

echo ""
echo "========================================"
echo "All builds completed!"
ls -lh "$OUTPUT_DIR"
