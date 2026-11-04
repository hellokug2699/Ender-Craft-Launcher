# ADR 0041–0049 实施记录

- 记录日期：2026-11-03
- 分支：`nightly`
- 范围：ADR 0041–0049 中当前代码库与运行环境可支撑的部分
- 状态依据：[ADR README](adr/README.md) 的生命周期规则

本记录只描述实际存在、可构建和可复核的实现。未完成项明确保留为未完成，不把文档、界面占位或测试桩写成已验收能力。`Implemented` 表示约定范围已有实现，但不表示已经完成全部实机验收；本批 ADR 均未标记为 `Verified`。

## 状态总览

| ADR | Decision | Implementation | 已实现范围 | 主要缺口 |
|---|---|---|---|---|
| 0041 性能监控 | Accepted | In Progress | 进程/系统采样、Hang Detection 状态机、profiler backend 契约、本地样本日志 | 真实 profiler backend、游戏侧 telemetry 实机接入、性能页与端到端验收 |
| 0042 文件浏览器与崩溃报告 | Accepted | In Progress | 实例范围文件访问、共享 Inventory、资源识别、依赖/兼容证据、JAR 图标提取 | 原生 Explorer 页面、崩溃报告自动诊断联动、处理器注册与授权 UI |
| 0043 威胁防护 | Accepted | In Progress | 静态扫描、证据分级、可恢复隔离、启动前门控、风险接受与升级重提示 | 安全中心完整 UI、知识库分发、运行时证据 |
| 0044 网络诊断 | Accepted | In Progress | DNS/TCP/TLS/HTTP 分阶段探测、代理路径标注、本地历史 | 独立 Addition 打包与原生页面 |
| 0045 Instance Engine | Accepted | In Progress | Identity、File Access、Inventory、Transaction、Snapshot 的统一 API | 依赖/影响分析深化、既有写入路径全量迁移、迁移/克隆 UI |
| 0046 实例身份 | Accepted | Implemented | 稳定 InstanceId、实例内标记、本地登记、重新定位、克隆、冲突检测 | 真机跨安装迁移验证 |
| 0047 事务与并发 | Accepted | In Progress | 实例级互斥、跨进程锁、阶段日志、前态指纹、回滚、NeedsRecovery、幂等 | 安装/整合包更新等既有路径尚未全部迁移 |
| 0048 快照与恢复 | Accepted | Implemented | 内容寻址存储、清单与哈希、差异、事务化恢复、显式清理 | 独立快照分支 UI、真机大实例验证 |
| 0049 GitHub | Accepted | In Progress | 固定仓库边界、原生资源解析、Markdown 链接解析、受控 API、ETag/限流 | GitHub App 授权、安全令牌存储、Addition 与原生页面 |

## 实施位置

### Instance Engine（ADR 0045–0048）

- `src/ECL.Minecraft/Engine/Identity/InstanceIdentityService.cs`：InstanceId 校验、`.ecl/instance.json`、本地 `registry.json`、路径重新定位、冲突证据。
- `src/ECL.Minecraft/Engine/Files/InstanceFileAccess.cs`：实例根目录边界、路径规范化、重解析点检查、目录统计、文本预览、受控导出、文件类型注册表。
- `src/ECL.Minecraft/Engine/Inventory/InstanceInventoryScanner.cs`：快速文件扫描、资源识别、模组深度分析、缓存与失效。
- `src/ECL.Minecraft/Engine/Inventory/ModIconExtractor.cs`：按 Fabric/Forge 元数据声明路径提取图标，限制条目大小、尺寸与像素数。
- `src/ECL.Minecraft/Engine/Transactions/InstanceTransactionCoordinator.cs`：实例级信号量、跨进程锁、阶段状态、前态指纹、恢复材料、回滚与 NeedsRecovery。
- `src/ECL.Minecraft/Engine/Snapshots/InstanceSnapshotService.cs`：用户快照创建、完整性校验、差异比较、事务化恢复、删除与孤立对象清理。
- `src/ECL.Minecraft/Instances/InstanceService.cs`：创建/导入/更新时登记身份，删除时移除登记，新增 `RelocateAsync` 与 `CloneAsync`。

### 文件与资源（ADR 0042）

- Inventory 是 Explorer、工作台和诊断共享的数据源；当前 UI 在工具箱“实例引擎”页读取同一 Inventory。
- 资源识别覆盖 Mod、资源包、光影、世界、投影/蓝图、崩溃报告、日志与配置。
- 模组依赖区分必需/可选/未知；Minecraft 与 Loader 兼容性分别记录，缺少证据时返回 Unknown。
- 崩溃报告作为 Inventory 资源同时保留原始文件；自动诊断联动仍由现有诊断引擎入口承担，尚未在 Explorer 页面串接。

### 性能（ADR 0041）

- `src/ECL.Minecraft/Performance/HangDetector.cs`：启动宽限、Tick 是否推进、MSPT 退化/Stall、疑似 Hang、恢复与退出。
- `src/ECL.Minecraft/Performance/PerformanceMonitorService.cs`：进程 CPU/内存采样、可选游戏侧 telemetry（`.ecl/telemetry/tick.jsonl`）、本地 JSONL 样本日志。
- `IProfilerBackend` / `ProfilerSessionManager`：backend 可替换，不可用时基础监控不受影响；当前未注册真实后端。
- `StartGameStep` 在进程启动后尝试开始监控，监控失败只记录警告，不影响游戏。

### 威胁防护（ADR 0043）

- `src/ECL.Minecraft/Security/ThreatProtectionService.cs`：手动实例扫描、归档结构与路径检查、脚本/字节码敏感特征、本地已知哈希。
- 隔离通过事务删除原文件并保留可恢复副本；恢复通过事务写回并校验哈希。
- `SecurityGateStep`（Order 450）在创建游戏进程前读取最近扫描结果并检查 Java Agent 参数；需要确认时暂停启动。
- 风险接受记录与检测结果分离；内容哈希、规则/扫描器版本变化后不再复用旧接受记录。

### 网络诊断（ADR 0044）

- `src/ECL.Minecraft/Network/NetworkDiagnostics.cs`：DNS、TCP、TLS、HTTP 分阶段结果；超时、并发、重复次数受控；明确标注直连/代理路径。
- 不做主动测速、不下载测试文件、不附加账户令牌；结果只写本地历史，可清除。

### GitHub（ADR 0049）

- `src/ECL.Minecraft/GitHub/GitHubRepositoryPolicy.cs`：固定 `hellokug2699/Ender-Craft-Launcher`，拒绝其他仓库/主机、编码路径穿越、重复分隔符与不支持资源。
- `GitHubApiClient`：固定 API 主机与仓库 endpoint、匿名公开读取、写入需 token、ETag 条件请求、离线缓存、速率限制分类。
- `IGitHubTokenStore` 目前只有 `NullGitHubTokenStore`；未接入平台安全存储，因此写入在默认安装中不可用，这是明确的未完成项。

## UI 与 CLI

- 工具箱新增“实例引擎”页：实例选择、Inventory 扫描、快照创建/恢复/删除、安全扫描与风险列表。
- CLI 新增：
  - `engine inventory|snapshot|verify|transactions|clone|relocate`
  - `security scan|status|quarantine|restore|list`
  - `network diag`
  - `github resolve`
  - `launch --accept-security-risk`

## 自动化证据

- `tests/ECL.Tests/Engine/InstanceIdentityTests.cs`
- `tests/ECL.Tests/Engine/InstanceFileAccessTests.cs`
- `tests/ECL.Tests/Engine/InstanceInventoryTests.cs`
- `tests/ECL.Tests/Engine/InstanceTransactionTests.cs`
- `tests/ECL.Tests/Engine/InstanceSnapshotTests.cs`
- `tests/ECL.Tests/Security/ThreatProtectionTests.cs`
- `tests/ECL.Tests/Performance/HangDetectorTests.cs`
- `tests/ECL.Tests/Network/NetworkDiagnosticsTests.cs`
- `tests/ECL.Tests/GitHub/GitHubPolicyTests.cs`

验证命令：

```powershell
dotnet build ECL.sln -c Release
dotnet test tests/ECL.Tests/ECL.Tests.csproj -c Release
dotnet run --project src/ECL.Cli -- engine inventory <实例ID> --json
dotnet run --project src/ECL.Cli -- engine snapshot create <实例ID> --scope full
dotnet run --project src/ECL.Cli -- security status <实例ID>
dotnet run --project src/ECL.Cli -- network diag mojang --json
dotnet run --project src/ECL.Cli -- github resolve https://github.com/hellokug2699/Ender-Craft-Launcher/issues/1
```

## 实机验证记录

在临时数据目录与临时 Minecraft 工作区中完成了一次真实端到端验证（不涉及用户现有实例）：

1. `instance create` 创建 1.20.1 实例，`engine verify` 返回 `Ok · Confirmed`。
2. `install` 成功下载并校验 3,627 个文件、732,939,111 字节；`launch --dry-run` 生成 41 个参数并选择 Java 21。
3. `security scan` 对一个测试 JAR 检出归档内脚本条目，结论 `Suspicious`。
4. 首次真实启动在安全检查步骤暂停，未创建游戏进程；使用 `--accept-security-risk` 后记录风险接受并成功启动游戏进程。
5. `StartGameStep` 随进程启动写入本地性能样本 JSONL；样本阶段为 `Starting`，未提供游戏侧 Tick telemetry 时明确标记 `不可用`，未伪造 TPS/MSPT。
6. `engine snapshot create/list` 生成并列出完整快照；`network diag mojang` 返回 DNS/TCP/TLS/HTTP 分阶段结果；`github resolve` 接受目标仓库 Issue、拒绝其他仓库引用。

该验证证明“安装 → 快照 → 安全门控 → 风险接受 → 进程启动 → 性能样本”链路可用；仍未验证真实 profiler backend、恶意样本处置、GitHub App 授权与安全令牌存储，因此不将相关 ADR 标记为 `Verified`。

## 未完成与风险

1. **未标记 Verified**：缺少真实 profiler backend、真实威胁样本、GitHub App 授权与安全令牌存储的端到端验收。
2. **写入路径迁移未完成**：模组安装、整合包更新、世界编辑等既有流程仍可能绕过 Instance Engine 事务；ADR 0045/0047 保持 `In Progress`。
3. **UI 范围有限**：本批只增加一个原生实例引擎页；完整文件浏览器、崩溃报告详情、安全中心与 GitHub 项目中心页面尚未实现。
4. **外部输入与网络**：GitHub 内容、Markdown、归档和模组都被当作不可信输入处理；不实现网页宿主、不自动加载远程图片、不执行远程代码。
5. **本地优先**：所有新增扫描、快照、性能样本、网络历史、风险接受记录与 GitHub 缓存均只写本地；未新增遥测或上传路径。
