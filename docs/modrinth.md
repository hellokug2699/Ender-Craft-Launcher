# Modrinth 集成

v0.8 接入 Modrinth v2 API，与 CurseForge 一起构成“下载”页的两个内容来源。实现位于 `ECL.Minecraft/Modrinth/` 与 `ECL.Minecraft/Content/`，WPF 与 CLI 共用同一套服务。

## 1. 请求与安全

- 基础地址：`https://api.modrinth.com/v2`，请求带 `User-Agent: EnderCraftLauncher/<版本>`（Modrinth 要求可识别的 UA）。
- 单请求 30 秒超时；404 → `modrinth.not_found`，429 读取 `Retry-After` 后重试，5xx/网络错误指数退避，最多 3 次；JSON 解析失败 → `modrinth.invalid_response`。
- 文件下载只接受 `https://modrinth.com` 或 `*.modrinth.com` 地址，避免 API 响应被滥用为任意 URL。
- 不做磁盘缓存，只缓存进程内不必要的数据；搜索结果与版本信息按需请求。

## 2. 搜索

`ModrinthClient.SearchAsync` 使用 `facets` 组合过滤：

```text
[["project_type:mod"],["versions:1.21.1"],["categories:fabric"]]
```

| 启动器类型 | `project_type` |
| --- | --- |
| Mod | `mod` |
| 整合包 | `modpack` |
| 资源包 | `resourcepack` |
| 光影包 | `shader` |
| 数据包 | `datapack` |

加载器放在 `categories`（Fabric/Forge/NeoForge/Quilt）；资源包、光影包、数据包不按加载器过滤。没有实例时可以只按关键词搜索，版本/加载器留空。

## 3. 统一搜索

`ContentSearchService` 把两个平台映射成统一的 `ContentHit`：

- `ContentSourceFilter.All`：两个平台并行请求，结果交错排列；同名项目分别保留，用来源徽标区分，不跨平台去重。
- `CurseForge` / `Modrinth`：只请求选定平台。
- 一个平台失败时保留另一个平台的结果并返回警告；两个都失败才返回错误。

## 4. 安装与下载

`ContentInstallService` 负责落盘：

| 类型 | 目标目录 |
| --- | --- |
| Mod | `mods/` |
| 资源包 | `resourcepacks/` |
| 光影包 | `shaderpacks/` |
| 数据包 | `datapacks/`（作为暂存目录，正式使用需放入具体存档） |
| 整合包 | 先下载到 `<data>/downloads/modpacks/`，再导入实例 |

- 没有实例时，文件下载到 `<data>/downloads/<类型>/`；Modrinth Mod 会一并解析必需依赖。
- 有实例时，先按实例游戏版本与加载器选择版本；Modrinth 依赖中 `dependency_type=required` 的项目递归补齐，`embedded` / `optional` 不自动安装。
- 文件校验优先使用 Modrinth 提供的 SHA-1；没有 SHA-1 时仍校验大小与下载结果。
- CurseForge Mod 复用 `CurseForgeService` 的依赖解析与安装记录；其他类型使用统一文件解析。

## 5. 整合包

1. 解析版本中的主文件（通常是 `.mrpack`），下载到 `<data>/downloads/modpacks/`。
2. 有目标实例时直接调用 `IModpackService.ImportAsync`。
3. 没有实例时读取 `modrinth.index.json` 的 `dependencies`（`minecraft`、`fabric-loader`、`forge`、`neoforge`）或 CurseForge `manifest.json` 的 `modLoaders`，自动创建实例后再导入。
4. 导入后仍需在“下载 → 游戏实例”中安装游戏/加载器文件，才会生成可启动的版本。

## 6. 与 CurseForge 的差异

| 能力 | CurseForge | Modrinth |
| --- | --- | --- |
| API 认证 | ECL 代理注入 key | 公开 API，无需 key |
| 搜索过滤 | `classId` + `gameVersion` + `modLoaderType` | `facets` |
| 文件 URL | 只接受 `*.forgecdn.net` / `*.curseforge.com` | 只接受 `*.modrinth.com` |
| 依赖 | `relationType=3` 递归 | `dependency_type=required` 递归 |
| 分发限制 | 作者可禁止第三方分发，此时返回明确错误 | 文件由 Modrinth CDN 提供 |
