# ADR 0002：元数据驱动的配置系统

- 状态：已采纳
- 日期：2026-10-26

## 背景

启动器配置项多、变化快，且需要全局默认 + 实例覆盖。传统“POCO + 手写设置页”会导致每加一个选项改模型、序列化、校验和 UI 多处，长期维护成本高。

## 决策

1. 每个选项声明为 `OptionDefinition<T>`：类型、默认值、作用域、分类、本地化键、范围、选项、可见性依赖、重启标记。
2. `OptionRegistry` 聚合所有 `IOptionProvider`，启动时检测重复 Key。
3. `SettingsService` 负责作用域解析、`JsonElement` 持久化、来源（Default/Global/Instance）与校验。
4. WPF 设置页根据 `OptionKind` 自动选择模板；CLI 自动提供 `settings list/get/set`。
5. 实例覆盖采用独立键 `instance.*`，空值表示继承。

## 后果

- 优点：新增选项只需声明定义 + 本地化；UI/CLI 自动同步；行为一致；易于测试。
- 缺点：定义层需要保持纪律（Key、说明、校验）；复杂控件仍需要新的 DataTemplate。
- 风险：定义与本地化键可能遗漏；后续增加“本地化键完整性测试”。

## 备选方案

- 强类型 POCO + 手写 UI：实现快，但扩展成本高。
- `Microsoft.Extensions.Configuration`：通用但缺少定义驱动 UI、来源解释与实例覆盖语义。
