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

## 3. 恢复

ECL 启动后由 `GameSessionService.Recover()` 处理遗留会话：

- 仅在 PID、进程启动时间与可执行文件身份匹配时重连；
- 进程仍存在但身份不匹配 → `ProcessStateUnknown`；
- 进程不存在 → `Interrupted`（原本正在排空 → `CaptureIncomplete`）；
- PID 单独不足以证明身份，避免 PID 复用导致的错误附着；
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
```

## 7. 尚未完成

- 每会话 Log4j2 日志隔离（当前由 stdout/stderr 直接采集，共享 `latest.log` 标记歧义）。
- 独立 LogHost 进程已实现（见 [loghost.md](loghost.md)）；LogHost 不可用时降级为进程内采集并记录 `CaptureFault`。
- 保留策略与物理清理尚未实现；当前只增加会话，不自动删除。
