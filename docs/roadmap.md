# 路线图

状态说明：`[x]` 已完成，`[ ]` 未开始，`[~]` 进行中。

## v0.1 架构骨架（当前）

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

## v0.2 原版安装与真实启动

- [ ] 版本清单（version_manifest_v2）获取与镜像。
- [ ] 版本 JSON 下载、继承链合并。
- [ ] client jar、libraries、assets、logging config 下载。
- [ ] 哈希校验、断点续传、并发下载、失败重试。
- [ ] natives 解压、library rules 过滤。
- [ ] 参数占位符完整替换。
- [ ] `eula.txt` 流程与启动前检查。
- [ ] 真实启动冒烟测试（Windows CI + 手动）。

## v0.3 账户体系

- [ ] Microsoft OAuth 设备码/授权码流程。
- [ ] Xbox Live → XSTS → Minecraft Services 链路。
- [ ] 令牌刷新与失效处理。
- [ ] DPAPI 加密存储。
- [ ] authlib-injector 第三方认证。
- [ ] 账户管理 UI。

## v0.4 Java 管理

- [ ] 按 Minecraft 版本推荐 Java 主版本。
- [ ] 下载 Mojang / Adoptium / 其他发行版运行时。
- [ ] 校验、解压、版本目录管理。
- [ ] 每实例 Java 选择 UI。
- [ ] JVM 参数预设（G1、ZGC、Aikar flags）。

## v0.5 模组加载器

- [ ] Fabric / Quilt 安装。
- [ ] Forge / NeoForge installer processors 与库补丁。
- [ ] OptiFine 兼容流程。
- [ ] 加载器版本选择与兼容性提示。
- [ ] 安装失败诊断与回滚。

## v0.6 Mod 与整合包

- [ ] `fabric.mod.json`、`mods.toml`、`mcmod.info` 解析。
- [ ] 依赖/冲突/重复 Mod 检测。
- [ ] 启用/禁用、批量操作。
- [ ] CurseForge / Modrinth 整合包导入。
- [ ] 整合包导出。
- [ ] 资源包、光影包管理。

## v0.7 体验与诊断

- [ ] 浅色/深色主题切换。
- [ ] 完整中英文覆盖与语言热切换（或明确重启提示）。
- [ ] 首次启动向导。
- [ ] 崩溃报告解析与 Mod 嫌疑排序。
- [ ] 日志导出/诊断包（脱敏）。
- [ ] 下载速度、进度、暂停/恢复 UI。
- [ ] 键盘导航与无障碍。

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
