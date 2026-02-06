#!/bin/bash
# 解锁 tgbot.go 中的一键配置命令

TGBOT="/clay/11/X-Panel/web/service/tgbot.go"
BACKUP="/clay/11/X-Panel/web/service/tgbot.go.bak"

# 备份原文件
cp "$TGBOT" "$BACKUP"
echo "✅ 已备份原文件到 $BACKUP"

# 使用 Python 进行更可靠的替换
python3 << 'PYTHON_SCRIPT'
import re

TGBOT = "/clay/11/X-Panel/web/service/tgbot.go"

with open(TGBOT, 'r', encoding='utf-8') as f:
    lines = f.readlines()

new_lines = []
i = 0
modified = False

while i < len(lines):
    line = lines[i]
    
    # 检测第一处：/oneclick 命令处理
    if 't.SendMsgToTgbot(chatId, "〔一键配置〕功能现已升级为"付费Pro版"专属功能' in line and 'Buy_ShouQuan_Bot' in line:
        # 替换为新代码
        indent = '\t\t\t'
        new_code = f'''{indent}// [已解锁] 发送一键配置选项菜单
{indent}inlineKeyboard := tu.InlineKeyboard(
{indent}\ttu.InlineKeyboardRow(
{indent}\t\ttu.InlineKeyboardButton("🚀 Reality + Vision").WithCallbackData(t.encodeQuery("oneclick_reality")),
{indent}\t),
{indent}\ttu.InlineKeyboardRow(
{indent}\t\ttu.InlineKeyboardButton("⚡ XHTTP + Reality").WithCallbackData(t.encodeQuery("oneclick_xhttp_reality")),
{indent}\t),
{indent}\ttu.InlineKeyboardRow(
{indent}\t\ttu.InlineKeyboardButton("🛡️ XHTTP + TLS").WithCallbackData(t.encodeQuery("oneclick_tls")),
{indent}\t),
{indent})
{indent}t.SendMsgToTgbot(chatId, "🎯 请选择您要一键配置的协议类型：", inlineKeyboard)
'''
        new_lines.append(new_code)
        modified = True
        print(f"✅ 第 {i+1} 行已替换 (/oneclick 命令)")
    
    # 检测第二处：oneclick_options 回调
    elif 't.sendCallbackAnswerTgBot(callbackQuery.ID, "功能升级提示' in line:
        # 跳过这一行和下一行
        indent = '\t\t '
        new_code = f'''{indent}t.sendCallbackAnswerTgBot(callbackQuery.ID, "请选择配置类型...")
{indent}// [已解锁] 发送一键配置选项菜单
{indent}inlineKeyboard := tu.InlineKeyboard(
{indent}\ttu.InlineKeyboardRow(
{indent}\t\ttu.InlineKeyboardButton("🚀 Reality + Vision").WithCallbackData(t.encodeQuery("oneclick_reality")),
{indent}\t),
{indent}\ttu.InlineKeyboardRow(
{indent}\t\ttu.InlineKeyboardButton("⚡ XHTTP + Reality").WithCallbackData(t.encodeQuery("oneclick_xhttp_reality")),
{indent}\t),
{indent}\ttu.InlineKeyboardRow(
{indent}\t\ttu.InlineKeyboardButton("🛡️ XHTTP + TLS").WithCallbackData(t.encodeQuery("oneclick_tls")),
{indent}\t),
{indent})
{indent}t.SendMsgToTgbot(chatId, "🎯 请选择您要一键配置的协议类型：", inlineKeyboard)
'''
        new_lines.append(new_code)
        # 跳过下一行（包含 Buy_ShouQuan_Bot 的行）
        if i + 1 < len(lines) and 'Buy_ShouQuan_Bot' in lines[i + 1]:
            i += 1
        modified = True
        print(f"✅ 第 {i+1} 行已替换 (oneclick_options 回调)")
    else:
        new_lines.append(line)
    
    i += 1

if modified:
    with open(TGBOT, 'w', encoding='utf-8') as f:
        f.writelines(new_lines)
    print("\n✅ tgbot.go 修改完成！")
else:
    print("❌ 未找到需要修改的代码")
PYTHON_SCRIPT

echo ""
echo "=== 验证 ==="
grep -c "oneclick_reality" "$TGBOT" && echo "✅ 验证成功：找到 oneclick_reality"
