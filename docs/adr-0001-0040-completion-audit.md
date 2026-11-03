# ADR 0001–0040 完成度扫描（2026-11-03）

本页记录对 `docs/adr` 中 0040 之前 ADR 的逐项扫描结果，以及本轮能够完成或需要占位的部分。状态严格遵循 [ADR README](adr/README.md)；`Implemented` 不等于 `Verified`，也不代表 stable 发布。

## 扫描结果

| ADR | 扫描前 | 本轮结果 | 说明 |
|---|---|---|---|
| [0012 整合包归档提取边界](adr/0012-modpack-archive-extraction-boundary.md) | In Progress | **Implemented** | 增加单条/累计压缩比限制与 zip bomb 测试 |
| [0013 分支版本与发布频道](adr/0013-branch-based-versioning-and-release-channels.md) | In Progress | **Implemented** | 版本/频道解析、滚动 Release、签名资产与过期构建跳过已由 workflow 覆盖 |
| [0021 WPF 交互与动效](adr/0021-wpf-interaction-and-motion-system.md) | In Progress | In Progress | 动效令牌/按钮/页面转场已有；统一操作确认组件尚未完成，见下方占位 |
| [0022 存储分析与安全清理](adr/0022-storage-analysis-and-safe-cleanup-center.md) | In Progress | **Implemented** | 增加实例级清理预览与“不要存放重要个人文件”提示 |
| [0023 多实例来源发现](adr/0023-multi-root-instance-discovery.md) | In Progress | In Progress | 来源分组改为可折叠；来源组头部总占用仍由存储页统计 |
| [0024 自签名代码证书信任](adr/0024-user-authorized-self-signed-code-signing.md) | In Progress | **Implemented** | 代码、设置入口与签名脚本完整；实机 UAC/证书存储验证未在本轮完成 |
| [0027 原生世界引擎](adr/0027-native-world-engine.md) | In Progress | In Progress | 解析/地图/NBT 只读与语义目录已有；NBT Explorer 编辑工作流与 BOP 真实样本未完成 |
| [0028 世界种子与生成分析](adr/0028-world-generation-analysis.md) | Not Started | Not Started | 仍是 Proposed；已加入显式占位说明，不伪造生成结果 |
| [0030 世界诊断与区块修复](adr/0030-world-diagnostics-and-chunk-fault-workflow.md) | In Progress | In Progress | 诊断解析/聚合/地图标记已有；分级修复工作流未实现 |
| [0031 多维度世界](adr/0031-multi-dimension-world-architecture.md) | Implemented | Implemented | 本轮未改变 |
| [0032 Additions](adr/0032-native-feature-interfaces-and-optional-additions.md) | In Progress | In Progress | 原生扩展页已接入；官方目录仍为空 |
| [0033 本地诊断](adr/0033-local-minecraft-diagnostic-and-knowledge-engine.md) | In Progress | In Progress | 工具箱诊断页提供基础解析；知识库 Data Addition 未接入 |
| [0034 自完整性](adr/0034-ecl-self-integrity-and-recovery.md) | In Progress | In Progress | 本地/联网修复已实现；install-manifest 独立签名与自动发现修复源未完成 |
| [0036 受限知识浏览器](adr/0036-embedded-browser-for-minecraft-knowledge.md) | In Progress | In Progress | 策略/错误页/地址栏契约已有；WebView2 宿主未实现 |
| [0037 衣柜](adr/0037-account-appearance-and-local-wardrobe.md) | In Progress | In Progress | 衣柜页与 Authlib 协议已有；Microsoft/第三方真实 API 未验证 |
| [0038 Authlib 材质上传](adr/0038-authlib-injector-texture-upload-protocol.md) | In Progress | In Progress | 协议与测试已有；无真实第三方服务端验收 |
| [0039 运行时生命周期](adr/0039-application-runtime-lifecycle-and-graceful-shutdown.md) | Implemented | Implemented | 本轮未改变 |

其余 ADR（0001–0011、0014–0020、0025、0026、0029、0035）已处于 Verified、Superseded 或 Implemented，本轮未重新验收，保持原状态。

## 本轮实际改动

### ADR 0012

`ModpackArchiveExtractor` 在条目数、单文件/累计大小、路径穿越、重复目标与 reparse point 检查之上增加：

- 单条压缩比：解压大小 ≥ 1 MiB 且比值 > 500 时拒绝；
- 累计压缩比：累计解压 ≥ 16 MiB 且比值 > 300 时拒绝。

新增 `tests/ECL.Tests/Minecraft/ModpackArchiveExtractorTests.cs`，覆盖正常提取、zip bomb、路径穿越与重复目标；真实大型整合包导入仍需手工回归。

### ADR 0022 / 0023

- 存储页新增“预览选中实例清理”：只预览该实例的历史日志/诊断临时文件，不混入下载缓存。
- 存储页明确提示不要将重要个人文件放入实例目录，未知文件按用户数据处理。
- 实例列表的来源分组改为可折叠 `Expander`；折叠只影响展示。

### ADR 0024

代码与设置入口已具备：证书固定指纹、TrustedPeople 信任/撤销、用户显式确认、发布签名脚本；实机 UAC、证书已存在/轮换/系统重装路径仍需在干净 Windows 环境验收，因此不标 `Verified`。

### 占位

主窗口「工具箱」新增：

- **知识浏览**：显示受限域名策略与 WebView2 宿主未实现的阻塞状态；
- **原版工具**：显示数据包未安装与模板位置；
- **世界工具**：显式标注 ADR 0028 未实现、ADR 0027/0030 编辑/修复工作流未完成。

这些占位不会伪造查询、生成或编辑结果。

## 验证

```powershell
dotnet build ECL.sln -c Release
dotnet test tests/ECL.Tests/ECL.Tests.csproj -c Release
dotnet format ECL.sln --verify-no-changes --no-restore
python scripts/check_docs.py
pwsh -NoProfile -File scripts/check-commit-hygiene.ps1
```

UI 冒烟：`ECL_START_PAGE=tools` 启动后可通过 UI Automation 找到扩展/衣柜/诊断/知识浏览/原版工具/世界工具标签，切换后无未处理异常。
