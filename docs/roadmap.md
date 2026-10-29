# 路线图

状态说明：`[x]` 已完成，`[ ]` 未开始，`[~]` 进行中。

## v0.1 架构骨架（已完成）

- [x] .NET 10 + WPF + 模块化单体 solution。
- [x] 应用路径、便携模式、数据目录布局。
- [x] 元数据驱动配置（全局/实例、校验、可见性、来源）。
- [x] 实例创建/列表/删除与目录隔离。
- [x] 离线账户与 Java 离线 UUID。
- [x] Java 版本解析与运行时探测。
- [x] 分阶段启动流水线 + Dry Run + 真实进程启动。
- [x] CLI（实例/账户/配置/启动/Java）。
- [x] 单元测试与 CI（Windows 全量、Linux Core）。
- [x] 架构、配置、启动、仓库策略文档。
- [x] 签名脚本与 public/private 仓库分离。

## v0.2 原版安装与真实启动（已完成，Java 匹配待 v0.4）

- [x] 版本清单（version_manifest_v2）获取与镜像。
- [x] 版本 JSON 下载、继承链合并。
- [x] client jar、libraries、assets、logging config 下载。
- [x] SHA-1 校验、断点续传、并发下载、失败重试。
- [x] natives 解压、library rules 按 OS/架构过滤。
- [x] 四档下载源策略（只官方 / 优先官方 / 优先镜像 / 只镜像）。
- [x] `eula.txt` 流程与首次启动 EULA 弹窗、多人联机免责声明。
- [x] CLI `install`、UI“安装/补全”、安装进度。
- [x] 真实启动冒烟测试：Linux 与 Windows 下均已完成安装、Dry Run 与进程拉起（Windows 使用 Java 8/21/25 验证）。
- [x] 版本 JSON `arguments` 规则对象与占位符：`arguments.jvm` / `arguments.game` 均按 OS/架构/feature 过滤，旧式 `minecraftArguments` 的 `${user_properties}` 等占位符已替换；demo/quick play 等未启用的 feature 参数会被安全过滤（对应交互功能仍未实现）。

## v0.3 账户体系（暂缓：Microsoft OAuth 审核限制）

- [ ] Microsoft OAuth 设备码/授权码流程。
- [ ] Xbox Live → XSTS → Minecraft Services 链路。
- [ ] 令牌刷新与失效处理。
- [ ] DPAPI 加密存储。
- [ ] authlib-injector 第三方认证。
- [ ] 账户管理 UI。

## v0.4 Java 管理（已完成）

- [x] 按 Minecraft 版本推荐 Java 主版本（版本 JSON + 版本号推断）。
- [x] 下载 Mojang Java 运行时（官方/镜像，四档下载源策略）。
- [x] SHA-1 校验、续传、并发、链接与可执行位处理。
- [x] 每实例 Java 选择器 + 自动选择。
- [x] Java 兼容性检查、启动前弹窗、实例级关闭开关。
- [x] 无 Java 时提示并支持一键下载。
- [x] Adoptium / Zulu / Liberica 等第三方运行时（API 解析、zip/tar.gz 解压、SHA 校验）。
- [x] 32/64 位与内存需求校验（选择/启动警告，不强制阻止）。
- [x] JVM 参数预设（G1、ZGC、Aikar flags；全局与实例覆盖）。

## v0.5 模组加载器（已完成）

- [x] Fabric 安装：Meta profile JSON、Fabric API 强制安装、版本选择与更新时间。
- [x] Forge 安装：Maven 版本目录、spec 0 直接解压、spec 1+ 运行安装器 processors。
- [x] NeoForge 安装：1.20.1 旧产物与 1.20.2+/26.x 版本前缀映射、processors。
- [x] Cleanroom 安装：1.12.2 安装器内容解析、版本 JSON 提取、库仓库映射、Java 25 展示。
- [x] OptiFine 显式安装：仅 Forge / Cleanroom 可选，默认不安装，遵守分发限制。
- [x] 加载器 / Fabric API / OptiFine 版本列表、更新时间标记、CLI `loader list`。
- [x] Fabric profile `arguments.jvm`、Forge/NeoForge `library_directory`/`--launchTarget` 与 Cleanroom `--tweakClass` 启动参数。
- [x] Maven Central → 阿里云公共仓库镜像回退；Forge/NeoForge Maven → BMCLAPI `/maven/`。
- [ ] Quilt、Legacy Fabric 等加载器（明确不在当前范围）。
- [x] 安装失败诊断与回滚：记录本次安装新建的文件，失败时清理并写入诊断 JSON。

## v0.6 Mod 与整合包（进行中）

- [x] `fabric.mod.json`、`mods.toml`、`neoforge.mods.toml`、`mcmod.info` 解析。
- [x] 依赖/冲突/重复 Mod/加载器不匹配检测（版本范围只展示，不做严格语义匹配）。
- [x] 启用/禁用、批量操作、导入、删除（WPF Mods 页面 + CLI）。
- [x] CurseForge / Modrinth 整合包导入：Modrinth `.mrpack` 与 CurseForge `.zip` 均完整下载远程文件、校验并解压 overrides。
- [x] CurseForge 搜索、文件列表与一键安装（WPF + CLI，递归安装必需依赖）。
- [x] CurseForge API key 的环境变量/发布版注入、缓存、限流与错误映射。
- [x] 整合包导出（Modrinth `.mrpack`，本地 overrides 形式）。
- [~] 资源包、光影包管理：列表/导入/删除已完成（WPF + CLI）；`options.txt` 启用选择待接入。
- [ ] Mod 依赖版本范围的严格语义匹配。
- [ ] Mod 更新检查与 CurseForge 可选依赖选择。

## v0.7 体验与诊断（进行中，首次启动向导暂缓）

- [x] 浅色/深色主题切换（运行时即时生效）。
- [x] 完整中英文覆盖（键完整性测试）与明确的语言重启提示；语言热切换未做。
- [ ] 首次启动向导（本阶段明确暂缓）。
- [x] 崩溃报告解析与 Mod 嫌疑排序（`crash` CLI + 实例页）。
- [x] 日志导出/诊断包（脱敏 zip，CLI `diagnostics export` + 实例页）。
- [x] 下载速度、进度、暂停/恢复 UI（安装与 Java 下载）。
- [~] 键盘导航与无障碍：Ctrl+1..4 页面切换、Ctrl+S 保存、F5 刷新、Tab 循环与导航项自动化名称已完成；更完整的焦点/读屏细节待后续。
- [x] 用户自定义 / 导入 JVM 预设（JSON 文件，全局与实例覆盖，禁止覆盖 -Xmx/-Xms/-cp）。

## v1.0 分发与更新

- [ ] 安装包（MSIX 或 Inno Setup）。
- [ ] 启动器自更新（稳定/测试/开发通道）。
- [ ] 更新包签名验证。
- [ ] 增量更新。
- [ ] 版本公告与迁移。
- [ ] 崩溃/遥测上传（可选、明确同意）。

## 跨阶段事项

- [ ] 每个模块的测试覆盖目标（核心链路 ≥80%）。
- [ ] 性能基线：冷启动、实例列表、启动准备。
- [ ] 依赖审计与 SBOM。
- [ ] 本地化键完整性测试。
- [ ] CI 增加 `dotnet format` 与静态分析。
- [ ] 文档随架构同步更新。
