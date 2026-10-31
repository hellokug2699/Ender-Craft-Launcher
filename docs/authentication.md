# Microsoft 账户接入说明

> 当前状态：Microsoft 正式账户认证链已经在 Windows 11 实机完成验证。ECL 使用 Microsoft OAuth Authorization Code + PKCE，经 Xbox Live、XSTS、Minecraft Services 完成 Java Edition 权益与档案验证，并使用 Windows DPAPI 保存敏感凭据。令牌刷新在不打开浏览器的情况下完成。

## Client ID 填写位置

打开 ECL **设置 → 全局 → 账户与身份验证 → Microsoft OAuth Client ID**，填写 Microsoft Entra 管理中心中应用注册的 **Application (client) ID**（GUID 格式）。

ECL 桌面应用属于 public client，不配置、不分发 Client Secret。当前公开仓库中的 Client ID 只是应用标识符，不是秘密。

## 登录流程

用户主动点击 Microsoft 登录后，ECL：

1. 生成 OAuth `state` 与 PKCE `code_verifier`。
2. 使用 `code_challenge_method=S256` 打开系统默认浏览器。
3. 使用本地回环地址接收授权回调，并校验返回的 `state`。
4. 使用 authorization code + `code_verifier` 换取 Microsoft access token 与 refresh token。
5. 使用 Microsoft token 完成 Xbox Live User Authentication。
6. 使用 Xbox token 请求 XSTS，relying party 为 `rp://api.minecraftservices.com/`。
7. 使用 `XBL3.0 x=<userHash>;<xstsToken>` 调用 Minecraft Services `login_with_xbox`。
8. 检查 Minecraft Java Edition entitlement。
9. 读取 Minecraft profile，创建 Microsoft 类型账户。

Xbox Live 与 XSTS 请求使用服务端要求的 `x-xbl-contract-version: 1`，请求 JSON 属性名保持协议要求的 PascalCase；Microsoft token 响应中的 `access_token` / `refresh_token` 等 snake_case 字段显式映射。

## 回环地址

当前桌面授权使用 `localhost` 回环地址。Microsoft Entra 对桌面应用的 localhost 回环地址具有特殊匹配规则；不要在不确认配置与平台行为的情况下把它改回 `127.0.0.1`。

## 凭据存储

Microsoft access token、refresh token 和 Minecraft token 的过期时间不会写入普通账户 JSON。Windows 环境使用 DPAPI 加密存储，并在账户重新加载时恢复到内存。

诊断包、CLI 输出、GUI 启动命令预览不会输出完整 access token；敏感 token 在需要展示时仅保留首尾少量字符。

不要把任何 token、authorization code、Client Secret 或密码提交到 Git 仓库，也不要写入普通日志。

## 静默刷新

启动或其他需要有效 Minecraft token 的操作可以检查 token 剩余有效期。默认在距离 Minecraft token 过期不足 30 分钟时静默刷新；如果过期时间未知，优先从 token 自身的 `exp` claim 推断。

刷新流程为：

```text
Microsoft refresh token
        ↓
Microsoft access token
        ↓
Xbox Live
        ↓
XSTS
        ↓
Minecraft Services
        ↓
新的 Minecraft access token
```

如果 Microsoft 返回轮换后的 refresh token，ECL 会持久化新的 refresh token。刷新不应自动打开浏览器；只有 refresh token 无法继续使用并需要重新授权时，才应提示用户重新登录。

## 实机验证

认证链已经在 Windows 11、通过 Microsoft/Mojang AppID allow list 审核的 Client ID 和真实 Minecraft 账户上完成实机验证。验证过程中发现并修复了以下真实环境问题：

- Microsoft token JSON 字段未正确映射为 C# 属性，导致 token 实际为 `null`。
- Xbox Live / XSTS 请求 JSON 大小写不符合服务端要求，导致 HTTP 400。
- Entra loopback 地址使用 `127.0.0.1` 时触发 `invalid_request`，改为 `localhost`。
- 同名但不同类型的账户不能共存。
- access token 出现在 CLI、GUI 命令预览和诊断输出中，现已脱敏。

这些问题均以修复提交进入 `nightly`，并应通过回归测试持续保护。

## 第三方认证（Authlib-Injector）

ECL 支持通过 Authlib-Injector / Yggdrasil 兼容服务器登录第三方账户。密码只用于本次登录，不写入账户 JSON、日志或诊断包；令牌保存在安全凭据存储中。

安全约束：

- 认证服务器地址必须是 HTTPS 绝对地址；仅 `localhost` / 回环地址允许 HTTP 用于本地测试。
- 拒绝带 `userInfo` 的 URL（例如 `https://user:pass@example.com`），避免地址本身携带凭据。
- 认证客户端使用 `AllowAutoRedirect = false`；3xx 重定向按失败处理，密码或令牌不会被 307/308 转发到其它主机。
- 修改认证服务器地址时，必须先输入密码完成重新登录；ECL 会先验证新服务器元数据并完成认证，再提交服务器地址与账户凭据。提交账户凭据失败时会尝试回滚服务器地址。
- Authlib-Injector 清单中的版本号只允许路径安全字符，SHA-256 必须是 64 位十六进制；缓存的 jar 只有通过校验并带有 `.sha256` 记录时才用于命令预览。

## 当前边界

- 已实现：Microsoft OAuth、PKCE、浏览器回调、Xbox Live、XSTS、Minecraft Services、Java Edition entitlement、Minecraft profile、Authlib-Injector 第三方账户、Windows DPAPI、安全 token 脱敏、静默刷新。
- 已完成：真实账户端到端实机验证。
- 仍需完善：账户注销 / 凭据彻底清理体验、refresh token 失效后的重新授权状态、跨平台安全凭据存储，以及完整账户管理 UI。

仅完成 Microsoft OAuth 不代表用户拥有可启动的 Minecraft Java Edition；ECL 必须继续完成 Xbox / XSTS / Minecraft Services 和 Java Edition entitlement 检查。
