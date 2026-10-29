# 源码与本地环境调查（2026-10-28）

## 已核实版本及边界

- CPAMP v1.14.9：`6d962db680085827cd19a778d2ff05a3d1315672`。独立参考源码在 `research/CPAMP`，下载自官方 GitHub tag archive。
- 已安装 BrimDeck 文件版本 1.2.1，产品提交 `01e8bb7077245cc933b032c635409b283597faf2`。`research/BrimDeck` 对应此提交。
- 真实数据目录 `D:\CPA\cpa-manager-plus_v1.14.9_windows_amd64\data`，数据库 `usage.sqlite`，存在活动 `usage.sqlite-wal` 和 `usage.sqlite-shm`。
- CPAMP 进程实际监听 18317；本机 API 地址 `http://127.0.0.1:18317`。原始 config.json 的 httpAddr 为 0.0.0.0:18317，本项目不修改它。
- 已通过源码确认并实测只读 Dashboard、auth-files、model-prices；不调用 usage queue、run inspection、refresh OAuth、账号修改等操作。

## 统计与费用依据

源码均相对 `research/CPAMP`：

- `apps/manager-server/internal/http/controller/dashboard/handler.go`：`GET /v0/management/dashboard/summary?today_start_ms=...&now_ms=...`，Bearer 面板密钥。响应 `today.total_calls/success_calls/failure_calls/total_tokens/total_cost`。
- `apps/manager-server/internal/http/controller/monitoring/handler.go`：`POST /v0/management/monitoring/analytics` 是只读查询，参数 `from_ms/to_ms/now_ms/time_zone`、`filters.include_failed=true`、`include.summary=true`。不是所有 POST 都是写操作。
- `internal/service/dashboard/service.go` 的 `Summary/loadTodayMetrics/buildTodaySummary/costForStat`：官方聚合与定价；`internal/service/monitoring/service.go` 的 `Analytics`：同一统计引擎的任意范围。
- `internal/service/pricing/cost.go` 的 `CostForModelCandidatesWithServiceTier`：billing/model 候选、PricingModel、服务等级、长上下文和缓存桶。费用由官方引擎计算，Bridge 用 decimal 读取金额，不重新配置单价，不另加 reasoning/cache token。
- `apps/web/src/features/monitoring/model/range.ts:getRangeBounds`：7d 是今天午夜往前 6×24h。`features/usage-analytics/usageAnalyticsModel.ts`：7d 是滚动 168h。默认使用请求监控的 7 个自然日，另提供 168h。自然日模式按 Windows 时区逐日取午夜，跨 DST 不机械减 144 小时；CPAMP 原监控前端在 DST 时存在固定毫秒差异，应对比明确起止时间。
- `usage_events` 以 `event_hash` 唯一，`timestamp_ms` 为 UTC 毫秒，`failed` 是 CPAMP 的二分类，不自行把 request_id 去重（一次请求重试可能产生多个官方记录）。取消/超时沿用官方 failed 语义。
- CPAMP 统计 API 不提供可靠的费用完整性承诺；返回 0 可能表示未定价。因此桥接保守标为“已知费用 / CPAMP 估算，完整性未确认”，不宣称账单或完整总费用。官方金额数值仍可对账。

## 额度与身份依据

- `GET /v0/management/auth-files`：CPAMP 现有管理代理，读取账号元信息，不读取具体凭证文件。
- `internal/http/controller/quotasnapshot/handler.go`：`POST /v0/management/quota-snapshots/query` 是只读；禁止调用无 `/query` 的写入端点。
- `internal/service/quotasnapshot/service.go:Query`：官方窗口生命周期、快照合并、stale、field_sources、availability。目标由 `apps/web/src/features/accounts/model/accountHistoryRows.ts` 和 `accountQuotaSnapshots.ts` 的字段构造规则提供。
- `internal/usageidentity/identity.go:AccountKey/LegacyAccountKey`：Codex 优先 workspace + 经验证的 member email；缺少强证据时退回 file/index。Bridge 对官方 account_key 加独立 SHA256 安全标识，不暴露邮箱/文件名编码；不凭显示名称或 auth_index 自动迁移选择。身份变化需重新选择，避免接错账号。
- 已发现 `account_quota_windows` 9 个逻辑窗口、`account_quota_snapshots` 2584 个历史快照（调查时刻），Codex five_hour/weekly/monthly 和 Antigravity 快照；`codex_inspection_results` 为空，不能依赖巡检记录冒充可用数据。
- SQLite schema/index 元数据保存在 `docs/schema-cpamp-v1.14.9.json`，不含数据库行值或密钥。仅用只读连接、query_only 和 busy timeout；费用优先官方 API；不使用 immutable、不复制主文件冒充最新数据。

## BrimDeck 兼容依据

源码相对 `research/BrimDeck`：

- `src/BrimDeck.Core/ScriptProvider.cs:ScriptSession.RunAsync/SendAsync`：Jint，执行/Promise 10 秒、32MB、2M statements；16 请求、4 并发，单请求 100～10000ms，默认 8000ms；脚本 128000 字符；普通响应/返回 1MB。`ctx.http.get` 响应的 `json()` 同步返回对象，await 也兼容。
- `UsageMetric.cs:MetricParser.ParseText/ParseTextItem`：必须非空数组，title 必须非空字符串，percent 必须数字 0～100，最多 100 行，文字字段必须字符串且截断 500 字符；每页 2 行。
- `MainWindow.Music.cs:QuotaRings`：每个应用一个环，取所有指标最大已使用比例；没有 percent 但存在文本行时原生环为满环。不能用假百分比改变该行为。
- `Settings.cs:AppEntry/AppPresets`：最多 7 列，没有针对单个 Custom 来源隐藏折叠环的字段；全局 summary 显示开关影响整组环。统计来源的成功率环属于请求统计，不能解释成账号额度。无账号选择时用文字提示，不填假百分比。
- `ScriptProvider.cs:ScriptProviders.FetchAsync`：失败保留最近成功快照并标 stale；脚本应抛错以保留原生错误状态，不吞掉 401/500。
- 官方文档：[BrimDeck Custom](https://github.com/MoFeng2223/BrimDeck/blob/01e8bb7077245cc933b032c635409b283597faf2/docs/en-US/ai-tools.md)。菜单名称以此安装版本源码本地化为准。

## 验证状态

调查完成，允许继续实现。完整的自动测试、真实数据对账、打包与 UI 验证结果另见 TEST_REPORT.md；此文档不预先宣称验收通过。

## v1.1.0 展示更新

2026-10-28 用户确认仅改变展示样式，保留现有指标，不增加模板中的历史用量或最近状态采集。卡片式布局仅在 Bridge 原生窗口实现；BrimDeck 仍使用上述 Custom 文本行契约。统计页面移除价格完整性说明，直接显示读取金额；API 仍保留 CostStatus / CostComplete。快照来源、字段含义和只读接口未变。验证结果见当前 TEST_REPORT.md，旧报告在 docs/history 中。

## v1.2.0 当前环境与刷新调查（2026-10-29）

本机运行 CPAMP v1.14.10、CPA v8.0.47、BrimDeck 1.2.1。旧 Bridge 配置仍指向已不存在的 v1.14.9 数据目录；API 统计正常时不受该路径影响，SQLite 降级需要手动更新实际目录。新配置不再硬编码私人版本路径。

### 快照与运行时额度是两条现有数据路径

- `POST /v0/management/quota-snapshots/query` 返回 CPAMP 持久化额度快照与生命周期。
- `GET /v0/management/auth-files` 的 `quota.observed_at / quota.signals` 返回 CPA 运行时保存的额度观测。已实测同账号运行时观测比持久化快照更新，两个接口数值可能暂时不同。
- 当前快照额度时间应读 `field_sources.quota.observed_at_ms`，不能只查旧的 `used_percent / remaining_percent` 字段来源。窗口结束优先当前生命周期 `current_cycle.scheduled_end_ms`，兼容 `cycle_end_ms`。
- 修复保留字段观测时间与最后读取时间的区别；未知数据不推算成百分比。经用户确认，Bridge及独立脚本都合并同账号、同窗口的持久化快照与运行时额度，只用较新有效观测替换；保留停用生命周期边界。查询 include_inactive=true 仅为防止旧运行时数据复活已停用窗口，展示时排除inactive。

### Codex 官方读取

- CPAMP `apps/web/src/utils/quota/constants.ts` 定义官方 GET `https://chatgpt.com/backend-api/wham/usage`。
- `providerRequests.ts:fetchCodexQuotaSummary` 与 `codexRequestHeaders.ts`：取 auth_index、GET 方法、`Authorization: Bearer $TOKEN$`，有明确 workspace 时带 `Chatgpt-Account-Id`。
- CPA `internal/api/handlers/management/api_tools.go:APICall` 接受 authIndex/auth_index，服务端按指定凭证替换 `$TOKEN$`，返回 `status_code/header/body`。外层200不表示上游成功。
- 官方额度读取已分别经 CPAMP 代理和直接 CPA 地址实测200，不访问额度重置券 consume、不请求推理接口。
- 独立脚本通过 BrimDeck pick 仅保留所需账号元信息，2秒账号列表与6.5秒额度请求适配10秒脚本总时限。每来源一个账号，避免多账号串行请求耗尽时间预算。

### CPA 原生统计

- 当前本机 `GET :8317/v0/management/usage` 实测404。CPAMP `:18317/v0/management/usage` 是自己的聚合接口，不能当作CPA直连成功。
- CPA v8 `api_key_usage.go:GetAPIKeyUsage` 提供 API-key 凭证的成功/失败计数，不提供Token/费用。
- `auth-files` 中 success/failed 是凭证的运行时计数，生命周期与CPAMP持久化统计不同。
- 独立 CPAMP 完整统计直接使用其只读 analytics API，保留今日、7天和费用。未写数据库或消费 CPA 用量队列。

参考源码固定为 CPAMP v1.14.10；CPA v8 API读取以本机8.0.47实际响应验证为准，公开main源码仅辅助解释接口语义。BrimDeck契约仍沿用上文固定提交。

CPA完整统计接口缺失时按用户决策明确报告不支持，不读取API-key局部计数替代完整统计。
