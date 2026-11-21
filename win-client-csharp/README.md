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
