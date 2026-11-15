# ADR 0071: Self-Contained Installer Executable and Embedded Deployment Media

- Decision: Accepted
- Implementation: In Progress
- Date: 2026-11-15
- Scope: Windows x64 / ARM64 首次安装介质、Installer 与 Bootstrapper 的单文件构建及人工下载入口
- Related: [ADR 0013: Branch-Based Versioning and Release Channels](0013-branch-based-versioning-and-release-channels.md)、[ADR 0025: Installer First-Launch Integrity Confirmation](0025-installer-first-launch-integrity-confirmation.md)、[ADR 0029: Installer, Bootstrapper and Uninstaller Lifecycle Boundaries](0029-installer-bootstrapper-uninstaller-lifecycle.md)、[ADR 0051: Unified Installation, Update and Recovery Architecture](0051-unified-installation-update-and-recovery-architecture.md)、[ADR 0072: Separation of Human-Facing Releases and Channel Update Feeds](0072-separation-of-human-facing-releases-and-channel-update-feeds.md)

## 背景

当前 Release 将 ECL 主程序 ZIP、Bootstrapper ZIP 和 Installer ZIP 并列发布。Installer ZIP 需要用户先手工解压，且 Installer 自身采用 framework-dependent 发布；同时独立下载的 Bootstrapper 与完整安装介质内的 Bootstrapper 不是同一种发布形态。这些差异让普通用户难以判断下载入口，也使“首次安装无需预先准备运行时”的恢复目标无法稳定成立。

ADR 0051 已规定 Installer 负责初始化首次安装介质并部署 Bootstrapper，实际签名验证、解包、逐文件校验和版本切换由 Bootstrapper 执行。本 ADR 只细化公开分发格式与构建约束，不改变该职责边界。

## 决策

1. **Installer 是普通用户唯一推荐的首次安装入口。** Release 直接发布自包含、单文件的 `ECL.Installer-<version>-<runtime>.exe`。用户下载后可直接运行，不需要先解压 ZIP，也不需要预先安装 .NET Desktop Runtime 才能运行 Installer。
2. **完整安装介质嵌入 Installer。** 构建先生成完整介质 ZIP，再将其作为 Installer 程序集资源嵌入。Installer 启动后将介质安全释放到用户临时目录，检查所需文件齐备，再进入既有安装 UI。不得依赖当前工作目录、安装器旁边的文件或用户手工解压。
3. **Installer 只负责释放与编排。** 它把 Bootstrapper 和公开信任材料复制到目标安装目录，并调用 Bootstrapper。Manifest 验签、包哈希校验、ZIP 安全解包、逐文件验证、staging 和 current 指针切换仍由 Bootstrapper 实现；Installer 不得复制这些逻辑。
4. **Bootstrapper 只有一种正式分发构建。** 发布为自包含单文件 EXE；独立 Bootstrapper 下载项与 Installer 内嵌介质必须使用同一个已构建二进制，不得分别发布 framework-dependent 与 self-contained 两套同名产物。
5. **Uninstaller 独立自包含。** 首次安装介质中的 Uninstaller 同样为单文件自包含程序，避免卸载流程依赖目标机运行时。
6. **完整 ECL 程序包按发布频道构建为自包含包。** 官方 CI 发布构建显式启用 `SelfContained`，确保在干净 Windows 环境中完成安装后，ECL 主程序及 LogHost 不再要求用户另行安装 .NET Desktop Runtime。开发者本地打包仍可显式选择 framework-dependent 产物，但不得将其作为官方 Release。
7. **Installer 完整性摘要通过 Release Notes 提供。** Installer 文件不能把自身 SHA-256 嵌入自身。官方 Release 不再发布 checksum sidecar；发布流水线必须在最终 EXE 签名和复制完成后计算 SHA-256，并将摘要写入同一 Release Notes。首次启动完整性向导要求用户从同版本官方 Release 页面复制摘要，并与 PowerShell 独立计算的本地 SHA-256 逐字符比较；未提供或不匹配时不得显示为已验证。用户仍可显式跳过，但必须明确显示未验证状态。
8. **人工 Release 页面严格只提供两个 EXE。** Installer EXE 是普通用户唯一推荐入口；独立 Bootstrapper EXE 仅供需要单独修复入口的高级用户。完整 ECL ZIP、Manifest、Signature、trust store 与其他更新材料不得作为 GitHub Release 附件；它们由 ADR 0072 定义的独立频道 Feed 提供。Release Notes 必须列出 Installer 与 Bootstrapper 的 SHA-256，并解释两者用途。
9. **频道更新 Feed 与人工 Release 分离。** `nightly`、`dev` 和 `main` 的版本与频道规则继续遵循 ADR 0013；人工 Release 只承载两个 EXE，日常更新所需 ECL Package 与签名 Feed 由 ADR 0072 管理。

## 备选方案

- **继续发布 Installer ZIP：**改动最少，但仍要求用户手工解压，且无法提供直观的单文件下载入口，因此不采用。
- **使用自解压 SFX 外壳并在外部放置介质：**仍可能产生多文件依赖，且无法保证用户从 Release 只下载一个文件即可启动，因此不采用。
- **让 Installer 自己实现签名验证和 ZIP 部署：**会与 Bootstrapper 形成两套部署实现，扩大安全审计面并破坏 ADR 0029 / 0051 的边界，因此拒绝。
- **Installer 启动时联网下载 Bootstrapper 和 ECL 包：**首次安装将依赖网络与远程可用性，不适合作为独立恢复入口，因此不采用。

## 后果

- 普通用户只需下载一个 Installer EXE；完整离线介质随 EXE 分发，不需要先手工解压。
- Installer、Bootstrapper、Uninstaller 和官方 ECL 包均可在未预装 .NET Runtime 的 Windows 环境中运行。
- Installer EXE 会明显增大，因为其中包含完整 ECL 安装包及自包含部署组件；这是独立安装能力的预期成本。
- Installer 临时释放介质会占用额外磁盘空间；安装完成或退出后应尽力清理，清理失败不得影响安装结果。
- Installer 的自身哈希不能内嵌，因此 Release Notes 必须提供独立摘要；摘要不再作为第三个下载资产，也不是 Manifest 签名的替代品。
- 官方发布体积增加，CI 构建时间也会增加；应通过构建日志和 Release 资产大小监控回归。

## 验收标准

1. 官方 Release 页面恰好提供 Installer 与 Bootstrapper 两个 EXE；不发布 ZIP、Manifest、Signature、trust store 或 checksum sidecar 附件。
2. 在未安装 .NET Desktop Runtime 的干净 Windows 10/11 x64 环境中，Installer 能启动并显示完整安装 UI。
3. Installer 可从嵌入资源释放完整介质；路径穿越、符号链接或缺失必要文件时必须拒绝继续。
4. 用户不手工解压、不预先放置 Bootstrapper 或 Manifest 文件，Installer 仍能完成介质检查并调用 Bootstrapper。
5. Installer 释放并部署的 Bootstrapper 与独立 Bootstrapper Release EXE 的 SHA-256 完全一致。
6. Installer 不实现 Manifest 验签、包解压和逐文件验证；篡改 Manifest、签名或包仍由 Bootstrapper fail-closed 拒绝。
7. 官方发布的 ECL 主程序、LogHost、Bootstrapper、Installer 和 Uninstaller 均为 self-contained；干净 Windows 环境完成安装后可以启动 ECL。
8. Release Notes 中列出的 Installer SHA-256 与最终发布 EXE 完全一致；向导只在用户输入匹配的 64 位摘要后启用“确认”。
9. 用户选择跳过完整性核对时，状态必须保持“未验证”，静默安装必须显式传入 `--accept-unverified`。
10. 安装、升级、回滚和卸载不删除或修改 Minecraft 实例、账户、配置及用户数据。
11. Release notes 清楚区分 Installer、ECL Package 和 Bootstrapper 的用途。
12. CI 完成构建、签名、打包、摘要生成和 Release 资产收集；构建失败不得更新滚动频道 Tag。

## 修订记录

- 2026-11-15：首次决策。统一 Installer 单文件分发、嵌入完整安装介质及 Bootstrapper 唯一自包含构建。
- 2026-11-15：依据 [ADR 0072](0072-separation-of-human-facing-releases-and-channel-update-feeds.md) 修订人工分发约束：Release 仅保留两个 EXE，摘要写入 Release Notes，自动更新 Feed 与人工 Release 分离。
