# 代码签名信任

ECL 使用两层相互独立的验证：

1. **Windows Authenticode 自签名证书**：用于让 Windows 识别 ECL 发布产物的签名者。
2. **ECL 更新 Manifest（RSA-PSS-SHA256）**：由 Bootstrapper 按 `update-trust.json` 验证更新清单，
   再按清单中的 SHA-256 校验发布包与文件。

两层不能互相替代。本页只描述 Authenticode 自签名信任；更新签名见 [deployment.md](deployment.md)。

## 固定身份

| 字段 | 值 |
| --- | --- |
| Subject | `CN=Ender Craft Launcher Self-Signed Code Signing, O=Ender Craft Launcher Contributors` |
| SHA-256 指纹 | `af0da1ee9e9393a1e6787832b842e25f5fb17180b50f1c6afbf732efe525a651` |
| 证书文件 | [`build/code-signing/ecl-code-signing.cer`](../build/code-signing/ecl-code-signing.cer) |
| 信任元数据 | [`build/code-signing/code-signing-trust.json`](../build/code-signing/code-signing-trust.json) |

客户端以 **SHA-256 指纹**校验证书，而不是 Subject 或文件名。可通过独立渠道核对上述指纹。

## 用户授权

- ECL 不会静默导入证书，也不会把导入隐藏在普通安装步骤中。
- 只有用户主动选择信任时才请求管理员权限，把叶证书导入 `LocalMachine\TrustedPeople`；
  不安装系统根证书。
- 用户拒绝、取消 UAC 或没有管理员权限时，ECL 继续使用更新 Manifest 验签，基本功能不受影响。
- 证书已存在且未被删除/撤销时，ECL 复用现有信任状态，不重复提权。
- 用户可以在设置中查看指纹、信任状态并撤销本机信任。

## 首次安装的边界

首次下载的 Installer 尚未被用户预先信任的 ECL 证书验证。Installer 在安装前提供 SHA-256
人工核对向导：用户使用 Windows PowerShell 计算本地 Installer 哈希，并与官方 Release 的
摘要逐字符比较。跳过、摘要不可用和哈希不一致都必须显示为“未验证”，不能显示为通过。

这不是对开发者现实身份的认证，也不能抵御 GitHub 账号、仓库权限或发布链被攻陷。

## 常规权限承诺

ECL 主程序、Bootstrapper、更新检查和游戏启动均不得请求管理员权限。唯一允许主动提权的
场景是用户明确发起的首次证书信任导入。若普通启动出现 UAC，应取消并视为潜在篡改信号。

## 验证方式

- 签名后修改文件，`Get-AuthenticodeSignature` 必须报告签名无效。
- `signtool verify /pa` 与 Windows 属性页的签名者信息应与固定指纹一致。
- 未导入证书的干净环境应显示未知发布者；导入后同一文件应显示 ECL 签名者。
- 测试拒绝 UAC、指纹不匹配、证书过期、撤销与轮换路径。
