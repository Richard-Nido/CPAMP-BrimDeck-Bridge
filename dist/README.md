# CPAMP–BrimDeck Bridge

一个独立的 Windows 11 x64 托盘小工具。只读 CPA-Manager-Plus（CPAMP）的已有统计和额度快照，通过 BrimDeck Custom JavaScript 显示结果。

支持今日 / 7 天 Token、已知费用估算、今日请求与成功率；从账号池选择少量账号，设置别名、额度窗口和顺序。默认不选中任何账号。

不修改 CPA、CPAMP、BrimDeck 的源码或配置，不写 CPAMP 数据库，不消费 CPA 用量队列，不启动上游巡检 / OAuth 刷新。

## 下载与启动

下载 [Releases](https://github.com/Richard-Nido/CPAMP-BrimDeck-Bridge/releases) 中的 Windows x64 包，解压到独立可写文件夹，双击 `CPAMP-BrimDeck-Bridge.exe`。无需安装 .NET、Python 或 Node。

1. 在“连接设置”填写 CPAMP 本机地址和 Admin Key，点击“保存并测试连接”。
2. 在“账号管理”勾选要显示的账号、设置别名与窗口，点击“保存账号选择”。
3. 在“BrimDeck 接入”复制地址、桥接密钥、额度脚本和统计脚本。
4. 在 BrimDeck“设置 → 配额与统计 → 添加应用”创建两个 Custom 来源，分别粘贴脚本并“测试连接”。

详见 [中文使用教程](使用教程.md)。仓库的 `dist/` 包含可直接运行的压缩单文件 EXE、脚本和教程；Release 另提供完整 ZIP 包。

## 指标含义与限制

- 今日从所选 Windows 时区午夜开始，查询区间左闭右开。7 天可选“含今天 7 个自然日”（默认、对应 CPAMP 请求监控）或“滚动 168 小时”（对应 CPAMP 用量分析）。
- 统计与费用由 CPAMP 官方聚合 / 定价引擎提供。金额以 decimal 读取，不重新维护价格表。API 没有可靠的价格覆盖完整性承诺，因此始终标注“已知费用估算，完整性未确认”；不等同订阅真实扣款。
- 额度来自已有快照，显示过期 / 更新时间；没有快照的账号显示未知，不主动查询上游。身份变化不按邮箱或列表位置自动接到另一个账号。
- BrimDeck 每个应用列一个折叠环，取该来源最大百分比；最多 7 列。统计来源的环表示请求成功率。无百分比的纯文字行可能显示满环，应以行文字为准。此版本没有单独隐藏某个来源环的设置。
- Custom 仅提供配额行。它不能向 BrimDeck 底部“统计应用”的内置 Token/费用区域写入数据。

## 兼容、开发与证据

验证基线：CPAMP v1.14.9 (`6d962db680085827cd19a778d2ff05a3d1315672`)，BrimDeck 1.2.1 (`01e8bb7077245cc933b032c635409b283597faf2`)。

- [构建与维护](BUILD.md)
- [实际测试报告](TEST_REPORT.md)
- [源码调查](docs/SOURCE_INSPECTION.md)
- [第三方声明](THIRD_PARTY_NOTICES.md)

主程序使用 .NET 8 WinForms、Kestrel、HttpClient、Windows DPAPI 和 Windows 自带 winsqlite3；没有第三方 NuGet 包。测试工具可选使用本机 BrimDeck 库，库不随本项目分发。

MIT License。问题反馈请勿提交密钥、数据库、OAuth 凭证或带个人账号的截图。
