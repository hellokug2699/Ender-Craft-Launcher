# Microsoft 账户接入说明

> 当前状态：仅预留 Client ID 配置项，Microsoft OAuth / Xbox Live / Minecraft Services 登录尚未实现。

## Client ID 填写位置

打开 ECL **设置 → 全局 → 账户与身份验证 → Microsoft OAuth Client ID**，
填写 Microsoft Entra 管理中心中应用注册的 **Application (client) ID**（GUID 格式）。

该设置会保存到 ECL 全局配置，但当前版本尚无登录流程读取它，因此填写后不会出现微软登录按钮，
也不会把离线账户转换为微软账户。

## 应用注册建议

正式实现登录时，推荐：

- Supported account types：按 ECL 面向的用户群体选择个人 Microsoft 账户（Minecraft 玩家通常需要个人账户）。
- Platform：Mobile and desktop applications / Public client。
- OAuth：Authorization Code + PKCE。
- Redirect URI：桌面应用回环地址，例如 `http://localhost`，并与代码中使用的 URI 完全一致。
- 不配置、不分发 Client Secret。桌面客户端属于 public client，无法安全保密静态密钥。

微软登录后仍需完成 Xbox Live、XSTS、Minecraft Services 身份验证和 Minecraft Java Edition 档案检查。
仅完成 Microsoft OAuth 不代表用户拥有可启动的 Minecraft Java Edition。

## 当前实现边界

- 已有：全局 Client ID 设置字段、账户模型中的 Microsoft 类型占位。
- 尚无：浏览器授权、PKCE、回调监听、token 刷新、安全令牌存储、Xbox/XSTS/Minecraft 令牌交换、
  正版档案验证和 Microsoft 账户管理 UI。
- 因此不要把 Client ID 或任何 token 写入源代码，也不要把当前字段描述为已可用的微软登录功能。
