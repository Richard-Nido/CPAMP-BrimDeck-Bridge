# v1.2.0 实际测试报告

验证日期：2026-10-29；Windows 11 x64、CPAMP v1.14.10、CPA v8.0.47、BrimDeck 1.2.1。

## 结果

共 **199 项自动检查通过**，正式源码可构建为自包含 Windows x64 EXE。测试结果如下：

| 范围 | 结果 | 证据 |
|---|---|---|
| 核心 / SQLite / HTTP / 调度 / 窗口选择 | PASS，98 项 | docs/evidence/v1.2.0/core-tests.json |
| 原桥接 JavaScript | PASS，32 项 | docs/evidence/v1.2.0/script-tests.json |
| 独立 Custom 脚本 | PASS，39 项 | docs/evidence/v1.2.0/direct-script-tests.json |
| 原 BrimDeck 引擎 / 正式EXE链路 | PASS，18 项 | docs/evidence/v1.2.0/brimdeck-contract.json |
| 独立脚本 BrimDeck 引擎 / 真实接口 | PASS，12 项 | docs/evidence/v1.2.0/direct-brimdeck-contract.json |

## 刷新问题与修复

本机实测同账号 auth-files.quota 的运行时额度比 quota-snapshots/query 持久化快照更新。旧 Bridge 只查询后者，因而可能继续展示旧额度和过期重置时间。旧代码还未读取当前 field_sources.quota 的额度观测时间。

经用户确认，CPAMP模式合并两条已有数据路径，以同账号、同窗口的有效额度观测时间决定取值；不会请求官方接口。读取包含inactive状态用于阻止旧运行时信息复活已停用窗口，展示时排除已停用窗口。兼容旧all和新codex_main主额度选择，附加模型窗口不并入主额度。

自动刷新回归使用实际30秒调度周期：模拟服务从35%更新到43%，未调用手动刷新，后台自动观察到新值。另有新运行时观测替换旧持久化值、旧运行时不覆盖新快照、缺失时间、过期与停用生命周期测试。

官方模式通过现有CPAMP管理代理让CPA查询官方wham/usage，仅请求已勾选Codex账号；401/429、超时与字段错误不会变成0%或100%。429测试保留最近官方值并标过期，health报告partial。切换来源后保留已保存窗口勾选。

## 无需常驻 Bridge 的实测

- 在本机已安装 BrimDeck 的真实 ConfiguredProviders / Jint / MetricParser 中运行正式独立脚本。
- CPA地址直连额度：只使用CPA管理API，动态读取auth_index，再由CPA请求官方接口；没有经过Bridge或CPAMP代理。
- CPAMP地址直连额度：只读取既有快照和运行时观测，合并后显示，不请求官方。
- CPAMP地址直连完整统计：8行指标成功，固定相同起止时间与官方dashboard统计对账一致。
- CPA v8.0.47的完整/usage接口实测404。按用户决策，正式脚本明确报告不支持，不调用局部计数接口作为降级。兼容旧版/usage响应的分支使用模拟真实契约验证；不宣称本机CPA支持完整原生统计。

BrimDeck默认脚本时限10秒；额度来源单账号两次本机请求（2秒列表、6.5秒数据），CPAMP统计两次并行查询。不同账号创建不同Custom来源。错误由BrimDeck原生最近成功缓存处理；无百分比的文字行不填虚假环值。

## 发布与界面检查

新版源码、嵌入脚本和独立脚本一起发布。使用程序内DrawToBitmap检查设置页与接入页布局，新增来源选择与复制按钮有对应控件；该检查不等同于真实桌面截图或用户手工点击验收。真实账号界面图只留本机临时目录，不发布。

仍为NOT TESTED：在真实BrimDeck设置页手工粘贴并保存来源、重启Windows验证开机启动。测试没有修改BrimDeck设置、CPAMP配置/数据库或消费CPA用量队列；测试用秘密只在隔离DPAPI状态中处理。

历史测试报告保留在docs/history/TEST_REPORT-v1.1.0.md。
