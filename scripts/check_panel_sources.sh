#!/usr/bin/env bash
# 防止安装/更新链路回退到官方仓库。
# 一键安装、面板内更新内核/geo、Docker 构建只允许使用本仓库 Release。
set -Eeuo pipefail

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
cd "$ROOT"

# 检查对象：安装脚本、面板源码、构建脚本（不含测试与 CI 里的工具链下载）
mapfile -t files < <(git ls-files '*.sh' '*.go' | grep -Ev '(^|/)(vendor|web/assets)/|_test\.go$')

pattern='github\.com/(XTLS/Xray-core/releases/download|xeefei|mhsanaei)|raw\.githubusercontent\.com/(xeefei|mhsanaei)'
if hits=$(grep -nE "$pattern" "${files[@]}" 2>/dev/null); then
    echo "发现指向官方仓库的下载地址（必须改为本仓库 Release）：" >&2
    echo "$hits" >&2
    exit 1
fi

# 单点来源：Go 与安装脚本必须声明同一个仓库常量
grep -q 'defaultPanelRepo = "875706361/X-Panel-Linux"' web/service/panel_core.go || {
    echo "panel_core.go 缺少 defaultPanelRepo 常量" >&2
    exit 1
}
grep -q 'PANEL_REPO="\${XUI_PANEL_REPO:-875706361/X-Panel-Linux}"' install.sh || {
    echo "install.sh 缺少 PANEL_REPO 常量" >&2
    exit 1
}

echo "OK: 安装/更新链路全部指向本仓库"
