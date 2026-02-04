#!/bin/bash
# X-Panel 跨平台全自动编译打包脚本
# 模仿 GitHub CI 流程，生成标准的安装包

set -e

# 配置区域
XRAY_VER="v26.2.2"
LD_FLAGS="-s -w"
PROJECT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
OUTPUT_DIR="$PROJECT_DIR/releases"

# 创建输出目录
mkdir -p "$OUTPUT_DIR"

echo "========================================"
echo "    X-Panel 跨平台打包工具"
echo "========================================"
echo "项目路径: $PROJECT_DIR"
echo "输出路径: $OUTPUT_DIR"
echo "========================================"

# 函数：下载 Xray 和 Geo 数据
download_deps() {
    local platform=$1
    local target_dir=$2
    local xray_file=""

    echo "📦 正在下载 $platform 的依赖组件 (Xray $XRAY_VER)..."
    
    mkdir -p "$target_dir/bin"
    
    case $platform in
        amd64) xray_file="Xray-linux-64.zip" ;;
        arm64) xray_file="Xray-linux-arm64-v8a.zip" ;;
        386)   xray_file="Xray-linux-32.zip" ;;
        armv7) xray_file="Xray-linux-arm32-v7a.zip" ;;
        armv6) xray_file="Xray-linux-arm32-v6.zip" ;;
        armv5) xray_file="Xray-linux-arm32-v5.zip" ;;
        s390x) xray_file="Xray-linux-s390x.zip" ;;
        win-amd64) xray_file="Xray-windows-64.zip" ;;
        win-386)   xray_file="Xray-windows-32.zip" ;;
    esac

    # 下载 Xray (如果本地没有缓存)
    local local_zip="/tmp/$xray_file"
    if [ ! -f "$local_zip" ]; then
        wget -q "https://github.com/XTLS/Xray-core/releases/download/$XRAY_VER/$xray_file" -O "$local_zip"
    fi
    
    unzip -q -o "$local_zip" -d "$target_dir/bin"
    
    if [[ $platform == win* ]]; then
        mv "$target_dir/bin/xray.exe" "$target_dir/bin/xray-windows-${platform#win-}.exe" 2>/dev/null || true
    else
        mv "$target_dir/bin/xray" "$target_dir/bin/xray-linux-$platform" 2>/dev/null || true
        chmod +x "$target_dir/bin/xray-linux-$platform"
    fi

    # 下载 Geo 数据 (每次更新以保证最新)
    echo "🌍 正在同步 Geo 数据文件..."
    wget -q https://github.com/Loyalsoldier/v2ray-rules-dat/releases/latest/download/geoip.dat -O "$target_dir/bin/geoip.dat"
    wget -q https://github.com/Loyalsoldier/v2ray-rules-dat/releases/latest/download/geosite.dat -O "$target_dir/bin/geosite.dat"
}

# 函数：打包 Linux
build_linux() {
    local arch=$1
    local go_arch=$arch
    local go_arm=""
    
    if [[ $arch == armv5 ]]; then go_arch="arm"; go_arm="5"; fi
    if [[ $arch == armv6 ]]; then go_arch="arm"; go_arm="6"; fi
    if [[ $arch == armv7 ]]; then go_arch="arm"; go_arm="7"; fi

    local pkg_name="x-ui-linux-$arch.tar.gz"
    local temp_dir="/tmp/x-panel-build-$arch"
    
    echo ""
    echo "🚀 >>> 正在构建 Linux $arch <<<"
    
    rm -rf "$temp_dir" && mkdir -p "$temp_dir/x-ui"
    
    # 编译主程序
    # 注意：跨平台 CGO 编译需要对应的工具链。如果环境没有，这里会报错。
    # 对于本系统的架构 (amd64)，开启 CGO。对于其他架构，如果失败则尝试关闭 CGO 编译。
    local cgo=1
    if [[ $arch != "amd64" ]]; then cgo=0; fi # 交叉编译默认关闭 CGO 以提高兼容性
    
    echo "💻 正在执行 Go 编译 (GOARCH=$go_arch, CGO=$cgo)..."
    GOOS=linux GOARCH=$go_arch GOARM=$go_arm CGO_ENABLED=$cgo go build -ldflags "$LD_FLAGS" -o "$temp_dir/x-ui/x-ui" "$PROJECT_DIR/main.go"
    
    # 复制辅助文件
    cp "$PROJECT_DIR/x-ui.service" "$temp_dir/x-ui/" 2>/dev/null || touch "$temp_dir/x-ui/x-ui.service"
    cp "$PROJECT_DIR/x-ui.sh" "$temp_dir/x-ui/" 2>/dev/null || touch "$temp_dir/x-ui/x-ui.sh"
    
    # 下载依赖
    download_deps "$arch" "$temp_dir/x-ui"
    
    # 打包
    echo "🎁 正在打包生成 $pkg_name..."
    cd "$temp_dir"
    tar -zcf "$OUTPUT_DIR/$pkg_name" x-ui
    cd - > /dev/null
    
    rm -rf "$temp_dir"
    echo "✅ [SUCCESS] $pkg_name"
}

# 函数：打包 Windows
build_windows() {
    local arch=$1
    local pkg_name="x-ui-windows-$arch.zip"
    local temp_dir="/tmp/x-panel-build-win-$arch"
    
    echo ""
    echo "🚀 >>> 正在构建 Windows $arch <<<"
    
    rm -rf "$temp_dir" && mkdir -p "$temp_dir/x-ui"
    
    # 编译主程序
    echo "💻 正在执行 Go 编译 (GOOS=windows, GOARCH=$arch)..."
    GOOS=windows GOARCH=$arch CGO_ENABLED=0 go build -ldflags "$LD_FLAGS" -o "$temp_dir/x-ui/x-ui.exe" "$PROJECT_DIR/main.go"
    
    # 复制 Windows 辅助文件
    if [ -d "$PROJECT_DIR/windows_files" ]; then
        cp -r "$PROJECT_DIR/windows_files/"* "$temp_dir/x-ui/"
    fi
    
    # 下载依赖
    download_deps "win-$arch" "$temp_dir/x-ui"
    
    # 打包
    echo "🎁 正在打包生成 $pkg_name..."
    cd "$temp_dir"
    zip -r "$OUTPUT_DIR/$pkg_name" x-ui
    cd - > /dev/null
    
    rm -rf "$temp_dir"
    echo "✅ [SUCCESS] $pkg_name"
}

# 主程序逻辑
if [ "$1" == "" ]; then
    # 默认构建用户请求的所有包
    build_linux "amd64"
    build_linux "arm64"
    build_linux "386"
    build_linux "armv7"
    build_linux "armv6"
    build_linux "armv5"
    build_linux "s390x"
    build_windows "amd64"
    build_windows "386"
else
    # 构建指定架构
    if [[ $1 == win-* ]]; then
        build_windows "${1#win-}"
    else
        build_linux "$1"
    fi
fi

echo ""
echo "========================================"
echo " ✨ 所有打包任务已执行完毕！"
echo " 📂 产物目录: $OUTPUT_DIR"
echo "========================================"
ls -lh "$OUTPUT_DIR"
