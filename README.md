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

四条关键设计：

1. **首连预算（默认 1500ms）只包住「选路」**，不包住数据转发。预算到点就先用系统 DNS 连上，DNS 扫描在后台继续，下一个请求即可命中新结果——不会出现「明明能直连却报 502」，也不会掐断长下载。
2. **首批候选是真正的时间窗口**：任一 DNS 查询一返回就合并结果并尝试发布，另有独立计时器到点强制发布；首批只问延迟低、近期成功率高的前若干台，其余交给后台 worker 补齐。不会因为「这一批里有一台 DNS 慢」就把整个窗口拖到单次 DNS 超时。
3. **扫描任务不接调用方的取消令牌**。调用方放弃等待 ≠ 扫描被取消。
4. **回落按原因分项计数**：池为空 / 预算到期 / 无解析答案 / 候选全失败，四种情况的修法完全不同。候选全部建连失败时默认再用系统 DNS 兜底（`FallbackOnCandidatesFailed`，可关）；严格语义下才回 502。

### 转发层的性能取舍

| 位置 | 做法 | 为什么 |
|---|---|---|
| 日志 | 有界队列 + 单后台任务批量落盘 | 旧实现把 `File.AppendAllText` 放在调用线程的锁里，杀软/磁盘抖动会拖住所有代理线程 |
| 转发缓冲 | `ArrayPool<byte>` 复用 32KB | 每条连接、每次响应都新分配会持续制造 LOH 压力 |
| 空闲超时 | 可重置的 `CancelAfter` | 旧实现每次 `Read` 都新建一个 linked CTS，大文件传输时每秒上万次分配 |
| chunked 尺寸行 | 8KB 内部缓冲 + 内存扫描 | 旧实现 `byte[1]` 逐字节 `ReadAsync`，每读一个字符进一次内核 |
| DNS 缓存 | 不可变数组快照 + 逐地址 TTL + 条目级锁 | 旧实现每次读取都进全局锁并 `ToArray()` 复制，且整条缓存共用一个「取最大值」的过期时刻，短 TTL 的地址会跟着长 TTL 一起滞留 |
| 首批候选 | 逐查询完成即发布 + 独立截止计时器 | 旧实现等整批 `Task.WhenAll` 才检查窗口，一台慢 DNS 就能把 200ms 窗口拖到 2s |
| 竞速候选 | 失败退避 + 建连耗时 EWMA 排序 | 竞速本来就知道谁慢谁坏，顺手记下来，下次先试好的 |
| 竞速套接字 | 只对胜出连接调大收发缓冲 | 旧实现给每个候选都设 256KB，竞速 8 个候选等于把内核缓冲成本乘以 8，而落败者几十毫秒就被释放 |
| 扫描进度 | 120ms 时间节流 | 1200 台服务器逐个 `Report` 会把 UI 线程刷爆 |
| 统计 UI | 窗口隐藏时停表 | 托盘运行期间不需要每秒拼字符串 |

隧道转发保留**半关闭**语义：一侧正常 EOF 时只 `Shutdown(Send)`，另一方向继续；**空闲超时不发 FIN**，否则「长时间只收不发」的大文件下载会被提前掐断。

---

## 三、快速开始

### 直接使用

1. 到 [Releases](https://github.com/sqrsus/GHT/releases) 下载 `GHT-*-win-x64.zip` 解压，或使用 CI 产物里的 `dist/GHT.exe`。
2. 双击运行（框架依赖版需要机器上有 [.NET 8 桌面运行时](https://dotnet.microsoft.com/download/dotnet/8.0)）。
3. 点「扫描」，结果会先以候选形式列在右侧表格里（带延迟），确认后点「应用结果」写入加速池，再点「启动加速」。
   - 扫描过程中可随时点「取消」，不影响已应用的加速池。
   - 启动后会自动把系统代理（当前用户）指向 `127.0.0.1:7890`，并把**你原本的代理设置**快照到 `proxy-backup.json`。
   - 点「停止加速」或退出程序时按快照还原（没有快照时只关闭指向本机回环的代理，不动你自己配置的代理）。
   - 若进程被强杀，下次启动会自动检测残留快照并还原，不会让全机一直指向已关闭的端口。
4. 想确认「加速到底有没有生效」，用「域名诊断」：输入域名与端口，它会用加速池解析 A/AAAA、对候选 IP 做一次 TCP 竞速，并给出结论与逐台 DNS 明细。

### 从源码构建

```powershell
dotnet restore GHT.sln
dotnet build   GHT.sln -c Release
dotnet test    tests/GHT.Tests/GHT.Tests.csproj -c Release --no-build

# 单文件 exe（框架依赖，需 .NET 8 桌面运行时）
dotnet publish src/GHT/GHT.csproj -c Release -r win-x64 --self-contained false `
  -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true `
  -p:DebugType=None -p:DebugSymbols=false -o dist
```

> `PublishTrimmed` 必须保持关闭：WPF 不支持裁剪。
> 框架依赖（`--self-contained false`）时也不能开 `EnableCompressionInSingleFile`，SDK 会报 NETSDK1176。

测试工程 `tests/GHT.Tests` 只编译「不依赖 Windows API 的纯逻辑源码」（HTTP 解析、chunked 帧、DNS 报文、白名单、配置归一化），因此可以在 Linux/WSL 上直接 `dotnet test`，不必等到 Windows runner。

---

## 四、配置项

配置保存在 `%LOCALAPPDATA%\GHT\settings.json`，改完保存即生效，无需重启程序。
保存采用「唯一临时文件 → 留一份 `settings.json.bak` → 原子替换」，并加了保存锁；读取失败时会自动回退到 `.bak`。
若本机存在旧版 `%LOCALAPPDATA%\CloudflareSmartProxy\settings.json`，首次启动会自动复制一份过来（不覆盖已有文件）。

| 字段 | 默认值 | 说明 |
|---|---|---|
| `ProxyPort` | `7890` | 本地监听端口，仅绑定 `127.0.0.1` |
| `GlobalDnsWhitelist` | GitHub 相关 16 条 | 逗号或换行分隔，支持 `*` 通配；**可清空**，清空即全部走系统 DNS |
| `DnsAccelEnabled` | `true` | 总开关 |
| `ConnectBudgetMs` | `3000` | 首连预算；`0` = 不限（等价旧行为，作为回滚开关）。应略大于单个地址的失败耗时，否则竞速会被掐断而给不出结论 |
| `FallbackOnCandidatesFailed` | `true` | 加速池候选全部连不上时，再用系统 DNS 兜底；`false` = 直接 502（严格语义） |
| `RaceMaxIps` | `8` | 单批并发竞速的 IP 数 |
| `RaceConnectTimeoutMs` | `5000` | 单个 IP 建连超时 |
| `RaceBatchBudgetMs` | `3000` | 单批竞速总预算（应小于 `ConnectBudgetMs`，否则永远不会生效） |
| `RaceRetryRounds` | `2` | 全批失败后的重赛轮数 |
| `RaceRetryDelayMs` | `800` | 重赛前等待（给后台扫描补候选的机会） |
| `DnsScanConcurrency` | `128` | 扫描公共 DNS 的并发数 |
| `DnsScanTimeoutMs` | `2000` | 单次 DNS 查询超时 |
| `DnsCacheMinTtlSec` / `DnsCacheMaxTtlSec` | `60` / `600` | 解析结果缓存 TTL 区间（逐地址计时） |
| `FirstBatchCollectMs` | `600` | 首批窗口：到点无论凑够几个都先返回（`0` = 拿到第一个答案就返回）。过小会让首批只拿到国内 DNS 的趋同答案，候选退化成单点 |
| `FirstBatchMinIps` | `5` | 首批凑够几个 IP 就提前返回 |
| `PoolResolveConcurrency` | `24` | 后台补齐时并发使用的 DNS 数量 |
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
├── MainWindow.xaml(.cs)       单页 UI：启停 / 白名单 / 加速池 / 诊断 / 参数 / 日志
├── ViewModels/
│   └── DnsPoolRow.cs          加速池表格行（延迟 + 运行期健康度）
├── Models/
│   ├── AppSettings.cs         配置模型（含默认白名单）
│   ├── DnsPoolEntry.cs        加速池条目
│   └── ProxyStatistics.cs     计数器快照（含字节数、活跃隧道、首连 P50/P95）
└── Services/
    ├── ProxyServer.cs         接入、选路编排、隧道与明文转发
    ├── ProxyProtocol.cs       HTTP 常量、逐跳头规则、错误响应
    ├── UpstreamRacer.cs       TCP 建连竞速 + 地址健康度（跨进程持久化）
    ├── ResolverHub.cs         域名 → 候选 IP（预算语义、后台扫描、渐进缓存、DNS 健康度）
    ├── Diagnostics/           域名诊断（复用 DNS 扫描与竞速，产出可复制报告）
    ├── Http/                  请求头解析、前置缓冲流、带缓冲的行读取流
    ├── Dns/                   DNS 报文编解码、UDP 客户端、扫描器、结果缓存、地址合法性判定、内置清单
    ├── SystemProxy.cs         系统代理注册表读写 + WinINet 刷新
    ├── SettingsStore.cs       配置读写与越界收敛
    ├── SingleInstance.cs      Mutex + 命名管道唤出
    ├── TrayIconService.cs     托盘图标、菜单与气泡
    ├── AutoStart.cs           开机自启
    ├── AppPaths.cs            %LOCALAPPDATA%\GHT 路径与旧配置迁移
    ├── DomainWildcardList.cs  通配域名匹配
    └── LogService.cs          异步批量落盘 + 按天分文件 + 大小轮转 + 内存缓冲

tests/GHT.Tests/               纯逻辑单元测试（链接源码，可跨平台运行）
└── HttpHead / ChunkedBody / DnsMessage / DnsResultCache / AddressPolicy / ResolverHub
    / UpstreamRacer / ProxyStatistics / LogService / DomainWildcardList / Settings
```

---

## 六、CI / 发布

| 工作流 | 触发 | 做了什么 |
|---|---|---|
| `build.yml` | push / PR 到 `main`、手动 | `windows-latest` + .NET 8 SDK → restore → build → **dotnet test（133 例）** → publish 单文件 exe → 上传 `GHT-win-x64` artifact |
| `release.yml` | 推送 `v*` 标签 | 同上构建，打包 zip 并创建 GitHub Release |

发布新版本：

```bash
git tag v1.3.1
git push origin v1.3.1
```

---

## 七、候选质量与地址健康度

「首连预算用尽」这条日志的含义是：**预算内一个候选都没连上**。它本身是降级动作，不是故障原因；病因通常在候选质量上。因此有三道处理：

**1. 不可路由地址在入池前就丢弃**（`Services/Dns/AddressPolicy.cs`）

投毒/被劫持的公共 DNS 会返回 `127.0.0.1`、`0.0.0.0`、`198.51.44.8`（TEST-NET-2）这类地址。它们「答得很积极」，于是永远排在首批，而答案永远连不上 —— 一批 8 个竞速名额就这样被白白占掉。过滤范围只限明确不可路由的段（回环、未指定、私有、CGNAT、链路本地、文档段、组播、保留段；IPv6 只收 `2000::/3`），**不**做「看着像内网就丢」的猜测：`20.x.x.x`、`185.199.x.x` 是 GitHub 自己的入口，误杀会直接断掉加速。

**2. 「答错」的责任归到那台 DNS 上**

`ResolverHub` 记录「候选 IP → 首个给出它的 DNS」。竞速判定某地址连不上时，通过 `ReportBadAnswer` 给该 DNS 记一次失败（独立的 `Bad` 计数），排序时按**双倍权重**参与降级判定。效果是「答得出且答得对」的 DNS 会逐渐顶替「答得出但答错」的。只降权、不剔除：池里若全是劣质 DNS，直接剔除会让人彻底连不上。

**3. 地址健康度跨进程存活**（`%LOCALAPPDATA%\GHT\address-health.json`）

失败次数、建连耗时 EWMA、最后失败时间（Unix 毫秒）在停止时落盘、启动时加载。否则每次重启都要用首连预算重新试错一遍 —— 日志里「首连预算用尽」大量集中在启动后几分钟，正是这个原因。

另外两个与预算直接相关的修正：

- **预算取消不再记为地址失败**。旧实现里竞速胜出后 `scope.Cancel()` 会让其余候选全部走进 `catch` 并各记一次失败，好地址被反复记失败，健康度被系统性污染。现在取消与真实失败在 `catch (OperationCanceledException) when (token.IsCancellationRequested)` 上分流。
- **预算用尽时打印失败明细**：候选数、失败个数、耗时区间与前几个 `IP(xxms)`。这三个数字能把「上游不可达（2000ms+）」「投毒地址（几十毫秒 ConnectionRefused）」「预算确实太小（失败列表为空）」三种情况分开。

---

## 八、已知边界

- **仅 Windows 10/11 x64**：WPF + 注册表 + 托盘，跨平台需重写宿主层。
- **仅 HTTP/1.1**：不支持 h2，浏览器会对代理自动降级。
- **明文 HTTP 采用「一次连接一个请求」**：向上游强制 `Connection: close`，换取转发逻辑的绝对可靠；HTTPS 走隧道不受影响。
- **明文 HTTP 的帧是严格校验的**：只接受 chunked 与定长两种请求体；畸形头、折行头、重复且冲突的 `Content-Length`、非 chunked 结尾的 `Transfer-Encoding`、缺 CRLF 的分块一律回 400，不做「猜着转发」。
- **端口 80/443 才走加速**：其他端口一律直连。
- **候选全部连不上时默认再走一次系统 DNS**：系统 DNS 常常能解析出池里没有的另一组地址，直接 502 对用户没有好处。想恢复严格语义就把 `FallbackOnCandidatesFailed` 设为 `false`。两种路径在状态栏的回落分项里是分开计数的。
- **状态栏的回落分项要会看**：`预算` 高说明 DNS 响应慢于预期（可调大 `ConnectBudgetMs` 或换池）；`无答案` 高说明这批 DNS 对该域名的解析不一致；`候选耗尽` 高说明解析出来的 IP 本身连不通；后面的「系统 DNS 成功 N/M」决定该不该继续依赖回落。
- **强杀进程后系统代理会残留，但下次启动会自动修复**：接管前会把用户原有的 `ProxyEnable` / `ProxyServer` / `ProxyOverride` 快照到 `%LOCALAPPDATA%\GHT\proxy-backup.json`；正常退出按快照还原，下次启动若发现遗留快照且代理仍指向自己则自动还原。若之后不再启动本程序，需要手工关闭系统代理。
- **首次启动需要先「扫描 + 应用结果」**：加速池为空时所有请求都会走系统 DNS（功能正常，但没有加速效果）；扫描结果不会自动写入配置。
- **DNS 同时查询 A 与 AAAA**：纯 IPv4 环境下 AAAA 查询会空跑（两次往返并发发出，超时与 A 相同，不额外拖慢首连）。
- **日志默认不落 DEBUG**：DEBUG 只进内存（UI 可见）；排查时设 `GHT_LOG_DEBUG=1` 再启动。日志按 4MB 分卷、保留 14 天，写入是异步的（进程被强杀时最多丢队列里最后几批）。
- **诊断结果是快照**：它反映点击那一刻的解析与建连情况，不代表运行期代理的逐请求行为。

---

## 九、许可与来源

本项目以 MIT 许可发布，详见 [LICENSE](LICENSE)。

实现思路参考了 [bebincas/IPOptimize](https://github.com/bebincas/IPOptimize)（CloudflareSmartProxy）：本仓库是**独立重写的精简实现**，只保留「DNS 加速池 + 多 IP 竞速 + 本地隧道代理」这一条链路，移除了原项目的 CDN 网段优选、MITM 证书解密、远端公告位等功能。原仓库未附带许可文件，因此本仓库不包含其源码，仅借鉴整体思路与若干工程取舍。
