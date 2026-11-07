# ADR 0041：Minecraft 性能监控与性能分析架构

- Decision: Accepted
- Implementation: In Progress
- Date: 2026-11-02
- Scope: Minecraft 实例运行期间的性能健康监控、卡死检测、性能分析器集成、性能证据采集及与本地诊断引擎的联动
- Related: [ADR 0032：原生功能入口与按需 Additions 扩展体系](0032-native-feature-interfaces-and-optional-additions.md)、[ADR 0033：本地 Minecraft 全面诊断与知识引擎](0033-local-minecraft-diagnostic-and-knowledge-engine.md)、[ADR 0039：应用运行时生命周期与优雅关闭](0039-application-runtime-lifecycle-and-graceful-shutdown.md)
- Additions: [Performance Monitor](../additions/ecl.performance-monitor/README.md)、[Performance Profiler](../additions/ecl.performance-profiler/README.md)

> **Addition 登记说明：** 本 ADR 记录领域架构决策，不再作为扩展目录或发布状态的权威来源。相关 Addition 的正式身份、登记状态、版本与发布信息统一以 [ECL Additions Registry](../additions/README.md) 为准；当前登记目录中的对应项目仍属于候选，须完成 ADR 0054 评审与登记流程后才成为正式条目。

## 背景

Minecraft 的“卡顿”并不只是一种问题。游戏可能因为 Tick 计算超时而降低 TPS，也可能因为渲染耗时、JVM 垃圾回收、内存压力、CPU 或磁盘 I/O 等因素表现异常。启动器如果只负责启动进程，就无法在问题发生时持续记录足够的上下文，也难以区分正常高负载、短暂卡顿和真正的游戏无响应。

现有专业 profiler 已经能够深入 JVM 和 Minecraft 运行时，例如通过 sampling、Tick profiling、allocation profiling 或 flame graph 定位高耗时调用。ECL 没有必要重新实现 JVM profiler；更有价值的方向是建立统一的性能监控与诊断入口，将轻量、持续的健康监控与按需、重型的性能分析分层，并把分析结果接入 ADR 0033 的本地诊断引擎。

因此，本 ADR 讨论的不是“把某个 profiler 模组硬编码进启动器”，而是建立 ECL 的性能诊断架构，并允许以官方 Addition 的形式提供基础监控与高级 profiler 能力。

## 决策

### 1. 建立 ECL 性能诊断体系，并划分基础监控与高级分析

ECL 建立统一的 Minecraft Performance 领域，至少分为两个官方 Addition：

- `ecl.performance-monitor`：基础性能监控 Addition。
- `ecl.performance-profiler`：高级性能分析 Addition。

两个 Addition 均不并入 ECL Core 的普通业务 UI，也不应影响没有安装对应 Addition 时的正常 Minecraft 启动流程。

基础监控面向持续运行，重点回答“游戏现在是否健康”；高级分析面向按需采样，重点回答“究竟是什么导致了性能问题”。

### 2. 基础性能监控采用低开销、持续运行模型

`ecl.performance-monitor` 应尽量以低开销方式持续记录 Minecraft 进程的健康状态。能够可靠获得的数据包括但不限于：

- 游戏进程是否存在、是否正常退出；
- CPU、内存及必要的系统资源使用情况；
- JVM Heap、GC 等运行时指标（能够可靠取得时）；
- Minecraft Tick 健康度，包括 TPS、MSPT、Tick stall 等游戏侧指标（需要游戏侧数据时通过受控的数据源获取）；
- FPS、Frame Time、GPU 等客户端渲染指标（只有存在可靠游戏侧或系统侧数据源时才提供）；
- 性能指标的时间序列及异常事件。

基础监控不得为了获取单个指标而引入高开销 profiler。不能可靠取得的指标必须明确标记为不可用，而不是使用猜测值填充。

### 3. 基础监控必须能够区分高负载、启动阶段、正常暂停和疑似卡死

ECL 应建立独立的 Hang Detection 逻辑，而不是简单使用“进程仍存在”判断游戏是否正常。

至少应区分：

1. **启动/加载阶段**：进程存在但游戏 Tick 尚未进入稳定运行状态，不应直接判定为卡死。
2. **正常运行**：Tick 持续推进，性能指标在正常范围内变化。
3. **性能退化**：Tick 仍推进，但 MSPT、Frame Time 或系统资源持续异常。
4. **短暂 Stall**：出现单次或短时间 Tick 超时，应记录异常而不是立即判定进程无响应。
5. **疑似 Hang**：进程仍存在，但关键进度指标在超过阈值的时间内没有推进，且无法由已知暂停/加载状态解释。
6. **进程退出**：进程已经终止，应转入正常退出或异常退出处理。

Hang Detection 的阈值和判定方式应允许按游戏版本、运行阶段及数据源能力调整，不得把固定时间阈值作为所有实例的唯一判断依据。

### 4. 高级性能分析采用按需启动的 profiler backend

`ecl.performance-profiler` 不自行实现 JVM sampling profiler，而采用可替换的 profiler backend 架构。

首选集成方向为兼容 Spark 类能力的现有 profiler backend；若使用 Spark，应通过明确的兼容、许可和版本策略进行集成。ECL 不得因为支持某一 profiler 而把该 profiler 的实现细节扩散到 Core 或普通启动流程。

高级分析至少应允许逐步支持：

- CPU sampling；
- Tick profiling；
- Slow tick 捕获，例如仅捕获超过指定 MSPT 阈值的 Tick；
- Thread profiling；
- Allocation profiling（backend 支持时）；
- 调用树与 flame graph；
- 按模组、包、类或方法聚合的耗时信息（backend 数据允许时）；
- Profile 导出与本地保存。

Profiler backend 的安装、启停、版本兼容和失败处理必须与 ECL 启动流程解耦。Profiler 不可用时，基础性能监控仍必须正常工作。

### 5. 性能分析应优先采用“异常发生时捕获”，避免长期重型 profiling

高级 profiler 默认不应持续高强度运行。ECL 应优先提供按需分析与异常触发模式，例如：

```text
检测到 MSPT 持续超过阈值
        ↓
提示或按用户授权启动性能分析
        ↓
仅捕获异常窗口 / Slow Tick
        ↓
停止 profiler
        ↓
导入本地分析结果
```

用户必须能够主动开始、停止和查看 profiler 状态。自动触发 profiler 时也必须有明确的用户授权或可配置策略，不得静默上传分析结果。

### 6. 性能数据进入本地诊断引擎，而不是成为独立的分析孤岛

性能监控与 profiler 结果应作为 ADR 0033 Diagnostic Engine 的一种证据来源，与游戏日志、崩溃报告、世界信息和运行环境信息结合。

诊断结果可以形成如下证据链：

```text
症状
  ↓
MSPT / TPS / Frame Time / Resource usage
  ↓
异常时间窗口
  ↓
Profiler evidence
  ↓
Mod / package / class / method attribution
  ↓
诊断规则与知识
  ↓
可能原因、置信度与建议
```

诊断引擎不得把 profiler 的采样结果直接包装成确定性因果结论。除非存在足够证据，否则应使用“可能原因”“相关性证据”等保守表述。

### 7. 性能监控必须支持客户端与服务端场景

ECL 的性能架构不应假定 Minecraft 只有单一“服务器 Tick”模型。

- 对单机客户端，应区分游戏 Tick、渲染 Frame、JVM 和系统资源。
- 对独立服务器实例，应重点关注 Tick、MSPT、TPS、JVM 和系统资源。
- 对可能存在代理、模组服务器或特殊运行模式的实例，应根据实际数据源能力声明可用指标。

不能取得的指标必须表现为“不可用”或“未知”，不得跨场景推断。

### 8. 游戏侧数据源与 ECL 进程外监控解耦

ECL 应尽可能从进程外获取不需要游戏内部协作的基础信息，例如进程状态、CPU、内存和部分 JVM/系统指标。

对于 TPS、MSPT、FPS、Tick stall 以及 profiler 数据等必须依赖游戏内部运行时的信息，应允许通过游戏侧 Addition、兼容 profiler backend 或其他明确协议获取。

因此架构应允许：

```text
ECL
├── Process / System Monitor
│
├── Performance Monitor Addition
│      ↑
│      └── Game-side telemetry（可选）
│
└── Performance Profiler Addition
       ↑
       └── Profiler backend（可替换）
```

ECL 不得为了获取游戏内部数据而注入任意未经授权的代码，也不得要求普通启动流程必须安装性能分析组件。

### 9. 性能数据默认本地保存，上传必须显式授权

性能指标、Profiler profile、调用树和诊断证据默认仅存储在本地。ECL 不得因为启用性能分析而自动将 profile、日志、世界信息、实例信息、账号信息或其他运行数据上传到远程服务。

如果未来提供远程分享或在线分析功能，必须单独定义数据范围、脱敏规则、用户授权和保留策略，并不得改变本 ADR 的本地优先原则。

### 10. 性能分析 Addition 与 Core 保持清晰边界

ECL Core 提供稳定的生命周期、进程管理、诊断证据接口、数据模型和 Addition API，但不承担具体 profiler UI 或某个 profiler backend 的实现。

Performance Addition 可以依赖 Core 的受控能力，但不得直接依赖 WPF UI 内部类型、任意私有实现或未稳定的业务对象。

这与 ADR 0032 的 Addition 边界一致，也与 ADR 0039 的运行时生命周期模型一致：Addition 应拥有自己的启动、停止和资源释放流程，不能把 profiler backend 的生命周期混入 ECL 全局退出逻辑。

### 11. 与 Spark 等现有工具的关系

ECL 可以集成或适配成熟的第三方 profiler，例如 Spark，但不把“Spark 本身”定义为 ECL 的架构边界。

因此：

- ECL 可以将 Spark 作为一个 profiler backend；
- profiler backend 可以在未来替换或增加其他实现；
- ECL 不复制 profiler 的核心实现；
- 第三方组件的许可证、版本兼容性和分发方式必须在实现阶段单独确认；
- 如果某版本 Minecraft / Loader 无法安全提供 profiler backend，基础监控仍可运行。

### 12. 不在本 ADR 中冻结具体指标、阈值和 UI

TPS、MSPT、FPS、Frame Time、CPU、RAM、GC 等指标的具体展示方式、默认阈值、颜色和交互属于 Feature Spec / Roadmap，不在本 ADR 中固定。

同样，profiler 的具体命令、启动方式、profile 文件格式和第三方 backend 版本不作为长期架构约束；实现阶段必须根据目标 Minecraft 版本、Loader 和 backend 能力验证。

## 备选方案

### 方案 A：完全不在 ECL 内提供性能能力

优点是实现成本最低，但玩家遇到卡顿时必须离开 ECL 使用外部工具，ECL 的诊断引擎也无法自然获得性能证据。

放弃原因：与 ADR 0033 的本地诊断方向不一致，也无法形成完整的“发现问题 → 捕获证据 → 分析原因”闭环。

### 方案 B：把 Spark 直接内置到 ECL Core

优点是用户体验直观，理论上可以统一版本。

放弃原因：会把第三方 profiler 的实现和生命周期耦合到 Core，增加版本、许可证、Minecraft Loader 兼容和维护成本；同时 profiler 的重型能力会污染普通启动路径。

### 方案 C：ECL 自行实现完整 JVM profiler

优点是完全控制数据模型和实现。

放弃原因：工程量巨大，且没有必要重复实现成熟的 sampling、allocation、flame graph 等能力。ECL 更应投入在跨数据源编排、诊断解释和用户体验上。

### 方案 D：采用本 ADR 的分层 Addition + 可替换 backend

基础 Addition 负责低开销持续健康监控，高级 Addition 负责按需 profiler，并将结果接入诊断引擎。该方案在功能价值、维护成本和架构边界之间取得更合理的平衡。

当前提案倾向方案 D，但在 Decision 仍为 `Proposed` 期间，不把该方案视为已经批准的 Core 架构约束。

## 后果

### 正面影响

- ECL 可以持续发现 Minecraft 性能退化和疑似卡死，而不要求用户手动打开外部工具。
- 普通玩家只安装基础监控即可，高级 profiler 不增加正常启动路径的复杂度。
- ECL 可以把性能证据与日志、世界和运行环境证据统一交给 Diagnostic Engine。
- 通过 profiler backend 抽象，ECL 不需要自己实现 JVM profiler，也避免永久绑定单一第三方工具。
- 客户端与服务端可以共享统一性能数据模型，同时允许各自声明不同的指标能力。
- 性能数据默认本地保存，符合 ECL 的本地优先和隐私边界。

### 代价与风险

- 游戏侧 telemetry 协议需要跨 Minecraft 版本和 Loader 维护兼容性。
- profiler backend 可能受第三方版本、许可证和 Minecraft Loader 生态影响。
- Hang Detection 存在误报和漏报风险，必须结合运行阶段与多个指标，而不能只依赖固定超时。
- 高级 profiler 仍可能对游戏产生额外开销，因此必须控制采样时长和触发条件。
- 性能数据与诊断知识结合后可能产生过度归因，需要保持证据与结论的明确区分。
- 如果未来增加远程分享，profile 可能包含实例、模组、类名、路径或其他环境信息，需要单独建立隐私和脱敏决策。

## 验收标准

本 ADR 在进入 `Accepted` 前，至少需要验证以下关键事实：

1. 基础监控可以在不启用重型 profiler 的情况下稳定监控 Minecraft 进程生命周期和系统资源。
2. 至少存在一种可靠的游戏侧数据源，可以为目标支持范围提供 TPS/MSPT 或等价 Tick 健康指标；无法覆盖的场景必须能明确声明能力缺失。
3. Hang Detection 能够区分至少启动阶段、正常运行、性能退化、短暂 Stall 和疑似 Hang，不以进程存在作为唯一健康判断。
4. 至少一个目标 Minecraft Loader / 版本组合能够通过受控方式启动和停止高级 profiler backend，并导入可解析的 profile 数据。
5. profiler backend 失败或不存在时，基础性能监控仍可独立运行。
6. profiler 结果能够进入本地 Diagnostic Engine，并保留原始证据与诊断结论之间的边界。
7. 性能数据默认不会自动上传；本地保存、清理和用户授权边界可以被测试或代码审查验证。
8. Performance Additions 的生命周期可以独立启动、停止和释放，不破坏 ECL 正常退出流程。
9. 第三方 profiler backend 的许可证、分发方式、目标版本兼容性和更新策略在正式实现前完成确认。
10. 至少完成一次真实 Minecraft 实例上的端到端验证：发现性能异常 → 捕获性能证据 → 生成本地诊断结果。

## 修订记录

- 2026-11-02：首次提出本 ADR，定义 ECL Performance Monitor、Performance Profiler 与可替换 profiler backend 的分层架构；当前保持 `Proposed + Not Started`，待进一步验证游戏侧 telemetry、Hang Detection 与第三方 profiler 集成边界。
- 2026-11-03：维护者指示按 ADR 0041–0049 实施，决策进入 `Accepted`。已实现进程/系统采样、Hang Detection 状态机、可替换 profiler backend 契约与本地样本日志；游戏侧 telemetry 采用可选 JSONL 协议，真实 profiler backend 与端到端实机验证仍未完成，故实施状态为 `In Progress`。证据见 [ADR 0041–0049 实施记录](../adr-0041-0049-implementation.md)。
