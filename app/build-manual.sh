#!/bin/bash
# ponytail: 显式 JDK 17 + build-tools 34，避免 systemd 缺 JAVA_HOME 用到 JDK 21 (D8 不认 class file v65)
export JAVA_HOME=/usr/lib/jvm/java-17-openjdk-amd64
export PATH=$JAVA_HOME/bin:$PATH
set -e
SDK=/opt/android-sdk
BT=$SDK/build-tools/34.0.0
PLATFORM=$SDK/platforms/android-34
PROJECT=/root/xpanel-app
OUT=$PROJECT/build
rm -rf $OUT; mkdir -p $OUT/classes $OUT/dex $OUT/gen

echo "=== 1. 编译资源 ==="
$BT/aapt2 compile -o $OUT/res.zip \
  $PROJECT/src/main/res/layout/activity_main.xml \
  $PROJECT/src/main/res/layout/activity_app_picker.xml \
  $PROJECT/src/main/res/layout/app_picker_item.xml \
  $PROJECT/src/main/res/values/styles.xml \
  $PROJECT/src/main/res/drawable/ic_launcher.xml \
  $PROJECT/src/main/res/drawable/card_bg.xml \
  $PROJECT/src/main/res/drawable/input_bg.xml \
  $PROJECT/src/main/res/drawable/btn_primary.xml \
  $PROJECT/src/main/res/drawable/btn_power.xml \
  $PROJECT/src/main/res/drawable/btn_power_on.xml \
  $PROJECT/src/main/res/drawable/ic_key.xml

echo "=== 2. 链接 ==="
$BT/aapt2 link -o $OUT/unsigned.apk -I $PLATFORM/android.jar \
  --manifest $PROJECT/src/main/AndroidManifest.xml --java $OUT/gen \
  -A $PROJECT/src/main/assets $OUT/res.zip

echo "=== 3. 编译 Java ==="
javac -cp $PLATFORM/android.jar -d $OUT/classes \
  $OUT/gen/com/example/xpanel/R.java $PROJECT/MainActivity.java $PROJECT/AppPickerActivity.java $PROJECT/XrayVpnService.java 2>&1 | grep -v "warning:" || true

echo "=== 4. DEX ==="
$BT/d8 --lib $PLATFORM/android.jar --output $OUT/dex $(find $OUT/classes -name '*.class')

echo "=== 5. 打包 ==="
cd $PROJECT/src/main && zip -r $OUT/unsigned.apk jniLibs/* && cd -
cd $OUT/unsigned.apk_lib_patch 2>/dev/null || true
# 重命名 jniLibs 为 lib
python3 -c '
import zipfile, os
with zipfile.ZipFile("/root/xpanel-app/build/unsigned.apk", "a") as z:
    z.write("/root/xpanel-app/src/main/jniLibs/arm64-v8a/libxpanel.so", "lib/arm64-v8a/libxpanel.so")
'

# old打包

cd $OUT/dex && zip -r ../unsigned.apk . && cd -
$BT/zipalign -f 4 $OUT/unsigned.apk $OUT/aligned.apk

echo "=== 6. 签名 ==="
KEYSTORE=$PROJECT/debug.keystore
[ -f $KEYSTORE ] || keytool -genkey -v -keystore $KEYSTORE -alias androiddebugkey \
  -keyalg RSA -keysize 2048 -validity 10000 -storepass android -keypass android \
  -dname "CN=Debug, OU=Dev, O=Example, L=City, S=State, C=US"
$BT/apksigner sign --ks $KEYSTORE --ks-pass pass:android --key-pass pass:android \
  --out $OUT/app-debug.apk $OUT/aligned.apk
cp $OUT/app-debug.apk /root/xpanel-client.apk
echo "✅ /root/xpanel-client.apk ($(du -h /root/xpanel-client.apk | cut -f1))"