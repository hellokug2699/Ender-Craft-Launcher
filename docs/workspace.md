# Minecraft 工作区与存储边界

本页描述 ADR 0018 的落地实现：ECL 的四类存储、首次启动的工作区确认、路径校验、外部实例与迁移。

## 1. 四类存储

| 类别 | 默认位置 | 说明 |
|---|---|---|
| 程序安装目录 | `%LOCALAPPDATA%\Programs\Ender Craft Launcher` | Bootstrapper、版本目录、部署状态；由安装器选择 |
| ECL 用户数据 | `%LOCALAPPDATA%\EnderCraftLauncher` | 配置、账户、日志、缓存；`--data-dir` 可覆盖 |
| 受管理 Java 运行时 | `<data>/Runtimes` | 旧版 `<data>/java` 只读兼容 |
| Minecraft 工作区 | 首次启动由用户确认 | 实例、版本、libraries、assets 与下载内容 |

`StorageLayout`（`Ecl.Core.Storage`）把四者作为独立字段暴露，避免在代码与文档里混用“数据目录”和“游戏目录”。工作区不得位于程序目录、系统目录、`ProgramData` 或 ECL 用户数据目录内；位于 Application Data 下的显式选择会给出警告而不是默认推荐。

## 2. 工作区配置

持久化在 `<data>/config/workspace.json`：

```json
{
  "schemaVersion": 1,
  "primaryId": "ws-1a2b3c4d5e6f",
  "roots": [
    {
      "id": "ws-1a2b3c4d5e6f",
      "name": "ECL",
      "path": "<工作区绝对路径>",
      "kind": "managed",
      "status": "ready",
      "lastValidatedAtUtc": "2026-11-01T00:00:00+00:00"
    }
  ],
  "onboardingCompletedAtUtc": "2026-11-01T00:00:00+00:00",
  "onboardingSkipped": false
}
```

- `id` 由规范化路径派生，稳定且与显示名称无关。
- `roots` 是列表，为后续多根目录留出扩展空间；`primaryId` 指向当前主工作区。
- 写入使用同目录临时文件 + 原子替换；损坏或更高 schema 版本的配置会被报告，但不会删除原文件。
- 主工作区不可用时保留登记，状态改为 `missing`，绝不自动创建空目录冒充原工作区。

## 3. 首次启动

1. 启动时先由 `WorkspaceBootstrap` 应用已保存的主工作区；旧版 `launcher.minecraftDirectory` 只迁移一次。
2. 未确认时弹出工作区引导（`WorkspaceSetupWindow`），显示推荐位置、完整路径与可用性说明。
3. 推荐顺序：用户可见的非系统固定磁盘（`<盘符>:\Games\ECL`）→ 文档目录 `Games\ECL`；没有合适磁盘时只给回退建议并要求确认。
4. 引导会展示可移动盘、网络位置、系统盘、空间不足与 AppData 警告。
5. 确认前不会创建工作区目录、不会开始下载；选择“稍后配置”后仍可浏览，但创建实例/安装/启动前会再次要求确认。

## 4. 路径校验

`WorkspacePathValidator.Check` 返回结构化结果：`ready`、`missing`、`notWritable`、`invalid`，并附带剩余空间与警告码。

- 新建选择允许创建缺失目录；已登记工作区校验使用“必须存在”模式。
- 写入检查通过创建并删除探针文件完成，不修改用户数据。
- 路径必须为绝对路径；拒绝文件占位、无效字符与目标磁盘不可用。
- 拒绝包含路径穿越与符号链接逃逸（比较规范化后的完整路径边界）。

## 5. CLI

```powershell
ecl workspace status [--json]      # 配置、状态、可用空间与存储边界
ecl workspace suggest [--json]     # 推荐位置
ecl workspace set <目录>            # 确认工作区（创建结构并写入配置）
ecl workspace validate [--json]    # 校验当前工作区，缺失时退出码 1
ecl workspace roots [--json]       # 已登记根目录
ecl workspace adopt-legacy         # 仅在检测到旧数据时继续使用数据目录
ecl workspace skip                 # 稍后配置
```

`instance create`、`install`、`launch` 与 `modpack import` 在未确认工作区时直接失败，并提示先运行 `ecl workspace set`。

## 6. 外部实例

外部实例（其它启动器的 `.minecraft` 或版本隔离目录）只登记引用，不复制、不移动、不重命名、不清理源目录。`instance import` 与 `instance scan` 继续使用实例实际游戏目录；工作区列表中的外部根目录来自 `workspace roots`，用于展示与后续迁移判断。

## 7. 生命周期保护

- 更新、回滚、修复只操作程序安装目录与版本目录，不扫描工作区。
- 卸载程序不删除配置、账户、日志、运行时或工作区。
- 工作区离线（移动盘拔出、网络中断、盘符变化）时保留登记并提示重新连接或重新定位。

## 8. 尚未完成

- 工作区迁移状态机与向导（复制 → 校验 → 原子切换 → 保留源）尚未实现。
- 多工作区根目录的 UI 管理与按实例归属仍在后续阶段。
