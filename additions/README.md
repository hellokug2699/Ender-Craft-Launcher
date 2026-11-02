# 官方 Additions

本目录保存 ECL 官方扩展目录与扩展数据模板。实现遵循 [ADR 0032](../docs/adr/0032-native-feature-interfaces-and-optional-additions.md) 与 [ADR 0040](../docs/adr/0040-vanilla-toolkit-addition.md)。

## 目录

```text
additions/
├─ catalog.json            # 官方目录（由私钥签名，客户端用 build/additions-trust.json 验证）
├─ catalog.json.sig        # 目录的 detached RSA-PSS 签名
└─ vanilla-toolkit/
   ├─ data-template/       # 数据包模板、Schema 与最小样例
   └─ src/                 # Vanilla Toolkit 工具实现（作为 Addition 能力程序集）
```

## 发布流程

1. 构建扩展包 zip；包根目录必须包含 `addition.json`（`id`、`version`、`entryAssembly`、逐文件 `size`/`sha256`）。
2. 上传为现有 ECL 公有仓库 GitHub Releases 的独立 Release Asset；标签使用 `additions-<id>-<version>`，不要求重新发布启动器。
3. 更新 `catalog.json` 的条目：包地址（HTTPS）、大小、SHA-256、Core API 范围、依赖、数据 schema。
4. 使用受控私钥签名目录：

   ```powershell
   pwsh -NoProfile -File build/sign-additions-catalog.ps1 `
     -SigningKeyPath <私有 key 路径> -KeyId ecl-additions
   ```

5. 提交 `catalog.json` 与 `catalog.json.sig`。私钥只保存在私有 Core 仓库或受控发布环境，并遵循 [ADR 0024](../docs/adr/0024-user-authorized-self-signed-code-signing.md) 的信任与轮换规则。

## 约束

- 不在 Git 历史中提交扩展二进制或大型数据包；只提交索引、签名和模板。
- 目录与包内容必须一致；哈希或签名验证失败时客户端拒绝安装并保留原有可用版本。
- 未安装或禁用扩展时，普通启动、实例管理与游戏启动不受影响。
- 不建立服务器端内容代理、镜像或抓取服务；包分发只使用现有静态发布渠道。
