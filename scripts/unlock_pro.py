#!/usr/bin/env python3
"""
X-Panel Pro 功能解锁脚本
此脚本用于修改 X-Panel 源代码，解锁被限制的 Pro 版功能
使用方法: 在 WSL 中运行 python3 unlock_pro.py
"""

import re
import os

# 文件路径
TGBOT_GO = "/clay/11/X-Panel/web/service/tgbot.go"
INBOUNDS_HTML = "/clay/11/X-Panel/web/html/inbounds.html"
INDEX_HTML = "/clay/11/X-Panel/web/html/index.html"

def unlock_tgbot():
    """解锁 tgbot.go 中的一键配置命令"""
    with open(TGBOT_GO, 'r', encoding='utf-8') as f:
        content = f.read()
    
    # 1. 替换 /oneclick 命令处理
    old_oneclick = '''t.SendMsgToTgbot(chatId, "〔一键配置〕功能现已升级为"付费Pro版"专属功能，\\n\\n请联系面板管理员〔购买授权码〕之后才能继续使用，\\n\\n----->>> "授权码购买"机器人：@Buy_ShouQuan_Bot")'''
    
    new_oneclick = '''// [已解锁] 发送一键配置选项菜单
			inlineKeyboard := tu.InlineKeyboard(
				tu.InlineKeyboardRow(
					tu.InlineKeyboardButton("🚀 Reality + Vision").WithCallbackData(t.encodeQuery("oneclick_reality")),
				),
				tu.InlineKeyboardRow(
					tu.InlineKeyboardButton("⚡ XHTTP + Reality").WithCallbackData(t.encodeQuery("oneclick_xhttp_reality")),
				),
				tu.InlineKeyboardRow(
					tu.InlineKeyboardButton("🛡️ XHTTP + TLS").WithCallbackData(t.encodeQuery("oneclick_tls")),
				),
			)
			t.SendMsgToTgbot(chatId, "🎯 请选择您要一键配置的协议类型：", inlineKeyboard)'''
    
    content = content.replace(old_oneclick, new_oneclick)
    
    # 2. 替换 oneclick_options 回调处理
    old_callback = '''t.sendCallbackAnswerTgBot(callbackQuery.ID, "功能升级提示......")
		 t.SendMsgToTgbot(chatId, "〔一键配置〕功能现已升级为"付费Pro版"专属功能，\\n\\n请联系面板管理员〔购买授权码〕之后才能继续使用，\\n\\n----->>> "授权码购买"机器人：@Buy_ShouQuan_Bot")'''
    
    new_callback = '''t.sendCallbackAnswerTgBot(callbackQuery.ID, "请选择配置类型...")
		 // [已解锁] 发送一键配置选项菜单
		 inlineKeyboard := tu.InlineKeyboard(
			 tu.InlineKeyboardRow(
				 tu.InlineKeyboardButton("🚀 Reality + Vision").WithCallbackData(t.encodeQuery("oneclick_reality")),
			 ),
			 tu.InlineKeyboardRow(
				 tu.InlineKeyboardButton("⚡ XHTTP + Reality").WithCallbackData(t.encodeQuery("oneclick_xhttp_reality")),
			 ),
			 tu.InlineKeyboardRow(
				 tu.InlineKeyboardButton("🛡️ XHTTP + TLS").WithCallbackData(t.encodeQuery("oneclick_tls")),
			 ),
		 )
		 t.SendMsgToTgbot(chatId, "🎯 请选择您要一键配置的协议类型：", inlineKeyboard)'''
    
    content = content.replace(old_callback, new_callback)
    
    with open(TGBOT_GO, 'w', encoding='utf-8') as f:
        f.write(content)
    
    print(f"✅ 已修改: {TGBOT_GO}")

def unlock_inbounds_html():
    """解锁 inbounds.html 中的一键配置模态框"""
    with open(INBOUNDS_HTML, 'r', encoding='utf-8') as f:
        content = f.read()
    
    # 替换一键配置模态框内容
    old_modal = '''message="功能升级提示"
      type="info"'''
    new_modal = '''message="一键配置"
      type="success"'''
    content = content.replace(old_modal, new_modal)
    
    old_desc = '''〔一键配置〕功能现已升级为"付费Pro版"专属功能，
        <br />
        <br />
        请联系面板管理员〔购买授权码〕之后才能继续使用。'''
    new_desc = '''选择下方协议类型，即可快速创建节点配置。'''
    content = content.replace(old_desc, new_desc)
    
    old_icon = '''<template #icon><a-icon type="crown"></a-icon></template>'''
    new_icon = '''<template #icon><a-icon type="rocket"></a-icon></template>'''
    content = content.replace(old_icon, new_icon)
    
    old_link = '''<div :style="{ marginTop: '20px', textAlign: 'center' }">
          <p>
             <a href="https://t.me/Buy_ShouQuan_Bot" target="_blank">"授权码购买"机器人：@Buy_ShouQuan_Bot</a>
          </p>
       </div>'''
    new_link = '''<div :style="{ marginTop: '20px' }">
          <a-button type="primary" block @click="createOneClickConfig('reality')" :style="{ marginBottom: '10px' }">
            🚀 Vless + Reality + Vision
          </a-button>
          <a-button type="primary" block @click="createOneClickConfig('xhttp_reality')" :style="{ marginBottom: '10px' }">
            ⚡ Vless + XHTTP + Reality
          </a-button>
          <a-button type="primary" block @click="createOneClickConfig('tls')">
            🛡️ Vless + XHTTP + TLS
          </a-button>
       </div>'''
    content = content.replace(old_link, new_link)
    
    # 添加 createOneClickConfig 方法
    old_method = '''handleOneClickCancel() {
                this.oneClickModalVisible = false;
                this.inboundLink = '';          // 关闭时清空链接，以便下次重新打开时显示选项
            },

            // 〔中文注释〕: 处理"订阅转换"按钮的点击事件'''
    
    new_method = '''handleOneClickCancel() {
                this.oneClickModalVisible = false;
                this.inboundLink = '';          // 关闭时清空链接，以便下次重新打开时显示选项
            },

            // [已解锁] 创建一键配置
            async createOneClickConfig(configType) {
                this.oneClickModalVisible = false;
                const loadingMessage = this.$message.loading('正在创建 ' + configType + ' 配置，请稍候...', 0);
                
                try {
                    const msg = await HttpUtil.post('/panel/api/inbounds/createOneClick', { type: configType });
                    
                    if (msg.success) {
                        this.$message.success(configType + ' 配置创建成功！');
                        await this.getDBInbounds();
                    } else {
                        this.$message.error('配置创建失败: ' + (msg.msg || '未知错误'));
                    }
                } catch (err) {
                    console.error('创建一键配置失败:', err);
                    this.$message.error('创建配置失败: ' + err.message);
                } finally {
                    loadingMessage();
                }
            },

            // 〔中文注释〕: 处理"订阅转换"按钮的点击事件'''
    
    content = content.replace(old_method, new_method)
    
    with open(INBOUNDS_HTML, 'w', encoding='utf-8') as f:
        f.write(content)
    
    print(f"✅ 已修改: {INBOUNDS_HTML}")

def unlock_index_html():
    """解锁 index.html 中的欢迎弹窗"""
    with open(INDEX_HTML, 'r', encoding='utf-8') as f:
        content = f.read()
    
    # 替换版本标识
    content = content.replace('X-Panel 免费基础版', 'X-Panel Pro 版')
    content = content.replace('免费基础版</b>', 'Pro 专业版</b>')
    content = content.replace('type="smile"', 'type="crown"')
    
    # 替换购买提示
    old_buy = '''如需要使用〔Pro 版〕请联系"授权码购买"机器人：'''
    new_buy = '''🎉 所有功能已解锁，尽情享受！'''
    content = content.replace(old_buy, new_buy)
    
    with open(INDEX_HTML, 'w', encoding='utf-8') as f:
        f.write(content)
    
    print(f"✅ 已修改: {INDEX_HTML}")

if __name__ == "__main__":
    print("=" * 50)
    print("X-Panel Pro 功能解锁脚本")
    print("=" * 50)
    
    try:
        unlock_tgbot()
        unlock_inbounds_html()
        unlock_index_html()
        print("\n✅ 所有修改已完成！")
        print("请重新编译 X-Panel 以应用更改。")
    except Exception as e:
        print(f"\n❌ 发生错误: {e}")
        import traceback
        traceback.print_exc()
