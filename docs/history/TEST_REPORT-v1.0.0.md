# 实际测试报告

验证日期：2026-10-28（Asia/Shanghai）。环境：Windows 11 x64；CPAMP v1.14.9；BrimDeck 1.2.1。测试使用了真实运行中的 CPAMP，第三方安装和配置未改动。

## 结果

| 范围 | 结果 | 证据 |
|---|---|---|
| 核心程序 / SQLite / HTTP | PASS，62 项 | docs/evidence/core-tests.json |
| JavaScript 契约 | PASS，28 项 | docs/evidence/script-tests.json |
| 已安装 BrimDeck 引擎与正式 EXE 链路 | PASS，18 项 | docs/evidence/brimdeck-contract.json |
| 正式 Windows x64 自包含 EXE | PASS，实际构建并启动 | dist/CPAMP-BrimDeck-Bridge.exe |
| 原生窗口页面 | PASS，实际启动并检查 4 个页面控件快照 | 本机验证记录；不公开含个人账号的截图 |
| BrimDeck 设置 UI 内人工粘贴 / 点击验收 | NOT TESTED | 没有修改现有 BrimDeck 设置；已直接运行同一安装的 ConfiguredProviders.TestAsync / Jint / MetricParser |
| Windows 重启后的自动启动 | NOT TESTED | 实现当前用户 Run 项开关，但没有为测试重启电脑或启用开机启动 |

自动化合计 **108 项通过，0 项失败**。真实 CPAMP 的账号池发现、快照、统计已验证；故障、空库、锁定、缺失字段等不能在原库破坏性制造的情形使用隔离 fixture 验证。

## 真实 CPAMP 对账

最后一次同区间对账时间：2026-10-28 15:46:55（UTC+8）。时区是运行环境得到的 Asia/Shanghai。

- 今日 `[2026-10-27T16:00:00Z, 2026-10-28T07:46:55.051Z)`。
- 7 个自然日 `[2026-10-21T16:00:00Z, 2026-10-28T07:46:55.051Z)`。
- Bridge 从 monitoring/analytics 读取；独立调用 dashboard/summary 传相同起止时间核对。

| 指标 | CPAMP / Bridge 比较 | 结论 |
|---|---|---|
| 今日 Token | 精确整数相等 | PASS |
| 7天 Token | 精确整数相等 | PASS |
| 今日已知费用估算 | 官方 API 数值一致 | PASS |
| 7天已知费用估算 | 官方数值一致，校验容差小于 0.00000001 USD | PASS |
| 今日请求 | 精确整数相等 | PASS |
| 成功请求 | 精确整数相等 | PASS |
| 失败请求 | 精确整数相等 | PASS |

为开源隐私，公开报告不含用户真实 Token 总量、支出或原始账号信息；完整数值对账 JSON 只保留在本机 `verification-local`，不提交 Git、不放入发行 ZIP。费用一致不意味着所有模型定价完整，程序始终标“已知费用估算，完整性未确认”。官方引擎使用浮点输出，Bridge decimal 原样接收，不承诺消除官方浮点尾差。

## 真实额度链路

- 发现 14 个账号元信息（含 9 个 Codex）；仅在隔离测试配置中选择其中 2 个有 5h/weekly 快照的 Codex，未改变正式用户选择或上游账号。
- 用最终 EXE 启动本机 API，调用已安装 BrimDeck 的原版 ConfiguredProviders.TestAsync 执行正式三份脚本。
- 原版 Jint 成功执行，原版 MetricParser 接受返回行；每个实际 usedPercent 与 HTTP 快照字段逐值相等，没有把 remaining 当 used。
- 验证真实部分账号筛选、两个窗口、别名、独立账号来源和正常退出。无快照账号显示未知，不为测试主动触发巡检、登录、OAuth 刷新或模型调用。

## 覆盖说明

核心 62 项：空数据、仅成功 / 仅失败 / 混合、UTC 日期 / 跨月 / 跨年 / DST、自然日 / 168h、精确 long 与 decimal、未知分类拒绝、已用 / 剩余换算、0% / 100% / 未知 / 过期 / 异常额度、部分账号 / 多窗口 / 别名 / 顺序 / 同名 / 身份变化、DPAPI 与持久化、无明文密钥、回环限制、401 / 500 / 重新连接、并发复用缓存、正常退出关闭端口。

隔离 SQLite：活动 WAL 中已有 writer 写入仍能读取最新数据；同一 request_id 的不同重试记录按 CPAMP 事件口径保留；不同模型 / 已删除账号历史不受账号选择影响；只读写入被拒绝；DELETE journal 排他锁明确报错并能恢复；归档删除造成原始记录缺失会阻止不完整统计；文件被移动会报错。原库没有进行写入、迁移、VACUUM、REINDEX、checkpoint 或锁定试验。

脚本 28 项：8 行中文 / 数字格式、仅成功率带 percent、无请求 N/A、费用未知、窗口端点、部分账号、未知 / 过期 / 失效 / 异常、401 / 500 / 断线 / 超时模拟、缺少 JSON、单账号模板和错误输入。断线也经过原版引擎异常路径；没有人为把真实 CPAMP 拖慢。

## 安全与非侵入性

- Bearer 未提供 / 错误密钥返回 401，跨站 Origin 与伪造 Host 返回 403；监听器仅 127.0.0.1。非本机不存在可连接的 LAN 监听地址。
- CPAMP 管理密钥和桥接密钥分开，DPAPI 当前用户加密。返回值和日志不输出管理密钥 / OAuth Token。测试确认配置不保存明文测试密钥。
- 主程序只含已确认的 analytics 查询、auth-files GET、quota-snapshots/query 和诊断 dashboard GET。没有队列消费或账号修改 / 巡检 / refresh 请求路径。
- 原库一直接收 CPAMP 自己的写入，不能用前后文件 hash 不变来证明只读。证据来自 READONLY + query_only、拒绝写入试验、参数化 SQL / API allowlist 审计和原服务仍正常响应。
- 账号截图、加密的本机配置、实际支出对账数据不发布。临时第三方源码、SDK、压缩下载、bin/obj 和测试缓存按用户要求在验收结束后删除。

## 已知边界

仅承诺官方已有快照的数据能力。快照不等同实时上游查询；BrimDeck 原生最多 7 列、每来源最大百分比一个环，无单列隐藏环设置，纯文字也可能画满环。成功率来源单独的环属于请求统计。Custom 不写内置 Usage app 数据。完整初次接入仍需使用者在 BrimDeck 设置中粘贴脚本并点击测试。

源码与构建均已交付，尚未执行的人工 UI / 重启验收没有标成通过。
