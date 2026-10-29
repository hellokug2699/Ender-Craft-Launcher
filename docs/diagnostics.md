# 崩溃分析与诊断包

v0.7 增加面向实例的崩溃分析与脱敏诊断包导出。两者都只读取本地文件，不启动游戏。

## 崩溃分析

`CrashReportService` 按以下顺序查找：

1. `minecraft/crash-reports/*.txt` 中最后修改的崩溃报告；
2. `minecraft/logs/latest.log`（包含 `Minecraft Crash Report` 或 `Exception` 时）。

解析内容：

- `Description:`、`Minecraft Version:`、`Java Version:`；
- `Fabric Mods:` / `Mod List:` / `Loaded Mods:` 列表；
- Mod 元数据中的 ID/名称；
- 异常、`Caused by`、`at` 栈帧与 `Error`/`Exception` 行。

嫌疑排序是“ID/名称出现次数 + 栈帧证据加分”，截断前 5 个；这是启发式结果，不保证与真实根因一致。

### UI

实例页“崩溃与诊断”区域：

- **分析最近崩溃**：显示摘要、来源、可疑 Mod 与关键日志行；
- **导出诊断包**：写入 `<data>/logs/diagnostics/ecl-diagnostics-<实例ID>-<时间>.zip`，并在界面显示路径。

### CLI

```powershell
ecl crash <实例ID> [--json]
ecl diagnostics export <实例ID> [--output <路径>] [--json]
```

## 诊断包内容

- `manifest.json`：时间、启动器版本、实例元数据、OS/.NET、Java 运行时；
- `instance.json`、实例 `settings.json`、全局 `config/global.json`；
- 最近 5 个崩溃报告、最近 5 个 Minecraft 日志、最近 5 个实例日志；
- 该实例最近 5 个安装失败诊断；
- 最近 3 个启动器日志（`logs/ecl-*.log`）。

**不包含** `accounts/accounts.json`。所有文本在写入 zip 前经过 `DiagnosticPackageService.Redact`：

- `accessToken` / `refreshToken` / `clientSecret` / `authorization` / `token` 键值；
- `Bearer ...`；
- `--accessToken ...`；
- URL 中的 `user:pass@`。

单个文本文件最多读取 2 MB，避免诊断包过大。

## 限制

- 不是符号化崩溃解析，也不上传任何数据；
- 崩溃分析依赖日志内容，日志被截断或 Mod 未列出时可能无法定位；
- 资源包/光影包的启用状态由 `options.txt` 决定，诊断包只收集日志与安装信息，不修改游戏配置。
