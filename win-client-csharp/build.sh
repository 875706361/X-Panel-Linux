#!/bin/bash
# X-Panel Windows 客户端 (C#) 构建：mcs 交叉编译 WinForms + 内嵌 Xray 内核
# 产物：XPanel.exe（单文件，~36MB，内嵌 xray.exe，Windows 双击即用）
set -e
cd "$(dirname "$0")"

SRC="Program.cs Native.cs Controls.cs LinkParser.cs Xray.cs Updater.cs MainForm.cs MainForm2.cs MainForm3.cs MainForm4.cs"
OUT="XPanel.exe"
WEBEXT="/usr/lib/mono/4.5/System.Web.Extensions.dll"
GZDIR=".resgz"

# 内核文件需先在 resources/ 下
[ -f resources/xray.exe ] || { echo "缺少 resources/xray.exe（Xray Windows 内核）"; exit 1; }

# 先把三个资源 gzip 到 .resgz/，再以 .gz 名字嵌入。
# 直接嵌原始文件会让 exe 涨到 65MB，超 IM 单文件传输限制，只能靠 zip 包裹，
# 而手机端解压会丢执行位导致 exe 无法运行。
rm -rf "$GZDIR"; mkdir -p "$GZDIR"
for f in xray.exe geoip.dat geosite.dat hysteria.exe; do
    gzip -9 -c "resources/$f" > "$GZDIR/$f.gz"
done

mcs -target:winexe -out:"$OUT" $SRC \
    -win32icon:app.ico \
    -resource:"$GZDIR/xray.exe.gz",XPanelWin.resources.xray.exe.gz \
    -resource:"$GZDIR/geoip.dat.gz",XPanelWin.resources.geoip.dat.gz \
    -resource:"$GZDIR/geosite.dat.gz",XPanelWin.resources.geosite.dat.gz \
    -resource:"$GZDIR/hysteria.exe.gz",XPanelWin.resources.hysteria.exe.gz \
    -r:System.Windows.Forms.dll \
    -r:System.Drawing.dll \
    -r:System.IO.Compression.dll \
    -r:System.IO.Compression.FileSystem.dll \
    -r:"$WEBEXT"

rm -rf "$GZDIR"

echo "OK $OUT ($(du -h "$OUT" | cut -f1))"
md5sum "$OUT"
