# 安卓客户端（Java / VpnService）

面板公开下载的 `xpanel-client.apk` 就是这个目录的产物：包名 `com.example.xpanel`，
minSdk 21 / targetSdk 34，不依赖 gradle（aapt2 + javac + d8 + apksigner 手工构建）。

功能：粘贴节点链接（vless / vmess / trojan / ss / hy2）→ 本机 VpnService 拉起内核转发 →
按应用分流（`AppPickerActivity`），面板公开接口查流量/到期，启动时按 `client.json` 哈希自动更新。

## 目录

| 文件 | 说明 |
| --- | --- |
| `MainActivity.java` | 主界面、链接解析、面板查询、配置生成 |
| `XrayVpnService.java` | VpnService + 内核启停、TUN 转发 |
| `AppPickerActivity.java` | 按应用分流选择器 |
| `Updater.java` | 读 `/download/pkg/client.json`，哈希不一致时下载并安装新 APK |
| `src/main/AndroidManifest.xml` | 权限与组件声明 |
| `src/main/res/` | 布局与 drawable |
| `build-manual.sh` | 构建脚本（产物 `dist/xpanel-client.apk`） |

## 构建

需要 JDK 17、Android SDK（build-tools 34.0.0 + platforms/android-34）、`zip`、`python3`。

```bash
# 两个大文件不入库，需自行放置（见下）
bash build-manual.sh      # -> dist/xpanel-client.apk，versionCode 每次 +1
```

`build-manual.sh` 支持的环境变量：`ANDROID_SDK`（默认 `/opt/android-sdk`）、
`XUI_ANDROID_OUT`（产物目录）、`XUI_ANDROID_KEYSTORE`（签名 keystore）。

### 缺失的大文件（不入库，需自备）

| 路径 | 内容 | 来源 |
| --- | --- | --- |
| `src/main/jniLibs/arm64-v8a/libxpanel.so` | Xray 内核（Go，aarch64，约 31MB） | 本仓库 Release 面板包内的 Android 内核，或从已发布 APK 的 `lib/arm64-v8a/` 提取 |
| `src/main/jniLibs/arm64-v8a/libsingbox.so` | sing-box 内核（约 29MB） | sing-box 官方 Android 构建 |
| `src/main/jniLibs/arm64-v8a/libtun2socks.so` | tun2socks（约 11MB） | 上游 badvpn/hev-socks5-tunnel 构建 |
| `src/main/assets/sing-box` | 备用 sing-box 可执行文件（约 29MB） | sing-box 官方 release 的 `sing-box-android-arm64` |

三者都放进 `jniLibs/arm64-v8a/` 后重新构建即可；缺 `libxpanel.so` 时脚本会直接报错退出。

## 签名

默认用 `debug.keystore`（文件不存在时脚本自动生成，口令 `android`）。
正式分发请换成自己的 keystore 并**长期保持同一签名**，否则已装用户无法覆盖安装。

## 版本号

`version-code.txt` 每次构建自增，同时写入 APK 的 versionCode / versionName（`1.0.<code>`）。
入库值跟随当前线上 APK 的 versionCode。
