# 构建、测试与维护

## 主程序

开发需要 Windows 11 x64、.NET 8 SDK。最终使用者不需要 SDK。可使用项目内的便携 SDK，发布过程不会安装系统级运行时。

```powershell
./build.ps1
```

等价命令：

```powershell
dotnet publish src/Bridge/Bridge.csproj -c Release -r win-x64 --self-contained true -o dist -p:DebugType=None -p:DebugSymbols=false
```

主程序没有第三方 NuGet PackageReference。使用 Windows 自带的 winsqlite3，通过 `sqlite3_open_v2(SQLITE_OPEN_READONLY|SQLITE_OPEN_FULLMUTEX)`、query_only、1500ms busy timeout 和绑定参数读取。正常统计优先 CPAMP API；SQLite 仅作为无管理密钥时的明确降级。

v1.1.0 通过下一次到期时间调度后台读取，数据改变通知可见窗口；托盘隐藏状态不运行 UI 定时刷新。有效快照和 HttpClient 复用，保存设置后重建连接。API 金额元信息保持兼容，展示文案独立于数据契约。

Self-contained / single-file / compressed，捆绑 .NET WinForms 和 ASP.NET Core 运行时。Release 中保留 .NET 第三方通知；不要从发布 EXE 中删减运行时 DLL。首次双击可能需要解包 .NET 自身原生运行库至 Windows 临时目录，这是运行时正常行为。

## 自动测试

在仓库根目录运行，需要 Python 3、Node.js 和 .NET 8 SDK（仅开发 / 测试需要）：

```powershell
dotnet run --project tests/Bridge.Tests/Bridge.Tests.csproj -- "$PWD" "C:/path/to/python.exe"
node tests/scripts.test.mjs
```

SQLite 测试只在 `tests/output` 生成可丢弃数据库，不对 CPAMP 做锁定 / 写入试验。测试涵盖 WAL 中的未 checkpoint 写入、并发 writer、只读写入拒绝、DELETE journal 临时排他锁、归档缺失、缺失状态、重复 request_id / 重试、日期边界、DST、decimal、安全、缓存、HTTP 生命周期和脚本契约。

本版增加卡片控件交互、未保存编辑保留、长数值宽度、调度空闲等待、快照复用和认证密钥轮换。`tests/measure-idle.ps1` 可在相同 CPAMP 环境比较两个 EXE 的空闲 CPU / 内存，只写 tests/output。模板图片含私人账号，Template/ 不进入 Git。

## 可选：本机 BrimDeck 原引擎测试

需要 .NET 10 SDK 和一个已安装的兼容 BrimDeck。此测试引用已安装程序的 `BrimDeck.Core/Jint/Acornima/ZstdSharp`，不会修改安装文件或 BrimDeck 设置，不把这些 DLL 放进发布包。

```powershell
dotnet publish src/Bridge/Bridge.csproj -c Release -o dist
dotnet run --project tests/BrimDeck.Contract/BrimDeck.Contract.csproj -p:BrimDeckInstall="D:/BrimDeck/BrimDeck" -- "$PWD" "$PWD/dist/CPAMP-BrimDeck-Bridge.exe"
```

真实数据测试前，先用 GUI 将自己的连接设置保存到 `tests/live-state`，或使用明确指向现有密钥文件的诊断命令：

```powershell
./dist/CPAMP-BrimDeck-Bridge.exe --diagnose --state-dir tests/live-state --import-key-file "C:/your-private-key-file.txt" --output tests/output/live-reconciliation.json
```

以上密钥文件路径仅是示例，文件里只能有一个可明确识别的管理密钥；通常建议使用 GUI 手动填入。命令不输出密钥。测试使用独立状态目录，临时选择两个有 5h/weekly 快照的 Codex 账号。它会启动发布 EXE 25 秒，通过已安装的 `ConfiguredProviders.TestAsync` 执行正式脚本，核对真实值并正常退出。原 BrimDeck UI 的设置点击验收仍需人工完成。

## 发布和清理

`build.ps1` 将压缩单文件 EXE、三份 JS、教程、许可证和文档放入 dist。将发行文件压缩为 ZIP 并提供 SHA256。Git 追踪完整源码、正式 EXE、脚本和文档，ZIP 以 GitHub Release 附件分发，不重复写进 Git 历史。

退出桥接程序后可删除项目内 `tools`（便携构建 SDK / 下载）、`research`（只读研究副本）、各 `bin/obj`、`tests/output` 与 `tests/live-state`。如需保留验收证据，先把无秘密、无个人信息的 JSON 报告放入 `docs/evidence`。**不要删用户实际使用中的 dist/state。**

## 依赖升级与兼容性

基线 CPAMP v1.14.9、BrimDeck 1.2.1。CPAMP 升级后核对：analytics 请求 / summary 字段、Bearer 认证、auth-files 元信息、quota-snapshots/query 的目标身份规则和生命周期 / field_sources；无 API 密钥降级还要核对 usage_events 与归档覆盖表。字段变化应修适配器并重新测试，不能在原数据库执行迁移。

BrimDeck 升级后核对 fetchUsage(ctx)、Jint / parser、percent 表示已用、原生折叠环和最大应用列数，再跑原引擎测试。通过真实相同起止时间的对账后才更新兼容声明。定期用受支持的 .NET 8 servicing SDK 重建自包含 EXE，以带上运行时安全修复。

## v1.2.0 构建与独立脚本

新增三份直连脚本（额度脚本含两种来源）随 `build.ps1` 复制至 dist，且作为资源嵌入 EXE，生成脚本不依赖外部 JS 文件。源码里的通用 ACCOUNT 选择器不包含真实账号；只有用户点击复制时才生成本机个人选择器。

新增检查：

```powershell
node tests/direct-scripts.test.mjs
./tests/run-direct-contract.ps1 -BrimDeckInstall 'D:/BrimDeck/BrimDeck'
```

后一命令需要 PowerShell 7.6 / .NET 10 运行时和已安装 BrimDeck 1.2.1，在真实 Jint/MetricParser 中执行本地模拟响应，不改 BrimDeck 设置。先用 .NET 8 SDK 运行 `dotnet build src/Bridge/Bridge.csproj -c Release`。`-Live` 是可选本机集成检查，要求自己准备 `tests/live-state` 的 CPAMP 连接和 `tests/output/cpa-state` 的 CPA 连接（同一 ConfigStore/DPAPI 格式），不从测试代码自动提取凭证。

核心测试新增真实30秒调度周期检查：更新模拟服务快照后，等待后台自动读取，断言35%变为43%，不调用手动刷新。HTTP/SQLite/旧脚本测试继续运行。

额度 `quotaSource` 配置为 `cpamp` 或 `official`，省略时默认 `cpamp`。官方模式通过既有 CPAMP 管理代理转发至 CPA，不存储 OAuth Token。CPA 上游 HTTP 状态在 api-call 的 `status_code` 中，不能仅凭外层200当作查询成功。

CPA v8 移除了部分旧版统计 API。维护原生统计脚本时必须区分旧版 `/usage`、OAuth auth-files 请求计数、API-key 请求计数，不消费 `/usage/queue`，不把局部计数当作完整历史。按用户选择，缺少完整接口时直接报告不支持，不降级显示局部计数。

原有桥接脚本也可通过 PowerShell 7.6 复现原引擎测试：

./tests/run-bridge-contract.ps1 -BrimDeckInstall 'D:/BrimDeck/BrimDeck'

增加 -BridgeExe 'dist/CPAMP-BrimDeck-Bridge.exe' 进行真实自包含EXE链路检查（需 tests/live-state）。
