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
    local xray_file=""
    
    mkdir -p "$target_dir/bin"
    
    case $platform in
        amd64)  xray_file="Xray-linux-64.zip" ;;
        arm64)  xray_file="Xray-linux-arm64-v8a.zip" ;;
        386)    xray_file="Xray-linux-32.zip" ;;
        armv7)  xray_file="Xray-linux-arm32-v7a.zip" ;;
        armv6)  xray_file="Xray-linux-arm32-v6.zip" ;;
        armv5)  xray_file="Xray-linux-arm32-v5.zip" ;;
        s390x)  xray_file="Xray-linux-s390x.zip" ;;
    esac

    local local_zip="/tmp/$xray_file"
    [ ! -f "$local_zip" ] && wget -q "https://github.com/XTLS/Xray-core/releases/download/$XRAY_VER/$xray_file" -O "$local_zip"
    
    unzip -q -o "$local_zip" -d "$target_dir/bin"
    mv "$target_dir/bin/xray" "$target_dir/bin/xray-linux-$platform" 2>/dev/null || true
    chmod +x "$target_dir/bin/xray-linux-$platform"

    wget -q https://github.com/Loyalsoldier/v2ray-rules-dat/releases/latest/download/geoip.dat -O "$target_dir/bin/geoip.dat"
    wget -q https://github.com/Loyalsoldier/v2ray-rules-dat/releases/latest/download/geosite.dat -O "$target_dir/bin/geosite.dat"
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
