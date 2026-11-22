# Windows 客户端（C# / WinForms）

面板公开下载的 `XPanel.exe` 就是这个目录的产物（mcs 编译，内嵌 xray/hysteria 内核）。
Release 资产名固定为 `XPanel.exe`，`scripts/publish_client.sh` 负责上传，`client.json` 里的
sha256 由 `scripts/sync_panel_mirror.sh` 生成，客户端据此自动更新。

## 构建

```bash
# 1) 内核文件：从 Release 的面板包或 Xray 官方包取，放到 resources/
#    xray.exe / geoip.dat / geosite.dat / hysteria.exe
bash build.sh          # 产出 ./XPanel.exe（约 28MB）
```

依赖：`mono-devel`（提供 `mcs`）与 `System.Web.Extensions.dll`（mono 4.5 profile 自带）。

## 测试

```bash
bash tests/run.sh
```

覆盖：到期文案语义（0=长期有效 / null=未获取 / 负值=未激活）、面板候选地址顺序、
`success:false`（面板通了但没这个 uuid）与网络不通的区分、HTTP 硬超时。

## 到期/流量查询（`MainForm4.PollTrafficLoop`）

- 客户端每 2 秒查 `/panel/<pub>/traffic/<uuid>`；查询失败退避到 8 秒，并在到期位显示
  `查询失败`（面板不通）或 `节点未登记`（面板通了但入站里没有这个 uuid）。
- 到期语义：`0` 长期有效；`null/缺字段` 未获取；负值 = 首连后 N 天（未激活）；
  过期按**时间点**判断（不是按整天截断），剩余不足 24 小时显示
  `(N小时)`，避免最后一天显示成「今天 (0天)」看起来像没过期。
- 候选顺序：节点地址的 `:1111/clay` → `:1111` → `/clay` → 根路径 → 明文 `:1111`
  → 内置 `PANEL_BASE`；命中的前缀写入 `HKCU\Software\XPanel\panel_base`，下次启动优先复用。
  该注册表值也可手工设置，用于中转/反代地址。
- 已连接（加速中）时查询走本地 http 入站 `127.0.0.1:10809`，避免面板端口被干扰时查不到。
- 单候选 1.2s 硬超时（`BeginGetResponse` + `WaitOne`，超时即 `Abort`），因为
  `HttpWebRequest.Timeout` 管不住 DNS/TCP 连接阶段，面板端口被丢包时会卡几十秒。

## 关闭与单实例（`MainForm.OnFormClosing` / `Program.Main`）

- 点叉的语义：**导入页**（还没登录节点、代理没在跑）真退出进程；仪表盘/设置页收进托盘，
  托盘菜单「退出」才真退。导入页收托盘等于骗用户已经关了。
- 第二个实例被单实例互斥挡住时，不能静默 `return`：先发命名事件让在跑的实例自己 `Show`，
  不行再用 `FindWindowW` + `ShowWindow(SW_RESTORE)` 把隐藏的窗口拽出来（这一步连旧版本也能唤醒）；
  两种都失败才按新实例启动 —— 宁可多开一个，也不要「点了图标一片安静」。
- `ShouldQuitOnClose(page, connected)` 是上面第一条规则的唯一实现，单测直接覆盖。

## 导入页栅格与高分屏（v10.5.1）

- 纵向栅格集中在 `MainForm.cs` 的 `P1*` / `EDIT_*` / `CHIP_*` / `BTN1_*` / `TIP_*` / `DIV1_Y` / `NOTE_Y`
  常量里，顺序是 标题 → 副标题 → 输入框 → 协议徽标 → 按钮 → 错误提示 → 页脚分隔线 → 页脚说明。
  `MainForm.CheckPage1Grid()` 把「相邻块至少 4px、页脚不压底栏」做成可单测的判定
  （`tests/ExpiryTests.cs` 直接调用）——上一版错误提示行与页脚说明就叠了 2px。
- 输入框白底由窗口自绘，`BorderedEdit` 内缩 10/8/6 作为真正的内边距；空框水印在
  `WndProc` 收到 `WM_PAINT` 后自绘（叠 Label 会吃掉点击，焦点进不了输入框）。
- `app.manifest` 声明 `dpiAware=true` + `dpiAwareness=system`，经 `app.rc` 由 windres
  打包成 `.res` 走 `mcs -win32res`。**必须用 `--output-format=res`**：coff 格式会让
  mcs 的 IKVM 反射层抛 `BadImageFormatException`（`CS0016 Could not write to file`），
  而 `-win32manifest` 被静默忽略、不会写 RT_MANIFEST。没有这段清单时，高分屏上进程先按
  96dpi 画完再被 Windows 整块拉伸，小字看着发虚、像缺了半截。
- 本地复现缩放：`XPL_DPI=144 mono XPanel.exe`（诊断开关，正式用户不需要设置）。

## 自动更新（`Updater.StartAutoLoop`）

- 启动 1.5 秒后查一次清单（`/download/pkg/client.json`），**前 3 分钟算启动期**：发现比
  `MainForm.VER` 新的版本号就直接下载、校验 sha256、写替换脚本并退出让脚本换文件重启。
- 开机头几分钟网络/面板常没就绪，检查失败按 6s → 15s → 35s → 90s 快速补查，
  不会一次失败就退到 30 分钟节奏；超过启动期只提示不打断（避免正在用时被重启）。
- 版本号护栏：清单里的版本 ≤ 本机版本一律不动，旧镜像即使 sha 不同也不会把人降级。
- 提示同时写到仪表盘那行和导入页的提示行——启动更新时用户多半还停在导入页。

## hysteria2（hy2）接入（v10.5.2）

Xray 不支持 hy2，客户端为它单开 `hysteria.exe` 内核，配置由 `Xray.BuildHy2Config` 生成。
这一版修掉的是三个"看起来能连、实际连不上"的真问题：

- **链接尾巴带斜杠**。`hy2://pw@host:443/?sni=..&insecure=1` 是常见导出格式，旧解析把
  `443/` 直接 `int.TryParse`，整条链接判成格式错误（界面只显示"链接格式错误"，看着像不支持 hy2）。
  现在 authority 会剥掉尾部 `/`，并支持 `hysteria2://`、大写 scheme、无 userinfo 用 `?auth=..`、
  IPv6 `[::1]:443`。
- **obfs 密码写在错误的层级**。hysteria2 的字段是 `obfs.salamander.password`（按类型名嵌套），
  旧代码生成 `obfs: {type: salamander, password: ..}`，内核启动即
  `FATAL invalid config: obfs.salamander.password: PSK must be at least 4 bytes` 秒退。
  现在按 `obfs.<type>.password` 生成，`salamander` 与 `gecko` 两种都验过（本地起服务端跑通）。
- **YAML 没转义**。密码里带 `:` `#` `"` 时裸写会被截断成别的值（认证一直失败、但配置看着没问题）。
  现在所有标量都走双引号转义；`sni` 缺省时回落到节点地址，不再硬编码成 `bing.com`。

连接状态也不再撒谎：`Xray.WaitReady` 等内核真的把 10809 监听起来才切系统代理（hy2 是先握手成功
才监听，所以这一步等于验证了密码/obfs/TLS），内核秒退或超时就撤销系统代理、状态显示"连接失败"，
失败原因用中文写到仪表盘底部那行（状态位只有 116px，塞不下完整原因）。内核原话仍记在日志里。

联调开关：`XPL_KERNEL_DIR=<目录>` 让客户端用该目录下的内核（不再释放内嵌资源），
Linux 上可以用同名 `hysteria.exe` 包装脚本指向 Linux 版 hysteria 跑界面联调。

## 自动更新可观测 + TLS 兜底（v10.5.3）

自动更新失败原本完全静默（只写日志），用户看到的现象是「打开软件什么都没发生」，和「已是最新」
无法区分。这一版把启动期结论显示到导入页提示行：失败保留原因，已最新 6 秒后清掉。

同一版修掉三个会让「自动更新/面板请求整体失效」的环境问题：

- **TLS 版本一次性赋值会全丢**。旧代码 `SecurityProtocol = Tls12 | (SecurityProtocolType)3072`：
  .NET 4.7 以下没有 TLS1.3 枚举值，整条赋值抛 `NotSupportedException` 被 catch 吞掉，
  结果 TLS1.2 也没启用（退回 TLS1.0），跟面板（Go 默认最低 TLS1.2）握手失败。
  现在分两条 `|=` 单独设，哪条不支持就只丢那一条。
- **替换脚本的 PID 判定是子串匹配**。`findstr /C:"<pid>"` 会把 `1234` 与 `11234`、`12340`
  以及内存列 `"1,234 K"` 一起算命中，进程退出后脚本还在等，表现为「下载完了但没换包」。
  改成 `tasklist /FO CSV` 第二列精确比对。
- **`timeout /t 1` 在无控制台时直接报错不等待**，循环空转；延时改用 `ping -n 2 127.0.0.1`。

## 字体系统重构（v10.5.4）

旧实现有三处叠加错误，症状都是「高分屏上字小、发虚、被切一半」：

- **双重缩放**。布局坐标全部走 `S()` 手动乘 `DpiScale`，而 Form 保留 WinForms 默认的
  `AutoScaleMode.Font` —— 125% 缩放下控件被再缩放一次（1.25²），字体只按 DPI 放大 1.25 倍，
  于是「框大一圈、字没跟上」。现在构造里显式 `AutoScaleMode = None`，布局只有一个缩放源头。
- **字号单位混用**。原来用 pt（`new Font(Y, 10.5f)`），pt 会按 `Graphics.DpiY` 再乘一次，
  与 `S()` 的缩放语义重叠；而且 125% 下 14px→17.5px 这样的半像素字号会被 GDI 亚像素缩放，
  笔画发虚。现在统一到 `Fonts`（`Fonts.cs`）：字号是 **96dpi 逻辑像素**，显式
  `GraphicsUnit.Pixel`，乘 `DpiScale` 后取整，与布局常量同一坐标系。
- **字体族没有回退**。写死 `"Microsoft YaHei UI"`，Win7 或精简系统没有这个族时 GDI 逐字回退，
  中英混排字重/基线/行高全乱。现在 `Fonts` 按
  `Microsoft YaHei UI → Microsoft YaHei → Segoe UI → Microsoft Sans Serif → WenQuanYi Zen Hei → SimSun`
  探测系统里真实存在的族；等宽同理（Consolas → Cascadia Mono → Courier New）。

配套的栅格校正（都由 `MainForm.CheckFontLayout()` 守住，`tests/FontTests.cs` 调用）：

- 大数字 36px→30px、槽位 40→42（30×1.35=41，最差行高也装得下）；协议 chips 高 14→16、
  未选中色 `0xAEB4BF`→`0x8A9099`（原色在浅底上过淡）。
- chips 文字补 `VerticalCenter`：`NoPadding` 会把文字贴到 rect 顶部。
- 底部两行（系统代理说明、速率/版本）从 `Transparent` 改为不透明 `BASE` 底色：透明 Label
  依赖父窗口背景回填，非整数缩放下回填区常错位，mono 下直接露黑底，Windows 上是闪烁/残影。
- 判定规则不依赖本机字体：行高按「字号 × 1.35」估算（雅黑实测 1.32、宋体 1.18，取最差值）。

自检/回归：`bash tests/run.sh`（`FontTests` 覆盖族解析、行高-槽位、DPI 等比放大），
`scripts/winforms-shot.sh` 分别跑 96/120/144/192 截图看比例是否一致。
