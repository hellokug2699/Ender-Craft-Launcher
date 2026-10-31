# 字体与排版（HarmonyOS Sans SC）

ECL 的 WPF 界面使用随程序内嵌的 HarmonyOS Sans SC 三个静态字面：Regular、Medium、Bold。
字体文件原样来自 private Core 仓库的 `HarmonyOS Sans/HarmonyOS_Sans_SC/`，未做任何修改。

## 1. 字面与内部家族名

| 文件 | 设计字重 | WPF 内部家族名 | 用途 |
| --- | ---: | --- | --- |
| `HarmonyOS_Sans_SC_Regular.ttf` | 400 | `HarmonyOS Sans SC` | 正文、说明、设置内容 |
| `HarmonyOS_Sans_SC_Medium.ttf` | 500 | `HarmonyOS Sans SC Medium` | 按钮、次要标题、强调标签 |
| `HarmonyOS_Sans_SC_Bold.ttf` | 700 | `HarmonyOS Sans SC` | 页面标题、强强调 |

Medium 的内部家族名不是 `HarmonyOS Sans SC`，因此不能只写 `FontWeight="Medium"` 让 WPF
合成一个 SemiBold；必须使用独立的 `UiMediumFontFamily` 资源。Bold 与 Regular 共享家族名，
通过 `FontWeight="Bold"` 选择真实 Bold 字面。

## 2. 共享排版 token

定义在 [Themes/Theme.xaml](../src/ECL.App/Themes/Theme.xaml)：

| Token | 字面 | 使用位置 |
| --- | --- | --- |
| `UiFontFamily` | Regular | 隐式 `TextBlock`、输入框、下拉框、窗口默认 |
| `UiMediumFontFamily` | Medium | `EmphasisText`、`SectionText`、`LabelText`、按钮 |
| `HeadingText` | Bold | 页面大标题与工作区实例名 |
| `TitleText` | Bold | 页面标题（28px） |

页面内原先的 `FontWeight="SemiBold"` 已替换为 `EmphasisText` 或 `HeadingText`，避免
WPF 在不存在 SemiBold 字面时合成粗细。字体大小与布局保持不变，首轮只改变字重层级。

## 3. 许可证与归属

- 许可证原文：[HarmonyOS_Sans_LICENSE.txt](../src/ECL.App/Fonts/HarmonyOS_Sans_LICENSE.txt)
- 关于页展示 HarmonyOS Sans SC 与许可证名称，并列出 Regular / Medium / Bold。
- 字体文件随程序资源打包，不单独分发、不修改；许可证文本复制到输出与发布目录。

## 4. 验证记录

2026-11-01，Windows 10/11，100% 显示缩放：

- [改造前主页](images/typography/before-home.png)
- [改造后主页](images/typography/after-home.png)

改造后正文仍使用 Regular，强调标签与按钮使用真实 Medium，页面标题使用真实 Bold；
未发现因字体度量变化导致的布局溢出。125% / 150% 缩放与更多页面截图仍待补充。

## 5. 后续检查

- 若 125% / 150% 下正文仍偏细，可按指引做一次把正文切换到 Medium 的受控对比，
  而不是全局改用 Bold。
- 新增页面应复用 `EmphasisText` / `HeadingText` / `SectionText` / `LabelText`，
  不要继续写 `FontWeight="SemiBold"`。
