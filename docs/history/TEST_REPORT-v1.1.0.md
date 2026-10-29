# v1.1.0 实际测试报告

验证日期：2026-10-28，Windows 11 x64；CPAMP v1.14.9；BrimDeck 1.2.1。

## 更新范围

- 账号页采用双列卡片，沿用现有账号选择、别名、顺序和窗口；主数值与绿色条表示剩余额度，下方显示已用量和重置倒计时。
- 首页改为 8 张统计指标卡；统计脚本显示“今日费用 / 7天费用”及实际数值，不显示“完整性未确认”等文案。原 API 金额、质量元信息、错误和过期状态保留。
- Custom 行使用简短窗口名称和倒计时，percent 仍表示已使用比例。
- 不添加模板中的历史账号统计、最近状态条等新数据。开关只控制桥接展示，不修改上游凭证。

## 自动验证

| 范围 | 结果 | 证据 |
|---|---|---|
| 核心 / SQLite / HTTP / 卡片交互 | PASS，75 项 | docs/evidence/v1.1.0/core-tests.json |
| 三份 JavaScript 契约 | PASS，32 项 | docs/evidence/v1.1.0/script-tests.json |
| 原版 BrimDeck 引擎与真实数据链路 | PASS，18 项 | docs/evidence/v1.1.0/brimdeck-contract.json |
| 今日和7天官方 API 对账 | PASS | 完整数值保存在本机 verification-local，不发布 |
| 运行窗口渲染 | PASS，运行首页与账号卡片实际检查 | docs/images/account-cards-preview.png 是合成数据布局证据 |

合计 **125 项自动检查通过**。新增验证覆盖按期限调度、非每秒轮询、有效快照对象复用、紧凑 HTTP JSON、认证密钥轮换、卡片编辑、刷新保留未保存内容和指标宽度。

账号、别名、顺序、窗口配置结构沿用 v1.0.0。既有的无请求 N/A、未知额度、只读 SQLite、活动 WAL、临时排他锁恢复、归档缺失、凭证身份变化、401 / 500 / 断线与缓存测试继续通过。

## BrimDeck 与真实 CPAMP

正式自包含 EXE 从真实 CPAMP 读取统计和快照；临时测试配置选中两个有 5h/weekly 的 Codex 账号。已安装 BrimDeck 原版 ConfiguredProviders.TestAsync、Jint、MetricParser 执行更新后的三份脚本，核对额度百分比、8行统计、独立账号来源、失败缓存和恢复行为。

由于测试用 .NET 10 SDK 下载中断，本次原引擎测试使用本机 PowerShell 的 .NET 10 运行时编译并运行同一份 C# 测试源，引用同一安装中的 BrimDeck.Core/Jint/Acornima；没有改动 BrimDeck。仓库仍提供 .NET 10 SDK 测试项目供复现。

统计对账调用 analytics 与 dashboard/summary，使用完全相同的 today/week 起止时间。今日和7天 Token / 费用、今日请求、成功与失败均一致。公开材料不含真实账号、实际支出、密钥或完整数值对账。

## 后台占用

- 调度器等待下一次统计 / 额度期限或设置变更，不再每秒唤醒检查。
- GUI 改为数据变化时更新；可见时每分钟更新倒计时，隐藏到托盘后停止 UI 定时器。
- 同一连接设置复用 HttpClient 和连接池；保存连接设置时才更换。
- 桥接 Bearer 认证在服务启动时解密一次，停止时清空，密钥轮换测试通过。
- 有效快照不重复深复制；HTTP JSON 不缩进，减少临时分配和传输体积。

同机、真实 CPAMP、仅本机 HTTP 服务模式，预热10秒后空闲25秒：

| 版本 | 累计CPU时间 | 单核CPU占比 | 工作集 | 私有内存 |
|---|---:|---:|---:|---:|
| v1.0.0 | 46.875 ms | 0.1873% | 138.39 MiB | 64.21 MiB |
| v1.1.0 | 15.625 ms | 0.0624% | 137.38 MiB | 63.47 MiB |

证据见 idle-comparison.json，可通过 tests/measure-idle.ps1 重跑。这是短时单机测量，两个版本的绝对空闲CPU都很低；不据此承诺所有机器上的固定下降比例。指标不包含打开账号页时的 GUI 工作集。

## 保留的边界

BrimDeck 原生每来源一个环、最多7列、percent表示已用的规则不变。Custom 不支持修改卡片布局，模板风格应用在桥接程序中。无额度时保留真实未知状态；过期时保留时间标记。

未操作真实 BrimDeck 设置页面粘贴脚本，未重启 Windows 验证开机启动。两项仍为 NOT TESTED。用户模板图片、真实 GUI 截图、state、verification-local、临时 SDK / 下载均排除在 Git / 发布包外。

v1.0.0 的历史报告见 docs/history/TEST_REPORT-v1.0.0.md。
