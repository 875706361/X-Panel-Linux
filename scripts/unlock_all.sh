#!/bin/bash
# X-Panel Pro 功能解锁 - 一键脚本
# 使用方法: sudo bash unlock_all.sh
# 
# 此脚本将解锁 X-Panel 免费版中被限制的 Pro 版功能
# 包括：一键配置功能、欢迎界面更新等

set -e

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
PROJECT_DIR="$(dirname "$SCRIPT_DIR")"

echo "========================================"
echo "  X-Panel Pro 功能解锁脚本"
echo "========================================"
echo ""
echo "项目目录: $PROJECT_DIR"
echo ""

# 检查是否以 root 运行
if [ "$EUID" -ne 0 ]; then
    echo "❌ 请使用 sudo 运行此脚本"
    exit 1
fi

# 步骤 1: 解锁 HTML 文件
echo "📝 步骤 1/3: 修改 HTML 文件..."
bash "$SCRIPT_DIR/unlock.sh"

# 步骤 2: 解锁 tgbot.go 回调
echo ""
echo "📝 步骤 2/3: 修改 tgbot.go 回调..."
bash "$SCRIPT_DIR/unlock_tgbot.sh"

# 步骤 3: 修复 tgbot.go 命令行
echo ""
echo "📝 步骤 3/3: 修改 tgbot.go 命令..."
python3 "$SCRIPT_DIR/fix_line575.py"

echo ""
echo "========================================"
echo "  ✅ 所有修改已完成！"
echo "========================================"
echo ""
echo "下一步操作:"
echo "  cd $PROJECT_DIR"
echo "  go build -o x-panel main.go"
echo ""
