# ADR 0012：整合包归档提取安全边界

> 生命周期迁移说明：保留原决策及实施历史；本次仅将原状态拆分为独立字段，不改变既有技术决策或已完成验证。

- Decision: Accepted
- Implementation: Implemented
- Date: 2026-10-31
- Scope: ECL 架构与实现

## 背景

整合包导入会从用户提供的 ZIP 归档向实例的 `minecraft/` 目录写入文件。归档文件名本身属于不可信输入，因此仅依赖字符串形式的路径穿越检查并不足够：目标目录中可能已经存在符号链接或其他 reparse point，两个不同的归档路径也可能规范化到同一个目标。

同时，恶意归档可以通过大量条目、超大单文件或超大的累计解压数据消耗本地资源。

## 决策

`ModpackArchiveExtractor` 作为归档进入实例文件系统的安全边界，并执行以下检查：

1. 限制 ZIP 条目数量。
2. 拒绝绝对路径、空路径、NUL 字符以及 `.` / `..` 路径段。
3. 限制单个覆盖文件大小与累计解压大小。
4. 规范化后拒绝重复目标路径，避免同一目标被多个条目覆盖。
5. 创建目标目录前后检查 `FileAttributes.ReparsePoint`，拒绝符号链接或其他 reparse point 作为目标目录。
6. 覆盖已有文件前检查目标文件本身，不允许通过已有链接将写入重定向到实例目录之外。
7. 最终目标路径必须保持在指定游戏目录下。

安全检查集中在 extractor 内，调用方不需要自行重新实现路径校验。

## 后续工作

压缩比限制已加入 extractor：对超过 1 MiB 的条目检查单条压缩比（上限 500），累计解压超过 16 MiB 后检查总压缩比（上限 300）。阈值刻意保持宽松，避免把正常的大型配置/JSON 误判为 zip bomb；真实大型整合包仍需实机导入回归。


JSON / TOML metadata 的 4 MiB 上限已在后续实现中加入，见下文。

## 压缩比与 zip bomb 防护

`ModpackArchiveExtractor` 在原有条目数、单文件大小、累计大小、路径穿越与 reparse point 检查之外，增加压缩比检查：

- 单条：解压大小 ≥ 1 MiB 且 `uncompressed / compressed > 500` 时拒绝；
- 累计：覆盖文件累计解压 ≥ 16 MiB 且累计压缩比 > 300 时拒绝。

该检查只作用于实际参与覆盖提取的条目，不因归档中无关的大文件误伤；失败时不写入任何目标文件。

## Metadata entry limits

The Modrinth index and CurseForge manifest are read from ZIP entries and must not use an unbounded `ReadToEnd()`. Metadata entries are limited to 4 MiB. The implementation checks the declared uncompressed length and also enforces the same limit while streaming, so a malformed archive cannot bypass the limit by reporting an incorrect entry length.

## 修订记录

- 2026-10-31：建立整合包归档提取安全边界。
- 2026-11-03：实施状态更新为 Implemented。增加单条与累计压缩比限制，并加入正常提取、zip bomb、路径穿越与重复目标测试（ModpackArchiveExtractorTests）；真实大型整合包导入回归仍需手工验收。
