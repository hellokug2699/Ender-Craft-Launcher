# ADR 0004：分阶段启动流水线与 Dry Run

- 状态：已采纳
- 日期：2026-10-26

## 背景

启动链路会持续增长：安装、账户、Java、加载器、模组、EULA、日志、崩溃分析。单函数“拼参数 + 启动”不可测试、不可观测，也不利于逐步实现。

## 决策

1. 启动拆分为 `ILaunchStep` 有序流水线：Validate → ResolveVersion → ResolveJava → CheckFiles → ExtractNatives → EnsureEula → BuildCommand → Start。
2. 步骤通过 `LaunchContext` 共享状态，返回 `LaunchStepResult`（Success/Warning/Skipped/Failed + 消息 + 耗时）。
3. 提供 Dry Run：生成命令但不启动进程；缺失版本/Java 降级为警告。
4. 真实启动时缺失关键输入即失败，不伪造成功。
5. 进程启动抽象为 `IProcessLauncher`，测试使用假实现；真实实现使用 `ArgumentList` 避免引号问题。

## 后果

- 优点：可观测、可测试、可增量实现；安装能力可以“补数据”而不改启动器；UI/CLI 共用报告。
- 缺点：步骤之间需要约定上下文；步骤顺序需要维护。
- 风险：未来并行化步骤（如 Java 探测与版本读取）需要重新设计依赖关系；当前保持串行以确保诊断清晰。

## 备选方案

- 单函数启动：实现快，但不可测试、不可诊断。
- 直接依赖外部启动库：省事但失去控制与差异化空间。
