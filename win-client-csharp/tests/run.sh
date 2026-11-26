#!/usr/bin/env bash
# 客户端逻辑单测：mcs 编译全部源码 + tests，分别以 ExpiryTests / Hy2Tests 为主入口，mono 运行。
# 依赖：mono-devel（mcs、mono）。不需要 resources/ 里的内核文件。
set -e
cd "$(dirname "$0")/.."

SRC="Program.cs Native.cs Controls.cs Fonts.cs LinkParser.cs Xray.cs Probe.cs Updater.cs \
     MainForm.cs MainForm2.cs MainForm3.cs MainForm4.cs"
REFS="-r:System.Windows.Forms.dll -r:System.Drawing.dll \
      -r:/usr/lib/mono/4.5/System.Web.Extensions.dll \
      -r:System.IO.Compression.dll -r:System.IO.Compression.FileSystem.dll"

build() {   # build <out.exe> <main class> <test file>
    # shellcheck disable=SC2086
    mcs -out:"$1" $SRC "$3" -main:"$2" $REFS 2>&1 | grep -v 'warning CS0649' || true
    [ -f "$1" ]
}

rc=0
build /tmp/xpanel-expiry-tests.exe XPanelWin.ExpiryTests tests/ExpiryTests.cs
mono /tmp/xpanel-expiry-tests.exe || rc=1

build /tmp/xpanel-hy2-tests.exe XPanelWin.Hy2Tests tests/Hy2Tests.cs
mono /tmp/xpanel-hy2-tests.exe || rc=1

# 自动更新链路冒烟（需要网络：直连面板镜像取 client.json）
build /tmp/xpanel-updater-tests.exe XPanelWin.UpdaterTests tests/UpdaterTests.cs
mono /tmp/xpanel-updater-tests.exe || rc=1

# 字体系统：字号=逻辑像素、随 DPI 等比放大、装得进槽位
build /tmp/xpanel-font-tests.exe XPanelWin.FontTests tests/FontTests.cs
mono /tmp/xpanel-font-tests.exe || rc=1

# 连接后探测链路（CONNECT → TLS → 204）：需要一个本地 CONNECT 代理监听 10809，
# 所以默认不跑，XPL_PROBE_LIVE=1 时才执行。
if [ "$XPL_PROBE_LIVE" = "1" ]; then
    build /tmp/xpanel-probe-tests.exe XPanelWin.ProbeTest tests/ProbeTest.cs
    mono /tmp/xpanel-probe-tests.exe || rc=1
fi

exit $rc
