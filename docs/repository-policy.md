# 仓库与保密策略

ECL 使用两个仓库：

| 仓库 | 可见性 | 内容 |
| --- | --- | --- |
| `Ender-Craft-Launcher` | public | 全部源码、文档、构建脚本、公开配置、公开密钥 |
| `Ender-Craft-Launcher-Core` | private | 仅存放极私密材料：签名证书、私钥、OAuth/上传凭据 |

## 1. 判断规则

**如果一个文件是构建、运行、测试、发布（不含签名）所必需的，它属于 public。**  
**只有当文件泄露会造成安全风险，且不属于产品源码时，才放进 Core。**

### public 仓库允许

- C# / XAML 源码、测试、示例配置。
- 架构文档、ADR、路线图、构建脚本。
- 更新公钥（用于客户端验证签名，公钥本身不保密）。
- 本地化文件。
- CI 配置（不含任何密钥值）。

### Core 仓库允许

- Windows 代码签名证书（`.pfx`）及其口令。
- 更新包签名私钥、安装包签名私钥。
- OAuth 客户端机密（若未来使用需要机密的流程）。
- 崩溃上传、遥测、私有 API 的访问令牌。
- 发布自动化使用的凭据、私有基础设施配置。
- 尚未公开的私密合作/分发材料。

### 绝对不允许

- 在 public 仓库提交 `.pfx`、`.p12`、`.snk`、私钥、令牌、密码。
- 在 public 仓库的提交历史、Issue、PR、Actions 日志中出现秘密。
- 在 Core 仓库放业务代码、文档、截图、构建产物、可公开的配置。
- 让 public 仓库的构建、测试、CI 依赖 Core 仓库或私有子模块。

## 2. 构建与签名分离

public 仓库必须在没有 Core 的情况下完整构建和测试：

```powershell
dotnet restore ECL.sln
dotnet build ECL.sln -c Release
dotnet test tests/ECL.Tests/ECL.Tests.csproj -c Release
```

签名是发布步骤，不是构建步骤。`build/sign.ps1` 从环境变量读取证书：

```powershell
$env:ECL_SIGNING_PFX = "C:\path\to\EnderCraft-Launcher.pfx"
$env:ECL_SIGNING_PASSWORD = "<从安全存储读取>"
./build/sign.ps1 -Files "artifacts/publish/win-x64/ECL.exe"
```

证书文件放在 Core 仓库的本地检出目录中，通过安全存储或 CI Secret 提供口令；public 仓库不保存任何证书。

## 3. CI 与发布

推荐两种方式，任选其一：

1. **Actions Secrets**：在 public 仓库的 release workflow 中，从 GitHub Actions Secrets 读取 `ECL_SIGNING_PFX_BASE64` 与 `ECL_SIGNING_PASSWORD`，临时解码到 runner，签名后删除。
2. **Core 私有 workflow**：在 Core 仓库保存证书，通过受保护的 Environment 触发发布；public 仓库只负责构建产物。

无论哪种方式：

- PR 构建永不签名。
- 发布环境需要人工审批。
- 日志中不得输出证书路径以外的敏感信息，不得 `echo` 口令。
- 临时证书文件在 job 结束后删除（`if: always()`）。

## 4. 泄露处理

1. 立即吊销并轮换证书/令牌。
2. 从 public 仓库移除文件，并清理 Git 历史（`git filter-repo`），再强制推送；操作前通知协作者。
3. 检查 Actions 日志、Release 附件、Issue/PR 缓存。
4. 在 Core 记录轮换时间与影响范围（不含新密钥值）。

## 5. Core 仓库使用约定

- 根目录 README 只描述用途与目录索引，不写具体密钥值。
- 证书、私钥使用明确文件名，例如 `signing/ender-craft-launcher.pfx`。
- 口令不写入仓库文件；使用密码管理器或 CI Secret。
- 仓库访问遵循最小权限；离职/移除协作者时同步轮换其可见的密钥。
- Core 仓库的 Git 历史包含秘密是预期行为，因此**永远不要把它公开或镜像到 public**。

## 6. 许可证说明

public 仓库代码采用 GPL-3.0-or-later。签名证书、私钥、CI 凭据不是源代码的一部分，不属于 GPL 的“对应源码”分发义务；发布的二进制仍应提供对应的 public 源码。
