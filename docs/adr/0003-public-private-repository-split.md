# ADR 0003：public 与 private 仓库分离

- 状态：已采纳
- 日期：2026-10-26

## 背景

项目采用 GPL-3.0-or-later，源码必须公开；但代码签名证书、私钥、OAuth/上传凭据不能公开。二者生命周期与访问控制完全不同。

## 决策

1. `Ender-Craft-Launcher`（public）：全部源码、文档、构建脚本、公开配置与公开密钥。
2. `Ender-Craft-Launcher-Core`（private）：仅签名证书、私钥、凭据等极私密材料。
3. public 仓库的构建、测试、CI **不依赖** Core；签名是发布步骤，通过环境变量/CI Secret 注入。
4. public 仓库 `.gitignore` 忽略证书与密钥文件，作为纵深防御。

## 后果

- 优点：源码合规公开；密钥访问最小化；构建可复现；贡献者无需访问私有仓库。
- 缺点：发布流程需要额外的秘密注入；需要严格的泄露处理与轮换流程。
- 约束：不得把 Core 作为 submodule 或构建依赖；不得在 public 历史中出现秘密。

详见 [repository-policy.md](../repository-policy.md)。
