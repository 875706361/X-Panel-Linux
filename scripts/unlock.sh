#!/bin/bash
# X-Panel Pro 功能解锁脚本
# 使用方法: sudo bash /mnt/c/Users/Administrator/unlock.sh

TGBOT="/clay/11/X-Panel/web/service/tgbot.go"
INBOUNDS="/clay/11/X-Panel/web/html/inbounds.html"
INDEX="/clay/11/X-Panel/web/html/index.html"

echo "=== X-Panel Pro 功能解锁 ==="

# 1. 修改 inbounds.html - 将 info 改为 success
sed -i 's/message="功能升级提示"/message="一键配置"/g' "$INBOUNDS"
sed -i 's/type="info"/type="success"/g' "$INBOUNDS"
sed -i 's/type="crown"/type="rocket"/g' "$INBOUNDS"
echo "✅ inbounds.html 已修改"

# 2. 修改 index.html 已经由 Python 完成了
echo "✅ index.html 已修改 (Python脚本)"

# 3. 显示验证信息
echo ""
echo "=== 验证结果 ==="
grep -c "一键配置" "$INBOUNDS" && echo "✅ inbounds.html 验证成功"
grep -c "Pro 版" "$INDEX" && echo "✅ index.html 验证成功"

echo ""
echo "=== 完成 ==="
echo "请重新编译 X-Panel: cd /clay/11/X-Panel && go build -o x-panel main.go"
