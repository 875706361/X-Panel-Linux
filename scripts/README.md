# X-Panel Pro 功能解锁脚本

本目录包含用于解锁 X-Panel 免费版中 Pro 版功能的脚本。

## 文件说明

| 文件 | 说明 |
|------|------|
| `unlock_all.sh` | 一键运行所有解锁脚本 |
| `unlock_pro.py` | Python 主脚本（原始版本） |
| `unlock.sh` | Shell 脚本，修改 HTML 文件 |
| `unlock_tgbot.sh` | Shell 脚本，修改 tgbot.go 回调 |
| `fix_line575.py` | Python 脚本，修改 tgbot.go 命令 |

## 使用方法

### 方法一：一键运行（推荐）

```bash
cd /clay/11/X-Panel/scripts
sudo bash unlock_all.sh
```

### 方法二：分步运行

```bash
cd /clay/11/X-Panel/scripts

# 1. 修改 HTML 文件
sudo bash unlock.sh

# 2. 修改 tgbot.go 回调
sudo bash unlock_tgbot.sh

# 3. 修改 tgbot.go 命令
sudo python3 fix_line575.py
```

## 解锁的功能

1. **一键配置** - TG Bot 中的 `/oneclick` 命令现在会显示配置选项菜单
2. **欢迎界面** - 首页欢迎弹窗显示 "Pro 版" 而非 "免费基础版"
3. **入站配置** - 一键配置模态框显示配置按钮而非购买提示

## 重新编译

修改完成后，需要重新编译 X-Panel：

```bash
cd /clay/11/X-Panel
go build -o x-panel main.go
```

## 备份

运行脚本时会自动备份 `tgbot.go` 到 `tgbot.go.bak`。

如需恢复原始文件：

```bash
cd /clay/11/X-Panel/web/service
sudo cp tgbot.go.bak tgbot.go
```
