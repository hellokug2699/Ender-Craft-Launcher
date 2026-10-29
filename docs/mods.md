# Mod、整合包与资源包

v0.6 提供 Mod 元数据解析、问题检测、启停/导入，以及 Modrinth/CurseForge 整合包导入和资源包/光影包文件管理。v0.7 补全 CurseForge API：按实例版本/加载器搜索、一键安装（含必需依赖）与整合包远程文件下载。Mod 的实际加载仍由 Fabric/Forge/NeoForge/Cleanroom 负责，本模块只管理实例 `minecraft/` 目录中的文件与元数据。

## Mod 元数据

| 文件 | 平台 | 读取内容 |
| --- | --- | --- |
| `fabric.mod.json` | Fabric | `id`、`name`、`version`、`description`、`depends`、`breaks`/`conflicts` |
| `META-INF/mods.toml` | Forge | `[[mods]]`、`[[dependencies.<modid>]]` |
| `META-INF/neoforge.mods.toml` | NeoForge | 同上 |
| `mcmod.info` | Legacy Forge / Cleanroom 兼容 Mod | `modid`、`name`、`version`、`requiredMods` |

Forge 的 `${file.jarVersion}` 会尝试从 `META-INF/MANIFEST.MF` 的 `Implementation-Version` 补全。

## 扫描与问题检测

`ModService.ScanAsync` 扫描实例的 `minecraft/mods/`：

- 启用文件：`*.jar`
- 禁用文件：`*.jar.disabled`
- 检测：
  - 同一 `modId` 出现在多个文件（重复 Mod）；
  - 启用 Mod 的必需依赖缺失；
  - 必需依赖存在但被禁用；
  - `breaks` / `conflicts` 中的 Mod 已启用；
  - Forge/NeoForge/Fabric/Cleanroom 与 Mod 平台不匹配；
  - 无法解析元数据的 jar。

依赖中的平台内置项（`minecraft`、`forge`、`neoforge`、`fabricloader`、`fml`、`cleanroom` 等）会被忽略。版本范围目前只展示，不做严格语义匹配。

## 启用/禁用与导入

- 单个：`mod enable|disable <实例ID> <文件名>`
- 批量：`mod enable-all|disable-all <实例ID>`
- 导入：`mod import <实例ID> <文件...>`（复制 `*.jar` / `*.jar.disabled`，默认不覆盖）
- 删除：`mod delete <实例ID> <文件名>`

WPF 的 **Mods** 页面提供同样的列表、启停、批量、导入、删除、整合包导入导出操作；页面刷新/切换时会重新加载实例下拉，新建/删除实例后无需重启启动器。

## 整合包

### 导入

- **Modrinth `.mrpack`**：读取 `modrinth.index.json`，校验 `dependencies.minecraft` 与实例游戏版本、加载器是否匹配；按 `files[]` 的 `path`、`hashes.sha1`、`downloads`、`fileSize` 下载并校验；解压 `overrides/` 与 `client-overrides/`。`env.client = unsupported` 的文件会跳过。
- **CurseForge `.zip`**：读取 `manifest.json`，校验游戏版本与加载器（`forge-*` / `fabric-*` / `neoforge-*` / `cleanroom-*`），通过 `POST /v1/mods/files`（经 ECL 代理，客户端不含 key）批量解析 `files[].fileID` 并下载到 `mods/`，校验 SHA-1 与大小；`required=false` 的文件跳过并警告；最后解压 `overrides`（支持自定义目录名）与 `client-overrides/`。代理不可用时返回真实网络/HTTP 错误，不会导入残缺包；只有 overrides 的旧式包仍可导入。详见 [curseforge.md](curseforge.md)。
- 所有路径都会做目录穿越检查，只允许写入实例 `minecraft/` 目录。
- 导入完成后写入 `minecraft/.ecl-modpack.json` 记录包名/版本/来源（`modrinth` 或 `curseforge`）。

### 导出

`modpack export <实例ID> <输出.mrpack>` 生成 Modrinth 格式：

- `modrinth.index.json`：`formatVersion=1`、`game=minecraft`、`versionId`、`name`、`dependencies`，`files` 为空；
- `overrides/`：实例的 `mods/`、`config/`、`defaultconfigs/`、`resourcepacks/`、`shaderpacks/` 全部文件。

这是本地可再导入的完整包，不会提供远程下载 URL，因此不包含第三方分发链接。

## 资源包与光影包

扫描/导入/删除：

```powershell
ecl pack list <实例ID> [--type resource|shader] [--json]
ecl pack import <实例ID> <文件.zip> [--type resource|shader]
ecl pack delete <实例ID> <文件名> [--type resource|shader]
```

- 资源包读取 `pack.mcmeta` 的 `pack_format` 与 `description`。
- 光影包按 `shaderpacks/` 目录管理。
- WPF **Mods** 页面的“资源包 / 光影包”区域也提供类型选择、列表、导入与删除。
- 当前只管理文件，不修改 `options.txt` 中的“当前启用”选择。

## CurseForge 搜索与一键安装

- WPF **Mods** 页面的 CurseForge 区域按当前实例的游戏版本与加载器搜索；选中结果后可安装或打开项目页。
- CLI `curseforge search|files|install` 提供同样的能力，支持 `--json`。
- 默认安装所选文件的最新稳定版（没有稳定版才选 Beta/Alpha），并递归安装 `RequiredDependency`；`--no-dependencies` 可关闭。
- 安装记录写入 `minecraft/.ecl-curseforge.json`，用于去重与后续更新。
- 完整说明、API key 配置与限制见 [curseforge.md](curseforge.md)。

## CLI 示例

```powershell
ecl mod list survival-a1b2c3
ecl mod check survival-a1b2c3
ecl mod disable survival-a1b2c3 "example.jar"
ecl mod import survival-a1b2c3 "C:\mods\example.jar"
ecl modpack import survival-a1b2c3 "C:\packs\example.mrpack"
ecl modpack import survival-a1b2c3 "C:\packs\example.zip"
ecl modpack export survival-a1b2c3 "C:\packs\export.mrpack"
ecl curseforge status --check
ecl curseforge search jei --game-version 1.20.1 --loader forge
ecl curseforge install survival-a1b2c3 jei
ecl pack import survival-a1b2c3 "C:\packs\resource.zip"
```

## 限制

- CurseForge 作者禁止第三方分发的文件（`downloadUrl` 为空且下载端点无返回值）无法下载。
- 不做 Mod 更新检查、依赖版本范围严格语义匹配、可选依赖选择界面。
- 不解析 Mod 内部类/注册表，只读取元数据。
- 资源包/光影包的启用选择尚未写入 `options.txt`。
