# 配置系统

配置是 ECL 的差异化重点。目标不是“写一堆 POCO 再手写设置页”，而是：

1. 每个选项只声明一次。
2. 校验、默认值、作用域、可见性、本地化都由定义派生。
3. UI 自动生成设置页，CLI 自动获得 `settings list/get/set`。
4. 全局设置与实例覆盖天然分层，来源可解释。

## 1. 模型

### 选项定义

`OptionDefinition` 是一个选项的元数据，`OptionDefinition<T>` 提供强类型默认值与校验。

| 字段 | 含义 |
| --- | --- |
| `Key` | 稳定唯一键，例如 `java.defaultMemoryMb` |
| `Kind` | 控件类型：Boolean / Integer / Decimal / Text / Select / Path / Color / KeyBinding |
| `Scope` | `Global` 或 `Instance` |
| `CategoryKey` | 分组，用于设置页分块 |
| `DisplayNameKey` / `DescriptionKey` | 本地化键 |
| `UnitKey` | 单位键，例如 `unit.mb` |
| `Default` | 强类型默认值；可空类型允许 `null` |
| `Min` / `Max` / `Step` | 数值约束与步长 |
| `Choices` | Select 的可选值及显示键 |
| `RequiresRestart` | 修改后是否需要重启 |
| `IsSecret` | 敏感值，不应写日志/诊断包 |
| `EmptyIsNull` | 空字符串是否表示“未设置/继承” |
| `VisibleWhenKey` / `VisibleWhenValue` | 依赖另一个选项决定是否显示 |
| `Validate` | 额外的业务校验委托 |
| `Order` | 同分类内排序 |

### 注册

模块通过 `IOptionProvider` 贡献定义：

```csharp
internal sealed class MyOptionsProvider : IOptionProvider
{
    public IEnumerable<OptionDefinition> GetOptions()
    {
        yield return new OptionDefinition<int>
        {
            Key = "my.feature.limit",
            Kind = OptionKind.Integer,
            Scope = OptionScope.Global,
            CategoryKey = "category.my",
            DisplayNameKey = "option.my.limit.name",
            Default = 10,
            Min = 1,
            Max = 100,
            Order = 10,
        };
    }
}
```

`OptionRegistry` 在容器构建后聚合所有 provider。Key 重复会在启动时抛异常，避免静默覆盖。

## 2. 作用域与分层

### Global

- 存储在 `<data>/config/global.json`。
- 影响所有实例。
- 全局选项不能写入实例作用域。

### Instance

- 存储在 `<data>/instances/<id>/settings.json`。
- 只对当前实例生效。
- 空值表示“未设置/继承”，不会写入文件。

当前实例选项包括：

| Key | 含义 |
| --- | --- |
| `instance.java.memoryMb` | 覆盖全局最大内存；留空继承 |
| `instance.java.executablePath` | 覆盖全局 Java 路径；留空继承 |
| `instance.java.extraArguments` | 覆盖全局 JVM 参数；留空继承 |
| `instance.game.extraArguments` | 覆盖全局游戏参数；留空继承 |

启动流水线读取实例值时使用 `ISettingsService.Get<T>(key, instanceId)`，为 `null` 时回退全局值。例如：

```csharp
var memory = settings.Get<int?>("instance.java.memoryMb", instance.Id)
             ?? settings.Get<int>("java.defaultMemoryMb");
```

### 来源

`GetSource` 返回：

- `Default`：使用定义中的默认值。
- `Global`：全局文件中存在显式值。
- `Instance`：实例文件中存在显式值。

UI 在每项下方展示“来源”，实例作用域下 `Default` 显示为“继承全局”，帮助用户理解优先级。

## 3. 文件格式

`config/global.json`：

```json
{
  "schemaVersion": 1,
  "values": {
    "java.defaultMemoryMb": 8192,
    "network.proxyMode": "manual",
    "network.proxyAddress": "http://127.0.0.1:7890"
  }
}
```

`instances/survival-a1b2c3/settings.json`：

```json
{
  "schemaVersion": 1,
  "values": {
    "instance.java.memoryMb": 6144
  }
}
```

- 未显式设置的选项不会出现在 `values` 中，因此默认值可以随版本升级。
- 写入使用“临时文件 + 覆盖移动”，避免半写文件。
- 读取失败（JSON 损坏、权限不足）会记录警告并回退默认值。
- `schemaVersion` 已预留；迁移逻辑在第一次破坏性变更时加入。

## 4. 校验与可见性

### 校验顺序

1. 作用域校验（全局/实例是否匹配）。
2. 类型转换（布尔支持 `true/false/1/0/on/off/yes/no`）。
3. 范围校验（`Min/Max`）。
4. Select 值必须在 `Choices` 中。
5. 自定义 `Validate`。
6. 序列化为 `JsonElement` 并持久化。

失败时返回人类可读错误，不写入文件。

### 可见性

```csharp
yield return new OptionDefinition<string>
{
    Key = "network.proxyAddress",
    Kind = OptionKind.Text,
    Scope = OptionScope.Global,
    CategoryKey = "category.network",
    DisplayNameKey = "option.network.proxyAddress.name",
    Default = string.Empty,
    VisibleWhenKey = "network.proxyMode",
    VisibleWhenValue = "manual",
};
```

- 设置页根据编辑中的值实时刷新可见性，而不是等到保存。
- 被隐藏的选项不会在保存时写入。

## 5. UI 自动生成

`SettingsView` 使用 `ItemsControl` + `OptionTemplateSelector`：

- 按 `CategoryKey` 分组，按 `Order` 排序。
- 按 `Kind` 选择 DataTemplate：布尔用 CheckBox，数值用 TextBox + 单位，文本用 TextBox，路径用 TextBox + 浏览按钮，Select 用 ComboBox。
- 每项展示名称、描述、来源、重启标记、继承提示。
- 保存时逐项调用 `TrySetRaw`，任一失败则停止并提示。

因此新增一个选项通常只需要：

1. 新增 `OptionDefinition<T>`。
2. 补 JSON 本地化键。
3. 如果 `Kind` 是新的，补一个 DataTemplate 和 selector 映射。

## 6. CLI

```bash
ecl settings list
ecl settings list --instance survival-a1b2c3
ecl settings get java.defaultMemoryMb
ecl settings set java.defaultMemoryMb 8192
ecl settings set instance.java.memoryMb 6144 --instance survival-a1b2c3
ecl settings clear instance.java.memoryMb --instance survival-a1b2c3
ecl settings list --json
```

- `--json` 输出 `key/scope/value/source/requiresRestart/kind`，便于脚本化。
- 实例命令会先校验实例存在。

## 7. 敏感配置

- 密码、令牌、证书口令**不应**作为普通配置项存储。
- 若未来需要存储令牌，使用单独的加密存储（DPAPI），并通过 `IsSecret` 防止日志泄漏。
- 代码签名证书、私钥、OAuth 机密只放 private Core 仓库，详见 [repository-policy.md](repository-policy.md)。

## 8. 设计取舍

- **为什么不用 `Microsoft.Extensions.Configuration`？** 启动器需要“定义驱动 UI”、实例覆盖、来源解释，这些不是通用配置系统的强项。我们仍使用 `Microsoft.Extensions.Logging/DI`，但配置层自建。
- **为什么值存 JSON 而不是全字符串？** 保留类型信息，便于诊断和后续迁移；UI 通过 `Format` 统一转字符串。
- **为什么设置写入是同步的？** 文件极小、频率低；同步原子写更简单，避免后台写入竞争与丢失。
- **为什么实例覆盖不使用“同一个 Key + 两层值”？** 当前实例选项独立命名（`instance.*`），语义清晰；未来若需要“同 Key 全局默认 + 实例覆盖”，可在 `SettingsService` 中扩展解析顺序。
