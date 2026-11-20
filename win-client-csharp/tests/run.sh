#!/usr/bin/env bash
# 客户端逻辑单测：mcs 编译全部源码 + tests，指定 ExpiryTests 为主入口，mono 运行。
# 依赖：mono-devel（mcs、mono）。不需要 resources/ 里的内核文件。
set -e
cd "$(dirname "$0")/.."

mcs -out:/tmp/xpanel-expiry-tests.exe \
    Program.cs Native.cs Controls.cs LinkParser.cs Xray.cs Updater.cs \
    MainForm.cs MainForm2.cs MainForm3.cs MainForm4.cs tests/ExpiryTests.cs \
    -main:XPanelWin.ExpiryTests \
    -r:System.Windows.Forms.dll -r:System.Drawing.dll \
    -r:/usr/lib/mono/4.5/System.Web.Extensions.dll \
    -r:System.IO.Compression.dll -r:System.IO.Compression.FileSystem.dll 2>&1 | grep -v 'warning CS0649' || true

mono /tmp/xpanel-expiry-tests.exe
