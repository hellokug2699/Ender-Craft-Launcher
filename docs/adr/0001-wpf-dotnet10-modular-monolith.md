# ADR 0001：WPF + .NET 10 + 模块化单体

- Status: Completed
- Date: 2026-10-26
- Scope: ECL 架构与实现

## 背景

启动器面向 Windows，需要现代桌面 UI、进程管理、文件与网络能力，并计划达到 HMCL/PCL2 的功能体量。团队规模与发布形态不支持多进程/微服务。

## 决策

1. UI 使用 **WPF**，目标 **.NET 10 LTS**。
2. 采用 **模块化单体**：单个进程与发布物，按职责拆分项目。
3. Core 与 CLI 不依赖 WPF，可在 Linux 构建与测试；WPF 项目通过 `EnableWindowsTargeting` 交叉编译验证。
4. 使用 `Microsoft.Extensions.DependencyInjection` + `Microsoft.Extensions.Logging` + Serilog。

## 后果

- 优点：Windows 原生、生态成熟、部署简单；业务逻辑可测试；后续可替换 UI。
- 缺点：UI 只能在 Windows 运行；跨平台需要重写 App 层或引入 Avalonia（未选择）。
- 约束：模块之间保持单向依赖；UI 只做展示与编排。

## 备选方案

- Avalonia：跨平台更好，但当前目标仅 Windows，且 WPF 的 Windows 集成更直接。
- WinUI 3：现代观感更好，但打包/部署与控件定制成本更高。
- 多进程架构：隔离性好，但对启动器而言复杂度过高。
