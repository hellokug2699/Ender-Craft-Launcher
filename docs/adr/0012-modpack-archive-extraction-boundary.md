# ADR 0012：整合包归档提取安全边界

- 状态：Accepted
- 日期：2026-10-31

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

当前边界仍有两个值得继续处理的方向：


- 评估压缩比与压缩数据大小限制，进一步降低 zip bomb 风险。

JSON / TOML metadata 的 4 MiB 上限已在后续实现中加入，见下文；当前仍需评估压缩比限制，且不能通过过严的经验阈值破坏正常大型整合包。


## Metadata entry limits

The Modrinth index and CurseForge manifest are read from ZIP entries and must not use an unbounded `ReadToEnd()`. Metadata entries are limited to 4 MiB. The implementation checks the declared uncompressed length and also enforces the same limit while streaming, so a malformed archive cannot bypass the limit by reporting an incorrect entry length.
