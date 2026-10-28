# CPAMP–BrimDeck Bridge v1.0.0

Windows 11 x64 独立托盘工具，通过 BrimDeck Custom 展示 CPAMP 的真实统计与已有账号额度快照。

- 今日 / 7 天 Token、已知费用估算、请求数、成功 / 失败与成功率。
- 自由选择账号、别名、5h / weekly 等实际窗口与显示顺序，支持独立账号来源。
- 回环 HTTP API、独立 Bearer 密钥、Windows DPAPI、只读 SQLite 降级及过期缓存标识。
- 无需安装 .NET、Python、Node；解压 ZIP 后双击 EXE。

基线：CPAMP v1.14.9，BrimDeck 1.2.1。108 项本地自动检查通过，包含真实 CPAMP 对账和已安装 BrimDeck 引擎链路。

费用为官方已知费用估算，完整性未确认；额度仅来自已有快照。BrimDeck 最多 7 列，每来源环取最大百分比，统计来源环表示成功率。人工在 BrimDeck 设置中粘贴脚本 / 点击验收和 Windows 重启后的自启动尚未执行。

请阅读发行包内《使用教程.md》，在桥接中选择账号，再添加“CPA 账号额度”和“CPA 用量统计”两个 Custom 来源。发布包不包含任何用户密钥、数据库或私人配置。
