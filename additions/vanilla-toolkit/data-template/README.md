# Vanilla Toolkit 数据模板

本目录是 ADR 0040 定义的官方数据包模板，**不是正式发布数据**。它演示 Manifest、Schema、版本目录和最小样例的格式；实际数据由维护者准备、校验并随 Release Asset 发布。

## 目录结构

```text
data-template/
├─ manifest.example.json          # 数据包清单示例（含文件大小与 SHA-256）
├─ addition.example.json          # 扩展包 addition.json 示例（ADR 0032）
├─ schemas/
│  ├─ data-pack.schema.json       # Manifest schema
│  └─ registry-domain.schema.json # registry 数据域 schema
└─ samples/1.20.1/registry.json   # 最小注册表样例
```

## 约束

1. 数据按 Minecraft 版本与数据域组织；新增版本必须新增独立目录与 Manifest 条目，不得覆盖旧版本数据。
2. 每个数据文件必须在 Manifest 中记录 `size` 与 `sha256`；加载器会在参与查询前校验。
3. `minecraftVersions` 是工具默认可用版本的**精确集合**；版本不匹配时必须让用户选择，不得静默回退到“最接近版本”。
4. 条目中的 `introduced` / `removed` / `versions` 用于版本适用性判断。无法可靠比较的版本（快照、预发布）应使用显式 `versions`。
5. 只保存事实性标识符与必要说明；不要复制未经授权再分发的 Wiki 文本、纹理、模型或其他游戏资产。
6. `quality` 必须如实填写：`verified`（从目标版本文件核对）、`derived`（由可检查规则推导）、`community`（社区资料整理）、`unverified`（未验证）。工具会把它随结果展示。
7. 修改数据后重新计算哈希；哈希不匹配会导致整个数据域被拒绝加载。

## 生成正式数据包

1. 在 `data-template` 之外准备数据（例如 `additions/vanilla-toolkit/data/1.20.1/`）。
2. 复制 `manifest.example.json` 为数据包根目录的 `manifest.json`，更新版本集合、数据域、来源、维护者、更新时间与哈希。
3. 用 `ECL.VanillaToolkit.VanillaDataPackLoader` 加载验证；无效数据不得发布。
4. 将数据包 zip 作为 GitHub Release Asset 发布，并在官方 `additions/catalog.json` 中登记包地址、大小与 SHA-256。
