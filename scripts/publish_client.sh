#!/usr/bin/env bash
# 一键分发 Windows 客户端：
#   1) 构建好的 XPanel.exe 作为 Release 资产上传（同名覆盖）
#   2) 面板执行 x-ui update / install.sh 时会自动把它放到 /root/win-client-csharp/
#   3) 用户可通过面板公开链接一键下载： https://<面板>/<basePath>/download/xpanel.exe
#
# 用法：bash scripts/publish_client.sh [exe路径] [tag]
#   tag 省略时取本仓库 latest release
set -Eeuo pipefail

REPO="${XUI_PANEL_REPO:-875706361/X-Panel-Linux}"
EXE="${1:-/root/win-client-csharp/XPanel.exe}"

fail() { echo "发布失败：$*" >&2; exit 1; }
command -v gh >/dev/null || fail "缺少 gh"
[ -f "$EXE" ] || fail "找不到 $EXE（先运行 win-client-csharp/build.sh）"

TAG="${2:-$(gh release view --repo "$REPO" --json tagName -q .tagName)}"
[ -n "$TAG" ] || fail "无法确定 Release tag"

echo "仓库: $REPO"
echo "版本: $TAG"
echo "文件: $EXE ($(du -h "$EXE" | cut -f1))  md5=$(md5sum "$EXE" | cut -d' ' -f1)"

gh release upload "$TAG" "$EXE#XPanel.exe" --repo "$REPO" --clobber

gh release view "$TAG" --repo "$REPO" --json assets \
  --jq '.assets[] | select(.name=="XPanel.exe") | "已上传: \(.name) \(.size) bytes"'
[ "$(gh release view "$TAG" --repo "$REPO" --json assets --jq '[.assets[]|select(.name=="XPanel.exe")]|length')" = "1" ] \
  || fail "Release 中没有 XPanel.exe"

echo "完成。面板更新后下载地址（公开，无需登录）："
echo "  https://<面板域名或IP>:<端口>/<webBasePath>/download/xpanel.exe"
