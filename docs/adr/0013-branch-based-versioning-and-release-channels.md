# ADR 0013：分支驱动的版本与发布频道

- Status: In Progress
- Date: 2026-11-01
- Scope: ECL 架构与实现

## 背景

ECL 同时维护 `main`、`dev`、`nightly` 三条长期分支。此前版本号、更新频道、程序集显示、安装包名称和 GitHub Release 元数据分别由不同位置维护，容易出现版本不一致，也容易把测试构建误标为正式版。手工填写发布参数还会导致相同分支的构建使用不一致的频道或版本。

私有 Core 仓库中的 hints 是设计决策的前置产物；已经形成并接受的项目决策应当沉淀在公开仓库的 `docs/adr` 中。

## 决策

### 1. 分支与频道一一对应

| Git 分支 | 内部频道 ID | 用户显示名称 |
|---|---|---|
| `main` | `main` | 正式版 |
| `dev` | `dev` | 测试版 |
| `nightly` | `nightly` | 夜间版 |

频道 ID 使用分支名，不再为同一概念维护 stable/beta 等别名。旧配置值 `stable` 映射到 `main`，`beta` 映射到 `dev`，以兼容既有用户配置。

其他临时分支属于自定义构建，不得伪装成上述三个官方频道。

### 2. SemVer 版本格式

基础版本统一由根目录 `Directory.Build.props` 的 `VersionPrefix` 提供，格式为 `MAJOR.MINOR.PATCH`。

- 正式版：`1.2.3`
- 测试版：`1.3.0-dev.YYYYMMDD.RUN.ATTEMPT`
- 夜间版：`1.3.0-nightly.YYYYMMDD.RUN.ATTEMPT`

日期使用 UTC；流水线运行序号与运行尝试次数共同保证同一工作流内构建标识可区分。版本主体不得由开发者在每次构建时手工输入。正式版本由 `vMAJOR.MINOR.PATCH` 标签触发，标签本身是正式版本号的权威来源。

### 3. 单一版本来源

构建流水线解析版本与频道，并将其注入 .NET 构建属性。程序集 InformationalVersion、应用主界面、关于页面、更新 Manifest、Release 标题、Artifact 名称和安装包文件名必须使用同一版本与频道元数据。

不得在 UI、打包脚本或更新器中另行拼接独立版本字符串。预发布后缀必须保留，版本比较必须遵循 SemVer 预发布排序规则。

### 4. 构建、发布与更新源分离

- **Build** 是一次 CI 执行及其可追溯产物；**Release** 是面向用户的发布记录；**Channel** 是客户端选择更新源的策略。三者不得混为一谈。
- `dev` 与 `nightly` 分支的每次推送都执行签名构建，并上传有期限的 Actions Artifact，供高级用户下载指定构建。
- `dev` 与 `nightly` 不为每次提交创建独立 GitHub Release；每个频道只维护一个滚动 Release，固定 Tag 分别为 `dev`、`nightly`。
- `main` 只接受 `vMAJOR.MINOR.PATCH` 标签作为正式版本发布入口；正式版本 Release 不可变，同时维护唯一的 `main` 滚动 Release 供自动更新客户端读取。
- 滚动频道资产更新完成后才移动对应 Tag；清理旧版本资产必须发生在新 Manifest 与签名发布后，避免遗留资产无限累积。
- 分支构建按队列执行，只有仍对应当前分支 HEAD 的构建可以更新滚动源；过期构建可以保留为短期 Actions Artifact，但不得覆盖较新的频道版本。
- `main`、`dev`、`nightly` 滚动源必须分别提供 `update-manifest.json`、`update-manifest.sig` 与 Manifest 指定的完整包。
- 发布工作流必须拒绝未知分支、格式错误版本和频道不匹配的标签。安装包、更新 Manifest 和 Release 元数据必须一致；发布前应验证所有预期文件存在。

## 后果

### 正面

- 用户看到的频道名称与实际更新来源一致。
- CI 自动生成可区分的预发布版本，减少人工操作和版本冲突。
- 安装包、程序集、更新器和 Release 可追溯到同一构建版本。
- 正式版发布需要明确打版本标签，降低误发布风险。

### 代价与约束

- 发布工作流需要维护版本注入、签名密钥和 Release 权限。
- 修改 `VersionPrefix` 会影响后续所有分支构建，版本升级应通过代码审查。
- GitHub Actions 环境审批、Secrets 和仓库权限属于部署前置条件，不应通过跳过签名或降低安全要求来规避。
- 滚动 Release 只保留最新频道资产；指定历史构建通过短期 Actions Artifact 获取，Artifact 过期后不保证继续提供。正式版本仍保留不可变 Release。

## 验收标准

1. 三条分支分别生成正确的频道 ID 和预发布标识。
2. 正式版只能由符合 SemVer 的 `v*` 标签生成。
3. UI、程序集、Manifest、Release 和 ZIP 文件名版本完全一致。
4. 预发布版本比较遵循 SemVer。
5. 未配置签名密钥、签名公钥不匹配、缺失资产或频道错误时，发布必须失败关闭。
6. CI 与 Release 工作流均有可检查的成功结果。
7. Nightly 每次提交不会增加独立 Release 条目；滚动 Tag、Manifest 与包必须指向同一构建。
