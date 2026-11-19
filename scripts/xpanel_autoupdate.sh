#!/usr/bin/env bash
# 面板全自动维护：同步镜像 -> 升级面板 -> 更新 Windows 客户端 -> 健康检查/回滚。
# 由 xpanel-autoupdate.timer 每 6 小时触发；也可手动执行。
# 环境变量（测试用）：XUI_INSTALL_DIR / XUI_SERVICE / XUI_PANEL_MIRROR / DRY_RUN
set -Eeuo pipefail

REPO="${XUI_PANEL_REPO:-875706361/X-Panel-Linux}"
INSTALL_DIR="${XUI_INSTALL_DIR:-/usr/local/x-ui}"
SERVICE="${XUI_SERVICE:-x-ui}"
MIRROR="${XUI_PANEL_MIRROR:-https://888888666.xyz:1111/clay}"
MIRROR_DIR="${XUI_PKG_MIRROR_DIR:-/root/xpanel-mirror}"
DRY_RUN="${DRY_RUN:-0}"
LOG="${XUI_AUTOUPDATE_LOG:-/var/log/xpanel-autoupdate.log}"

log() { echo "$(date '+%F %T') $*" | tee -a "$LOG"; }
fail() { log "ERROR: $*"; exit 1; }

mkdir -p "$(dirname "$LOG")"
exec 9>"${XUI_LOCK_FILE:-/run/xpanel-autoupdate.lock}"
flock -n 9 || { log "已有任务在运行，跳过"; exit 0; }

[[ -x "$INSTALL_DIR/x-ui" ]] || fail "未找到 $INSTALL_DIR/x-ui"

cur=$("$INSTALL_DIR/x-ui" -v 2>/dev/null | head -1 | tr -d '\r')
[[ -n "$cur" ]] || fail "读取当前版本失败"

# 1) 解析最新版本：优先镜像 latest.json（快、不依赖 GitHub API）
tag=""
if [[ -f "$MIRROR_DIR/latest.json" ]]; then
    tag=$(sed -n 's/.*"tag"[[:space:]]*:[[:space:]]*"\([^"]*\)".*/\1/p' "$MIRROR_DIR/latest.json" | head -1)
fi
if [[ -z "$tag" ]]; then
    tag=$(curl -fsSL -m 20 "https://api.github.com/repos/$REPO/releases/latest" \
        | sed -n 's/.*"tag_name": *"\([^"]*\)".*/\1/p' | head -1 || true)
fi
[[ -n "$tag" ]] || fail "无法获取最新版本（镜像和 GitHub 都不可用）"
tag_ver="${tag#v}"
log "当前=$cur 最新=$tag_ver"

# 已知坏版本不再反复尝试，等下一个 Release
if [[ -f "$MIRROR_DIR/.failed-$tag_ver" ]]; then
    log "跳过已知失败版本 $tag_ver（等待更新版本）"
    exit 0
fi

# 2) 同步镜像（同 tag 时脚本内部会直接跳过）
if [[ "$DRY_RUN" != "1" && -x "$INSTALL_DIR/sync_panel_mirror.sh" ]]; then
    if ! XUI_PANEL_MIRROR="$MIRROR" XUI_PKG_MIRROR_DIR="$MIRROR_DIR" \
        bash "$INSTALL_DIR/sync_panel_mirror.sh" "$tag" >>"$LOG" 2>&1; then
        log "WARN: 镜像同步失败，继续用现有文件"
    fi
fi

# 3) 升级面板（仅在最新版更高时）
newer=$(printf '%s\n%s\n' "$cur" "$tag_ver" | sort -V | tail -1)
if [[ "$cur" == "$tag_ver" || "$newer" != "$tag_ver" ]]; then
    log "面板已是最新，跳过升级"
else
    pkg="$MIRROR_DIR/x-ui-linux-$(uname -m | sed 's/x86_64/amd64/;s/aarch64/arm64/;s/armv7l/armv7/;s/armv6l/armv6/;s/armv5l/armv5/;s/i[36]86/386/').tar.gz"
    if [[ ! -s "$pkg" ]]; then
        pkg=""
        log "WARN: 镜像里没有本机架构包，回退 GitHub"
    fi

    tmp="$(mktemp -d)"
    trap 'rm -rf "$tmp"' EXIT
    if [[ -n "$pkg" ]]; then
        cp -f "$pkg" "$tmp/panel.tar.gz"
    else
        arch=$(uname -m | sed 's/x86_64/amd64/;s/aarch64/arm64/;s/armv7l/armv7/;s/armv6l/armv6/;s/armv5l/armv5/;s/i[36]86/386/')
        url="https://github.com/$REPO/releases/download/$tag/x-ui-linux-$arch.tar.gz"
        log "下载 $url"
        curl -fL --retry 3 -m 300 -o "$tmp/panel.tar.gz" "$url" || fail "下载新版本失败"
    fi
    tar -tzf "$tmp/panel.tar.gz" >/dev/null || fail "新版本包校验失败"
    tar -xzf "$tmp/panel.tar.gz" -C "$tmp"
    [[ -x "$tmp/x-ui/x-ui" ]] || fail "新版本包缺少 x-ui"

    log "开始升级 $cur -> $tag_ver"
    if [[ "$DRY_RUN" == "1" ]]; then
        log "DRY_RUN: 跳过安装与重启"
        exit 0
    fi

    cp -a "$INSTALL_DIR/x-ui" "$INSTALL_DIR/x-ui.bak.$cur"
    if [[ -f "$INSTALL_DIR/x-ui.sh" ]]; then cp -a "$INSTALL_DIR/x-ui.sh" "$INSTALL_DIR/x-ui.sh.bak.$cur"; fi
    # 只备份二进制，避免 /usr/local 被大文件撑满；DB 另存一份
    if [[ -f /etc/x-ui/x-ui.db ]]; then cp -a /etc/x-ui/x-ui.db "/root/x-ui.db.autoupdate.$tag_ver"; fi

    systemctl stop "$SERVICE" || true
    install -m 755 "$tmp/x-ui/x-ui" "$INSTALL_DIR/x-ui"
    if [[ -f "$tmp/x-ui/x-ui.sh" ]]; then install -m 755 "$tmp/x-ui/x-ui.sh" "$INSTALL_DIR/x-ui.sh"; fi
    if [[ -f "$tmp/x-ui/sync_panel_mirror.sh" ]]; then install -m 755 "$tmp/x-ui/sync_panel_mirror.sh" "$INSTALL_DIR/sync_panel_mirror.sh"; fi
    if [[ -f "$tmp/x-ui/autoupdate.sh" ]]; then install -m 755 "$tmp/x-ui/autoupdate.sh" "$INSTALL_DIR/autoupdate.sh"; fi
    if [[ -f "$tmp/x-ui/xpanel-autoupdate.service" ]]; then
        install -m 644 "$tmp/x-ui/xpanel-autoupdate.service" /etc/systemd/system/
    fi
    if [[ -f "$tmp/x-ui/xpanel-autoupdate.timer" ]]; then
        install -m 644 "$tmp/x-ui/xpanel-autoupdate.timer" /etc/systemd/system/
    fi
    systemctl daemon-reload || true
    systemctl enable --now xpanel-autoupdate.timer >/dev/null 2>&1 || true
    systemctl start "$SERVICE" || true

    # 健康检查：服务 active + 面板端口真的能响应（不依赖 ss）
    raw=$("$INSTALL_DIR/x-ui" setting -show true 2>/dev/null | sed 's/\x1b\[[0-9;]*m//g' || true)
    port=$(echo "$raw" | sed -n 's/.*端口号）: *\([0-9]*\).*/\1/p' | head -1)
    base=$(echo "$raw" | sed -n 's/.*webBasePath（访问路径）: *\(.*\)/\1/p' | head -1)
    base=${base%/}
    sleep 6
    ok=0
    if systemctl is-active --quiet "$SERVICE"; then
        if [[ -z "$port" ]]; then
            ok=1
        else
            code=$(curl -sk -m 5 -o /dev/null -w '%{http_code}' "https://127.0.0.1:${port}${base}/" 2>/dev/null || true)
            if [[ -z "$code" || "$code" == "000" ]]; then
                code=$(curl -s -m 5 -o /dev/null -w '%{http_code}' "http://127.0.0.1:${port}${base}/" 2>/dev/null || true)
            fi
            if [[ -n "$code" && "$code" != "000" ]]; then
                ok=1
            fi
        fi
    fi
    if [[ $ok != 1 ]]; then
        log "ERROR: 升级后健康检查失败，回滚到 $cur"
        touch "$MIRROR_DIR/.failed-$tag_ver" 2>/dev/null || true
        systemctl stop "$SERVICE" || true
        if [[ -f "$INSTALL_DIR/x-ui.bak.$cur" ]]; then cp -a "$INSTALL_DIR/x-ui.bak.$cur" "$INSTALL_DIR/x-ui"; fi
        if [[ -f "$INSTALL_DIR/x-ui.sh.bak.$cur" ]]; then cp -a "$INSTALL_DIR/x-ui.sh.bak.$cur" "$INSTALL_DIR/x-ui.sh"; fi
        systemctl start "$SERVICE" || true
        fail "已回滚"
    fi
    log "升级成功，当前 $("$INSTALL_DIR/x-ui" -v | head -1)"
    # 只保留最近 2 份二进制备份、3 份数据库备份
    ls -1t "$INSTALL_DIR"/x-ui.bak.* 2>/dev/null | tail -n +3 | xargs -r rm -f
    ls -1t /root/x-ui.db.autoupdate.* 2>/dev/null | tail -n +4 | xargs -r rm -f
fi

# 4) 更新 Windows 客户端（版本变化或文件缺失时）
if [[ "$DRY_RUN" != "1" && -f "$MIRROR_DIR/XPanel.exe" ]]; then
    if ! cmp -s "$MIRROR_DIR/XPanel.exe" /root/win-client-csharp/XPanel.exe 2>/dev/null; then
        mkdir -p /root/win-client-csharp
        cp -f "$MIRROR_DIR/XPanel.exe" /root/win-client-csharp/XPanel.exe
        cp -f "$MIRROR_DIR/XPanel.exe" /root/XPanel.exe
        cp -f "$MIRROR_DIR/XPanel.exe" /root/客户端.exe
        log "已更新 Windows 客户端"
    fi
fi

log "自动维护完成"
