#!/bin/bash
# X-Panel Windows 客户端 (C#) 构建：mcs 交叉编译 WinForms + 内嵌 Xray 内核
# 产物：XPanel.exe（单文件，~36MB，内嵌 xray.exe，Windows 双击即用）
set -e
cd "$(dirname "$0")"

SRC="Program.cs Native.cs Controls.cs Fonts.cs LinkParser.cs Xray.cs Updater.cs LogForm.cs MainForm.cs MainForm2.cs MainForm3.cs MainForm4.cs"
OUT="XPanel.exe"

# .NET Framework（所有版本）的 String 只有 TrimEnd/TrimStart(params char[])；
# 单字符重载 TrimEnd(char) 是 mono/Core 才有的，mcs 会绑定到它，用户机器上运行时
# MissingMethodException（表现为登录解析失败、更新检查失败、LoadPanelBase 构建异常）。
if grep -rnE "\.Trim(End|Start)\('[^']?'\)" $SRC 2>/dev/null; then
  echo "ERROR: 单字符 TrimEnd/TrimStart 重载在 .NET Framework 不存在，请改用 TrimEnd(new char[] { ... })" >&2
  exit 1
fi
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

# 资源节：app.res（windres 生成）= 图标 + DPI 清单。
# 注意必须用 --output-format=res（.res 容器）：coff 格式会让 mcs 的 IKVM 反射层
# 抛 BadImageFormatException（CS0016 Could not write to file 'XPanel'），
# 而 mcs 的 -win32manifest 只是被静默忽略，不会真的写 RT_MANIFEST。
x86_64-w64-mingw32-windres -i app.rc -o app.res --input-format=rc --output-format=res

mcs -target:winexe -out:"$OUT" $SRC \
    -win32res:app.res \
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
