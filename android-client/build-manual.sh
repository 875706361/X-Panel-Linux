#!/bin/bash
# X-Panel 安卓客户端手工构建（不依赖 gradle：aapt2 + javac + d8 + apksigner）
# ponytail: 显式 JDK 17 + build-tools 34，避免 systemd 缺 JAVA_HOME 用到 JDK 21 (D8 不认 class file v65)
set -e
export JAVA_HOME=${JAVA_HOME:-/usr/lib/jvm/java-17-openjdk-amd64}
export PATH=$JAVA_HOME/bin:$PATH

SDK=${ANDROID_SDK:-/opt/android-sdk}
BT=$SDK/build-tools/34.0.0
PLATFORM=$SDK/platforms/android-34
PROJECT=$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)
OUT=$PROJECT/build
DIST=${XUI_ANDROID_OUT:-$PROJECT/dist}
rm -rf "$OUT"; mkdir -p "$OUT/classes" "$OUT/dex" "$OUT/gen" "$DIST"

echo "=== 0. 检查依赖文件 ==="
SO=$PROJECT/src/main/jniLibs/arm64-v8a/libxpanel.so
[[ -s "$SO" ]] || { echo "缺少 $SO（Xray 内核库，见 README）；只编译 UI 不放内核也能出包，但无法启动 VPN"; exit 1; }
[[ -e "$PROJECT/src/main/assets/sing-box" ]] || { echo "缺少 src/main/assets/sing-box（见 README）"; exit 1; }

echo "=== 1. 编译资源 ==="
$BT/aapt2 compile -o "$OUT/res.zip" \
  "$PROJECT/src/main/res/layout/activity_main.xml" \
  "$PROJECT/src/main/res/layout/activity_app_picker.xml" \
  "$PROJECT/src/main/res/layout/app_picker_item.xml" \
  "$PROJECT/src/main/res/values/styles.xml" \
  "$PROJECT/src/main/res/drawable/ic_launcher.xml" \
  "$PROJECT/src/main/res/drawable/card_bg.xml" \
  "$PROJECT/src/main/res/drawable/input_bg.xml" \
  "$PROJECT/src/main/res/drawable/btn_primary.xml" \
  "$PROJECT/src/main/res/drawable/btn_power.xml" \
  "$PROJECT/src/main/res/drawable/btn_power_on.xml" \
  "$PROJECT/src/main/res/drawable/ic_key.xml"

echo "=== 2. 链接 ==="
VCODE_FILE="$PROJECT/version-code.txt"
VCODE=$(cat "$VCODE_FILE" 2>/dev/null || echo 0)
VCODE=$((VCODE + 1))
echo "$VCODE" > "$VCODE_FILE"
VNAME="1.0.$VCODE"
echo "=== 版本: $VNAME (versionCode $VCODE) ==="

$BT/aapt2 link -o "$OUT/unsigned.apk" -I "$PLATFORM/android.jar" \
  --manifest "$PROJECT/src/main/AndroidManifest.xml" --java "$OUT/gen" \
  --version-code "$VCODE" --version-name "$VNAME" --replace-version \
  -A "$PROJECT/src/main/assets" "$OUT/res.zip"

echo "=== 3. 编译 Java ==="
javac -cp "$PLATFORM/android.jar" -d "$OUT/classes" \
  "$OUT/gen/com/example/xpanel/R.java" "$PROJECT/MainActivity.java" "$PROJECT/AppPickerActivity.java" \
  "$PROJECT/XrayVpnService.java" "$PROJECT/Updater.java" 2>&1 | grep -v "warning:" || true

echo "=== 4. DEX ==="
$BT/d8 --lib "$PLATFORM/android.jar" --output "$OUT/dex" $(find "$OUT/classes" -name '*.class')

echo "=== 5. 打包 ==="
# 内核库以 lib/<abi>/ 形式落盘（Android 只从这个路径加载原生库）；
# jniLibs/ 那份是历史中间产物，保持原样一并塞进包，保证产物与旧版一致。
( cd "$PROJECT/src/main" && zip -qr "$OUT/unsigned.apk" jniLibs )
python3 - "$OUT/unsigned.apk" "$SO" <<'PY'
import sys, zipfile
apk, so = sys.argv[1], sys.argv[2]
with zipfile.ZipFile(apk, "a") as z:
    z.write(so, "lib/arm64-v8a/libxpanel.so")
PY
( cd "$OUT/dex" && zip -qr ../unsigned.apk . )
$BT/zipalign -f 4 "$OUT/unsigned.apk" "$OUT/aligned.apk"

echo "=== 6. 签名 ==="
KEYSTORE=${XUI_ANDROID_KEYSTORE:-$PROJECT/debug.keystore}
[ -f "$KEYSTORE" ] || keytool -genkey -v -keystore "$KEYSTORE" -alias androiddebugkey \
  -keyalg RSA -keysize 2048 -validity 10000 -storepass android -keypass android \
  -dname "CN=Debug, OU=Dev, O=Example, L=City, S=State, C=US"
$BT/apksigner sign --ks "$KEYSTORE" --ks-pass pass:android --key-pass pass:android \
  --out "$DIST/xpanel-client.apk" "$OUT/aligned.apk"
echo "✅ $DIST/xpanel-client.apk ($(du -h "$DIST/xpanel-client.apk" | cut -f1))"
