#!/usr/bin/env python3
"""修改 tgbot.go 第 575 行"""

TGBOT = "/clay/11/X-Panel/web/service/tgbot.go"

with open(TGBOT, 'r', encoding='utf-8') as f:
    lines = f.readlines()

# 第 575 行（索引 574）
target_line = 574

new_code = '''\t\t\t// [已解锁] 发送一键配置选项菜单
\t\t\tinlineKeyboard := tu.InlineKeyboard(
\t\t\t\ttu.InlineKeyboardRow(
\t\t\t\t\ttu.InlineKeyboardButton("🚀 Reality + Vision").WithCallbackData(t.encodeQuery("oneclick_reality")),
\t\t\t\t),
\t\t\t\ttu.InlineKeyboardRow(
\t\t\t\t\ttu.InlineKeyboardButton("⚡ XHTTP + Reality").WithCallbackData(t.encodeQuery("oneclick_xhttp_reality")),
\t\t\t\t),
\t\t\t\ttu.InlineKeyboardRow(
\t\t\t\t\ttu.InlineKeyboardButton("🛡️ XHTTP + TLS").WithCallbackData(t.encodeQuery("oneclick_tls")),
\t\t\t\t),
\t\t\t)
\t\t\tt.SendMsgToTgbot(chatId, "🎯 请选择您要一键配置的协议类型：", inlineKeyboard)
'''

if 'Buy_ShouQuan_Bot' in lines[target_line]:
    lines[target_line] = new_code
    with open(TGBOT, 'w', encoding='utf-8') as f:
        f.writelines(lines)
    print(f"✅ 第 575 行已替换！")
else:
    print(f"❌ 第 575 行内容不匹配: {lines[target_line][:50]}...")
