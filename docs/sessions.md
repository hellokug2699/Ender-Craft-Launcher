# 游戏日志会话与诊断导出

本页描述 ADR 0015 的实现：按进程隔离的日志会话、崩溃证据、恢复与按会话导出。

## 1. 会话身份

- 每次游戏启动生成唯一且不可变的 `SessionId`（形如 `20261102-004512-a1b2c3`）。
- 会话不等于实例、PID、`latest.log` 或崩溃报告；同一实例的多次启动、并发启动互不覆盖。
- 会话存储在 `<data>/logs/sessions/<sessionId>/`：

```text
logs/sessions/<sessionId>/
├─ manifest.json      # 会话元数据与证据清单（原子写入）
├─ events.jsonl       # 带序号的追加式生命周期事件
├─ game.stdout.log    # 进程标准输出（有界队列落盘）
├─ game.stderr.log    # 进程标准错误
└─ artifacts/         # 崩溃报告、hs_err、共享日志快照等副本
```

## 2. 生命周期

```text
Preparing → Starting → Running → GameExitedDraining
                                        ├─ CaptureCompleted
                                        ├─ CaptureIncomplete
                                        ├─ Interrupted
                                        └─ ProcessStateUnknown
```

- 进程启动**之前**先持久化 manifest 与 `session.created`。
- 进程创建成功后立即记录 PID、进程启动时间、可执行文件与工作目录。
- 游戏退出不等于采集完成：先排空管道、刷新文件，再标记完成。
- 管道读取与磁盘写入通过有界队列解耦；队列饱和时丢弃并写入缺口标记，游戏写入端不会被阻塞。
  实现使用 `Wait` + `TryWrite`：队列满时返回 false 并累计丢弃字节，保证“写入 + 丢弃”覆盖全部输入（完成时另写缺口标记）。

## 3. 恢复

ECL 启动后由 `GameSessionService.Recover()` 处理遗留会话：

- 仅在 PID、进程启动时间与可执行文件身份匹配时重连；
- 进程仍存在但身份不匹配 → `ProcessStateUnknown`；
- 进程不存在 → `Interrupted`（原本正在排空 → `CaptureIncomplete`）；
- PID 单独不足以证明身份，避免 PID 复用导致的错误附着；
- 若采集宿主已退出（记录的主机 PID 不存在），保留 `Running` 并标记 `session.recovered.hostUnverified`，同时把会话独立 `game.log` 登记为 `Direct` 证据；
- 恢复幂等：重复执行不会重复登记文件或破坏证据。

## 4. 证据与置信度

| 置信度 | 含义 |
|---|---|
| `Direct` | 会话独占路径或直接从该进程采集 |
| `StronglyCorrelated` | 在进程时间窗内创建且被可靠进程/会话证据佐证 |
| `Possible` | 只有时间/路径相关性 |
| `SharedOrAmbiguous` | 可能被多个进程写入，归属未定 |
| `Unassociated` | 找到但无法安全归属 |

已登记的证据家族：stdout/stderr、`hs_err_pid*.log`、客户端/服务端崩溃报告、共享 `latest.log`、轮转归档（按内容识别 gzip，不假设扩展名）。共享 `latest.log` 始终标记为 `SharedOrAmbiguous`，绝不声称属于单一进程。

## 5. 诊断导出

`ecl session export <会话ID>` 或日志页“导出诊断”：

- 一次导出只对应一个 SessionId；
- 包含 `manifest.json`、`events.jsonl`、会话日志、`artifacts.json` 与 `session-summary.txt`；
- 缺失或无法关联的证据在 summary 中逐项列出，不暗示采集完整；
- 文本内容经 `SecretRedaction.Text` 脱敏；原始本地证据不变；
- 先写 staging ZIP，成功后再原子改名，失败清理 staging；
- 单文件 16 MB、总量 128 MB 上限。

## 6. UI 与 CLI

- 日志页：多会话列表、增量读取文件、跟随/暂停、清空视图、打开目录、导出；关闭页面或重启 ECL 不删除会话。
- 新启动只会让界面聚焦最新会话，不删除旧证据。

```powershell
ecl session list [--json]
ecl session show <会话ID> [--json]
ecl session export <会话ID> [--output <路径>] [--json]
ecl session recover [--json]
ecl session prune --older-than-days <N> [--apply] [--json]
```

## 7. 每会话日志隔离

`Log4jIsolation` 为每次启动派生独立的 log4j2 配置：

- 仅当启动命令使用 `-Dlog4j.configurationFile`、目标是可识别的 Mojang 默认配置（`client-*.xml` 且包含默认文件 appender）时才派生；
- 把文件 appender 指向 `<session>/game.log`，轮转目录指向 `<session>/rolled/`；
- 不改写共享实例文件、不覆盖整合包或用户自定义配置；其他配置保持 `shared` 并继续标记归属歧义；
- 记录配置指纹（SHA-256 前 32 位）与隔离方式；`pending` 会在进程启动后由 LogHost（或进程内退出路径）确认文件是否真正创建，未确认则降级为 `shared` 并写入警告；
- 隔离成功时证据以 `Direct` 归属登记为会话日志，不再复制共享 `latest.log`。

## 8. 保留策略

`session prune --older-than-days <N>` 只清理已完成、已中断且超过保留期的会话：

- 运行中、启动中、排空中、恢复中、`ProcessStateUnknown` 的会话永不清理；
- 默认只预览；`--apply` 才会删除；执行前重新检查状态，避免删除刚变得活跃的会话；
- 清理是独立、用户显式发起的操作，不由新启动、关闭窗口或退出 ECL 触发。

## 9. 尚未完成

- 断电/强杀覆盖：会话目录缺 manifest、manifest 停在 Starting、PID 检查点前中断、退出后排空未完成、journal 尾部半条记录、孤儿目录；均有自动化故障注入测试。
- 保留策略尚未接入 UI 设置与自动提示。
- 写入失败（磁盘满/只读）通过 `SessionLogWriter.Fault` 上报并转为 `CaptureFault`；真实文件系统场景以注入流覆盖。

## 10. 实机验证（2026-11-02）

- 原版 1.21.1 + 离线账户，经 CLI 真实启动：会话在进程启动前创建，LogHost 启动进程并持有管道，manifest 记录 `captureHost=loghost`、真实 PID 与 Java 版本。
- 日志隔离实机生效：会话目录生成 `log4j2.xml` 与 `game.log`（2765 字节），manifest `logIsolation=direct`；退出后证据以 `Direct` 归属登记，未再复制共享 `latest.log`。
- 正常运行后关闭游戏：会话 `CaptureCompleted`、`exitCode=0`，stdout 6368 字节。
- 强制结束游戏进程后，`session recover` 将遗留会话标记为 `Interrupted`，`session export` 成功并列出缺失的崩溃证据。
- 两次启动使用不同 SessionId，旧会话证据保留。
