# ADR 0007：加载器安装与组件分发

- 状态：已接受
- 日期：2026-10-27
- 相关版本：v0.5

## 背景

v0.5 需要支持 Fabric 与 1.12.2 的 Cleanroom，并处理 Fabric API、OptiFine 的可选组件。要求包括：

- 1.12.2 的 Cleanroom 下载、安装、分析与显示。
- 不引入 Quilt、Legacy Fabric 等加载器。
- OptiFine 有分发限制，不得自动安装，只有用户显式选择时才安装。
- Fabric 必须安装 Fabric API，默认最新兼容版本，并显示横幅提示。
- 不支持 OptiFabric；Fabric 与原版下禁用 OptiFine。
- 版本列表需显示更新时间。

## 决策

### 1. 使用官方 profile / 安装器内容，而不是运行安装器

- Fabric 使用 Meta 的 `profile/json`，直接得到版本 JSON、库与 JVM 参数。
- Cleanroom 使用安装器 zip 中的 `version.json` 与 `install_profile.json`，补全空 URL 后按普通版本安装。
- **不执行** Cleanroom 安装器的 GUI 或 CLI 进程，避免交互、额外目录与不可控副作用。
- Forge/NeoForge 的 processors 属于例外，见 ADR 0008。

### 2. 加载器版本 ID 与文件布局

- 实例版本 ID 由 `<游戏版本>-<加载器>-<加载器版本>` 组成。
- 客户端 jar、版本 JSON 都放在该版本目录；原版版本 JSON 作为 Fabric 的父版本保留在已有位置。
- 加载器库与组件仍写入共享 `libraries/`，组件 jar 写入实例 `mods/`。

### 3. Fabric API 强制安装

- 选择 Fabric 即安装 Fabric API；UI 与 CLI 默认选择该游戏版本的最新 API。
- 允许用户改选具体 API 版本，但不提供“不安装”选项。

### 4. OptiFine 显式选择

- OptiFine 仅在用户显式选择时进入安装流程；当前允许 Forge 与 Cleanroom，原版、Fabric、NeoForge 不提供该选项。
- 默认值为“不安装”；原版、Fabric、NeoForge 下 UI 禁用选项，服务端再次校验。
- 下载源使用 BMCLAPI 的 OptiFine 列表与文件接口；ECL 不打包 OptiFine。

### 5. 更新时间来源

- Fabric / Cleanroom：GitHub releases Atom；仅最近发布有日期，其余显示“未知”。
- Fabric API：Modrinth `date_published`。
- OptiFine：BMCLAPI 记录 ObjectId 的时间戳。

### 6. Maven Central 镜像

- Cleanroom 与部分加载器依赖 Maven Central。下载源策略将 `repo.maven.apache.org` / `repo1.maven.org` 映射到阿里云公共仓库，避免 Maven Central 限流导致安装失败。
- BMCLAPI 未覆盖的第三方地址（Fabric Maven、Cleanroom Maven、Modrinth、BMCLAPI）在镜像模式下直接使用原始地址。

## 后果

- 不依赖安装器进程，安装流程可测试、可重试。
- 版本列表与安装都依赖第三方公开接口；接口失败时给出明确错误，不回退到自动安装 OptiFine。
- 旧版本加载器的更新时间可能显示未知，但不影响安装。
- Forge/NeoForge 的安装器 processors 由 ADR 0008 单独定义；Quilt、Legacy Fabric 仍不支持。
