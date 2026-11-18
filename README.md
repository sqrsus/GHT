# GHT — GitHub 访问加速（本地代理）

GHT 是一个 Windows 桌面小工具：在本机开一个 HTTP 代理，**只对白名单里的域名**换一条更快的解析与链路，其余流量不受影响。
默认白名单就是 GitHub 及其静态资源域（`github.com`、`githubusercontent.com`、`githubassets.com`、`ghcr.io`、`github.io` 等）。

它的目标很窄：解决「域名被解析到烂 IP / DNS 结果不可用」导致的访问卡顿、超时、clone 失败。

---

## 一、它不做什么

| 不做 | 说明 |
|---|---|
| 不解密 TLS | `CONNECT` 之后是纯 TCP 隧道，字节原样转发，不安装任何根证书，不需要管理员权限 |
| 不做 CDN 网段优选 | 不拉取 Cloudflare / CloudFront 网段做测速，避免与 DNS 加速链路重复 |
| 不做逐请求选路 | 同一条 HTTPS 连接内的所有请求共享一个上游 IP（隧道端到端，看不到内层请求） |
| 不做流量中转 | 没有任何远程服务器，全部逻辑都在本机完成 |
| 不提供加密或规避能力 | 只做解析与链路选择，请遵守你所在地区的法律法规与目标站点的使用条款 |

---

## 二、工作原理

```
浏览器/命令行
   │  HTTP 请求（系统代理指向 127.0.0.1:7890）
   ▼
┌──────────────────┐   命中白名单？
│  ProxyServer     │──────────────否──────────────► 系统 DNS 直连
│  (127.0.0.1)     │
└────────┬─────────┘
         │ 是（且端口是 80/443）
         ▼
┌──────────────────┐   用加速池里的 N 台公共 DNS 并发解析该域名
│  ResolverHub     │──────────► 拿到一批候选 IP（IPv4 优先，带 TTL 缓存）
└────────┬─────────┘
         ▼
┌──────────────────┐   同时向多个 IP 发起 TCP connect，第一个成功的胜出
│  UpstreamRacer   │──────────► 胜出 IP 建立隧道（TLS 端到端）
└────────┬─────────┘
         ▼
   数据双向转发（60s 空闲超时，半关闭语义保留）
```

三条关键设计：

1. **首连预算（默认 1500ms）只包住「选路」**，不包住数据转发。预算到点就先用系统 DNS 连上，DNS 扫描在后台继续，下一个请求即可命中新结果——不会出现「明明能直连却报 502」，也不会掐断长下载。
2. **扫描任务不接调用方的取消令牌**。调用方放弃等待 ≠ 扫描被取消。
3. **候选全部试完才报 502**。只有「扫描完成且所有候选 IP 都连不上」才算上游不可用。

---

## 三、快速开始

### 直接使用

1. 到 [Releases](https://github.com/sqrsus/GHT/releases) 下载 `GHT-*-win-x64.zip` 解压，或使用 CI 产物里的 `dist/GHT.exe`。
2. 双击运行（框架依赖版需要机器上有 [.NET 8 桌面运行时](https://dotnet.microsoft.com/download/dotnet/8.0)）。
3. 点「扫描并应用」，等扫描完成后确认，再点「启动加速」。
   - 启动后会自动把系统代理（当前用户）指向 `127.0.0.1:7890`。
   - 点「停止加速」或退出程序会自动恢复（只关闭指向本机回环的代理，不动你自己配置的代理）。

### 从源码构建

```powershell
dotnet restore GHT.sln
dotnet build   GHT.sln -c Release

# 单文件 exe（框架依赖，需 .NET 8 桌面运行时）
dotnet publish src/GHT/GHT.csproj -c Release -r win-x64 --self-contained false `
  -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true `
  -p:EnableCompressionInSingleFile=true -o dist
```

> `PublishTrimmed` 必须保持关闭：WPF 不支持裁剪。

---

## 四、配置项

配置保存在 `%LOCALAPPDATA%\GHT\settings.json`，改完保存即生效，无需重启程序。
若本机存在旧版 `%LOCALAPPDATA%\CloudflareSmartProxy\settings.json`，首次启动会自动复制一份过来（不覆盖已有文件）。

| 字段 | 默认值 | 说明 |
|---|---|---|
| `ProxyPort` | `7890` | 本地监听端口，仅绑定 `127.0.0.1` |
| `GlobalDnsWhitelist` | GitHub 相关 16 条 | 逗号或换行分隔，支持 `*` 通配；**可清空**，清空即全部走系统 DNS |
| `DnsAccelEnabled` | `true` | 总开关 |
| `ConnectBudgetMs` | `1500` | 首连预算；`0` = 不限（等价旧行为，作为回滚开关） |
| `RaceMaxIps` | `8` | 单批并发竞速的 IP 数 |
| `RaceConnectTimeoutMs` | `5000` | 单个 IP 建连超时 |
| `RaceBatchBudgetMs` | `6000` | 单批竞速总预算 |
| `RaceRetryRounds` | `2` | 全批失败后的重赛轮数 |
| `RaceRetryDelayMs` | `800` | 重赛前等待（给后台扫描补候选的机会） |
| `DnsScanConcurrency` | `128` | 扫描公共 DNS 的并发数 |
| `DnsScanTimeoutMs` | `2000` | 单次 DNS 查询超时 |
| `DnsCacheMinTtlSec` / `DnsCacheMaxTtlSec` | `60` / `600` | 解析结果缓存 TTL 区间 |
| `FirstBatchCollectMs` | `200` | 首批结果聚合窗口，`0` = 不聚合 |
| `FirstBatchMinIps` | `5` | 首批凑够几个 IP 就提前返回 |
| `PoolResolveConcurrency` | `24` | 单域名每轮并发使用的 DNS 数量 |
| `PreferIpv4` | `true` | 候选排序优先 IPv4 |
| `CloseAction` | `Ask` | 关闭窗口行为：`Ask` / `Tray` / `Exit` |
| `AutoStart` | `false` | 开机自启（写 HKCU `Run` 键，无需管理员） |
| `DnsPool` | `[]` | 已应用的公共 DNS 列表（`Ip` / `Name` / `LatencyMs`） |
| `DnsPoolDomain` | `github.com` | 加速池探测目标域名 |
| `DnsPoolUpdatedAt` | `null` | 加速池更新时间 |
| `DnsLastScanDomain` | `""` | 最后一次扫描的域名 |

旧配置里的 `SpeedTestUrl`、`PreferredEdges`、`MitmEnabled` 等废弃字段会被静默忽略，不会报错。

---

## 五、项目结构

```
src/GHT/
├── App.xaml(.cs)              单实例闸门、配置装载、退出清理
├── MainWindow.xaml(.cs)       单页 UI：启停 / 白名单 / 加速池 / 参数 / 日志
├── Models/
│   ├── AppSettings.cs         配置模型（含默认白名单）
│   ├── DnsPoolEntry.cs        加速池条目
│   └── ProxyStatistics.cs     计数器快照
└── Services/
    ├── ProxyServer.cs         接入、选路编排、隧道与明文转发
    ├── ProxyProtocol.cs       HTTP 常量、逐跳头规则、错误响应
    ├── UpstreamRacer.cs       TCP 建连竞速
    ├── ResolverHub.cs         域名 → 候选 IP（预算语义、后台扫描、渐进缓存）
    ├── Http/                  请求头解析、前置缓冲流
    ├── Dns/                   DNS 报文编解码、UDP 客户端、扫描器、结果缓存、内置清单
    ├── SystemProxy.cs         系统代理注册表读写 + WinINet 刷新
    ├── SettingsStore.cs       配置读写与越界收敛
    ├── SingleInstance.cs      Mutex + 命名管道唤出
    ├── TrayIconService.cs     托盘图标与菜单
    ├── AutoStart.cs           开机自启
    ├── AppPaths.cs            %LOCALAPPDATA%\GHT 路径与旧配置迁移
    ├── DomainWildcardList.cs  通配域名匹配
    └── LogService.cs          按天分文件日志 + 内存缓冲
```

---

## 六、CI / 发布

| 工作流 | 触发 | 做了什么 |
|---|---|---|
| `build.yml` | push / PR 到 `main`、手动 | `windows-latest` + .NET 8 SDK → restore → build → publish 单文件 exe → 上传 `GHT-win-x64` artifact |
| `release.yml` | 推送 `v*` 标签 | 同上构建，打包 zip 并创建 GitHub Release |

发布新版本：

```bash
git tag v1.0.1
git push origin v1.0.1
```

---

## 七、已知边界

- **仅 Windows 10/11 x64**：WPF + 注册表 + 托盘，跨平台需重写宿主层。
- **仅 HTTP/1.1**：不支持 h2，浏览器会对代理自动降级。
- **明文 HTTP 采用「一次连接一个请求」**：向上游强制 `Connection: close`，换取转发逻辑的绝对可靠；HTTPS 走隧道不受影响。
- **端口 80/443 才走加速**：其他端口一律直连。
- **强杀进程会残留系统代理**：请用「停止加速」或正常退出（程序退出时会尝试清理一次）。
- **首次启动需要先「扫描并应用」**：加速池为空时所有请求都会走系统 DNS（功能正常，但没有加速效果）。

---

## 八、许可与来源

本项目以 MIT 许可发布，详见 [LICENSE](LICENSE)。

实现思路参考了 [bebincas/IPOptimize](https://github.com/bebincas/IPOptimize)（CloudflareSmartProxy）：本仓库是**独立重写的精简实现**，只保留「DNS 加速池 + 多 IP 竞速 + 本地隧道代理」这一条链路，移除了原项目的 CDN 网段优选、MITM 证书解密、远端公告位等功能。原仓库未附带许可文件，因此本仓库不包含其源码，仅借鉴整体思路与若干工程取舍。
