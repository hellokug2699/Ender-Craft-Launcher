// SPDX-License-Identifier: GPL-3.0-or-later

using Ecl.Configuration.Network;

namespace Ecl.Configuration.Options;

internal sealed class LauncherOptionsProvider : IOptionProvider
{
    public IEnumerable<OptionDefinition> GetOptions()
    {
        // 界面语言默认“跟随系统”（settings-interface-preferences.md §5）：
        // 启动时按操作系统首选语言选择 ECL 已提供的最合适语言，不支持时回退到 en-US。
        // 已保存的旧值（zh-CN / en-US）继续有效，不迁移、不改写。
        yield return new OptionDefinition<string>
        {
            Key = "launcher.language",
            Kind = OptionKind.Select,
            Scope = OptionScope.Global,
            CategoryKey = "category.launcher",
            DisplayNameKey = "option.launcher.language.name",
            DescriptionKey = "option.launcher.language.desc",
            Default = "system",
            RequiresRestart = true,
            Order = 10,
            Choices =
            [
                new OptionChoice("system", "option.launcher.language.system"),
                new OptionChoice("zh-CN", "option.launcher.language.zh"),
                new OptionChoice("en-US", "option.launcher.language.en"),
            ],
        };

        yield return new OptionDefinition<string>
        {
            Key = "launcher.minecraftDirectory",
            Kind = OptionKind.Path,
            Scope = OptionScope.Global,
            CategoryKey = "category.launcher",
            DisplayNameKey = "option.launcher.minecraftDirectory.name",
            DescriptionKey = "option.launcher.minecraftDirectory.desc",
            Default = string.Empty,
            RequiresRestart = true,
            // 工作区由专用页面（workspace.json）管理；旧键只作为一次性迁移输入保留。
            IsHidden = true,
            Order = 20,
            Validate = path => string.IsNullOrWhiteSpace(path) || Path.IsPathFullyQualified(path),
        };

        yield return new OptionDefinition<bool>
        {
            Key = "launcher.checkUpdates",
            Kind = OptionKind.Boolean,
            Scope = OptionScope.Global,
            CategoryKey = "category.launcher",
            DisplayNameKey = "option.launcher.checkUpdates.name",
            DescriptionKey = "option.launcher.checkUpdates.desc",
            Default = true,
            Order = 30,
        };

        yield return new OptionDefinition<string>
        {
            Key = "launcher.updateChannel",
            Kind = OptionKind.Select,
            Scope = OptionScope.Global,
            CategoryKey = "category.launcher",
            DisplayNameKey = "option.launcher.updateChannel.name",
            DescriptionKey = "option.launcher.updateChannel.desc",
            Default = "main",
            VisibleWhenKey = "launcher.checkUpdates",
            VisibleWhenValue = "true",
            Order = 40,
            Choices =
            [
                new OptionChoice("main", "option.launcher.updateChannel.main"),
                new OptionChoice("dev", "option.launcher.updateChannel.dev"),
                new OptionChoice("nightly", "option.launcher.updateChannel.nightly"),
            ],
        };

        yield return new OptionDefinition<bool>
        {
            Key = "launcher.confirmBeforeDelete",
            Kind = OptionKind.Boolean,
            Scope = OptionScope.Global,
            CategoryKey = "category.launcher",
            DisplayNameKey = "option.launcher.confirmBeforeDelete.name",
            DescriptionKey = "option.launcher.confirmBeforeDelete.desc",
            Default = true,
            Order = 60,
        };

        // 界面偏好（settings-interface-preferences.md §3–§6）。ECL 品牌主题固定，
        // 这里只有字体族、字号档位、界面缩放与动效开关，不存在主题色或浅色模式键。
        //
        // 字体族只允许随包提供且已完成内部家族名/字面验证的资源；当前版本只随包提供
        // HarmonyOS Sans SC（Regular / Medium / Bold），因此选项表只有这一项。
        // 新增字体必须先随包资源与验证一起提交，不能只在这里加一个字符串。
        yield return new OptionDefinition<string>
        {
            Key = "launcher.interfaceFont",
            Kind = OptionKind.Select,
            Scope = OptionScope.Global,
            CategoryKey = "category.launcher",
            DisplayNameKey = "option.launcher.interfaceFont.name",
            DescriptionKey = "option.launcher.interfaceFont.desc",
            Default = "harmonyos-sans-sc",
            Order = 50,
            Choices =
            [
                new OptionChoice("harmonyos-sans-sc", "option.launcher.interfaceFont.harmonyosSc"),
            ],
        };

        // 字号档位只表达“整体文字尺寸”，倍率由 Design System 统一定义
        // （UiTypography：标准 1.0 / 较大 1.1 / 最大 1.25）。低于标准的档位会跌破
        // ADR 0050 §5 的可读性底线，因此不提供“较小”。
        yield return new OptionDefinition<string>
        {
            Key = "launcher.fontSize",
            Kind = OptionKind.Select,
            Scope = OptionScope.Global,
            CategoryKey = "category.launcher",
            DisplayNameKey = "option.launcher.fontSize.name",
            DescriptionKey = "option.launcher.fontSize.desc",
            Default = "standard",
            Order = 52,
            Choices =
            [
                new OptionChoice("standard", "option.launcher.fontSize.standard"),
                new OptionChoice("large", "option.launcher.fontSize.large"),
                new OptionChoice("xlarge", "option.launcher.fontSize.xlarge"),
            ],
        };

        // 界面缩放只作用于 ECL 自身布局（LayoutTransform），不修改 Windows 显示缩放。
        // 范围经最小窗口尺寸实测后确认；步进 5% 由 Validate 保证，CLI 也不能写入 83 之类的值。
        yield return new OptionDefinition<int>
        {
            Key = "launcher.uiScale",
            Kind = OptionKind.Integer,
            Scope = OptionScope.Global,
            CategoryKey = "category.launcher",
            DisplayNameKey = "option.launcher.uiScale.name",
            DescriptionKey = "option.launcher.uiScale.desc",
            UnitKey = "unit.percent",
            Default = 100,
            Min = 80,
            Max = 150,
            Step = 5,
            Order = 54,
            Validate = percent => percent % 5 == 0,
        };

        // 关闭后非必要动效时长归零（页面转场、按钮按压、弹窗淡入），但状态提示、
        // 进度与后台任务不受影响；操作系统“减少动画”偏好仍是上限（ADR 0021 §6）。
        yield return new OptionDefinition<bool>
        {
            Key = "launcher.animationsEnabled",
            Kind = OptionKind.Boolean,
            Scope = OptionScope.Global,
            CategoryKey = "category.launcher",
            DisplayNameKey = "option.launcher.animationsEnabled.name",
            DescriptionKey = "option.launcher.animationsEnabled.desc",
            Default = true,
            Order = 56,
        };

        yield return new OptionDefinition<string>
        {
            Key = "network.downloadSource",
            Kind = OptionKind.Select,
            Scope = OptionScope.Global,
            CategoryKey = "category.network",
            DisplayNameKey = "option.network.downloadSource.name",
            DescriptionKey = "option.network.downloadSource.desc",
            Default = "official-first",
            Order = 10,
            Choices =
            [
                new OptionChoice("official-only", "option.network.downloadSource.officialOnly"),
                new OptionChoice("official-first", "option.network.downloadSource.officialFirst"),
                new OptionChoice("mirror-first", "option.network.downloadSource.mirrorFirst"),
                new OptionChoice("mirror-only", "option.network.downloadSource.mirrorOnly"),
            ],
        };

        yield return new OptionDefinition<string>
        {
            Key = "network.proxyMode",
            Kind = OptionKind.Select,
            Scope = OptionScope.Global,
            CategoryKey = "category.network",
            DisplayNameKey = "option.network.proxyMode.name",
            DescriptionKey = "option.network.proxyMode.desc",
            // D-02 仍受“可靠安装代际信号”工程阻断（交接文档 §6.5）：
            // 在能区分旧安装与新安装之前，不能把默认值从 system 改成 none，
            // 否则会静默改变旧安装的代理行为。新安装默认 none 的迁移另行实施。
            Default = "system",
            Order = 20,
            Choices =
            [
                new OptionChoice("system", "option.network.proxyMode.system"),
                new OptionChoice("none", "option.network.proxyMode.none"),
                new OptionChoice("manual", "option.network.proxyMode.manual"),
            ],
        };

        // D-04：结构化端点。协议集合只包含本地代理服务对 .NET WebProxy 实测通过的项
        // （http / socks5 / socks4a），不承诺未验证的 https 代理。
        yield return new OptionDefinition<string>
        {
            Key = "network.proxyProtocol",
            Kind = OptionKind.Select,
            Scope = OptionScope.Global,
            CategoryKey = "category.network",
            DisplayNameKey = "option.network.proxyProtocol.name",
            DescriptionKey = "option.network.proxyProtocol.desc",
            Default = ProxyProtocols.Http,
            VisibleWhenKey = "network.proxyMode",
            VisibleWhenValue = "manual",
            Order = 21,
            Choices =
            [
                new OptionChoice(ProxyProtocols.Http, "option.network.proxyProtocol.http"),
                new OptionChoice(ProxyProtocols.Socks5, "option.network.proxyProtocol.socks5"),
                new OptionChoice(ProxyProtocols.Socks4a, "option.network.proxyProtocol.socks4a"),
            ],
        };

        yield return new OptionDefinition<string>
        {
            Key = "network.proxyHost",
            Kind = OptionKind.Text,
            Scope = OptionScope.Global,
            CategoryKey = "category.network",
            DisplayNameKey = "option.network.proxyHost.name",
            DescriptionKey = "option.network.proxyHost.desc",
            Default = string.Empty,
            VisibleWhenKey = "network.proxyMode",
            VisibleWhenValue = "manual",
            Order = 22,
            Validate = value => string.IsNullOrWhiteSpace(value)
                || ProxyEndpoint.TryCreate(ProxyProtocols.Http, value, 1, out _, out _),
        };

        yield return new OptionDefinition<int?>
        {
            Key = "network.proxyPort",
            Kind = OptionKind.Integer,
            Scope = OptionScope.Global,
            CategoryKey = "category.network",
            DisplayNameKey = "option.network.proxyPort.name",
            DescriptionKey = "option.network.proxyPort.desc",
            Default = null,
            Min = 1,
            Max = 65535,
            Step = 1,
            EmptyIsNull = true,
            VisibleWhenKey = "network.proxyMode",
            VisibleWhenValue = "manual",
            Order = 23,
        };

        // 旧版单一地址：只作为 D-04 的迁移输入，不再由页面编辑。
        // 解析成功时迁移为上面的结构化键并清除；解析失败时保留原值并提示修复。
        yield return new OptionDefinition<string>
        {
            Key = "network.proxyAddress",
            Kind = OptionKind.Text,
            Scope = OptionScope.Global,
            CategoryKey = "category.network",
            DisplayNameKey = "option.network.proxyAddress.name",
            DescriptionKey = "option.network.proxyAddress.desc",
            Default = string.Empty,
            IsHidden = true,
            Order = 30,
        };

        yield return new OptionDefinition<int>
        {
            Key = "network.maxConcurrentDownloads",
            Kind = OptionKind.Integer,
            Scope = OptionScope.Global,
            CategoryKey = "category.network",
            DisplayNameKey = "option.network.maxConcurrentDownloads.name",
            DescriptionKey = "option.network.maxConcurrentDownloads.desc",
            Default = 8,
            Min = 1,
            Max = 64,
            Step = 1,
            Order = 40,
        };

        yield return new OptionDefinition<int>
        {
            Key = "network.speedLimitMbps",
            Kind = OptionKind.Integer,
            Scope = OptionScope.Global,
            CategoryKey = "category.network",
            DisplayNameKey = "option.network.speedLimitMbps.name",
            DescriptionKey = "option.network.speedLimitMbps.desc",
            UnitKey = "unit.mbps",
            Default = 0,
            Min = 0,
            Max = 1000,
            Step = 1,
            Order = 50,
        };

        // 下载失败重试（settings-downloads.md §5）：开关关闭时每个地址只尝试一次；
        // 只有可恢复的网络/传输错误会重试，校验失败与权限拒绝不会重试。
        yield return new OptionDefinition<bool>
        {
            Key = "network.autoRetry",
            Kind = OptionKind.Boolean,
            Scope = OptionScope.Global,
            CategoryKey = "category.network",
            DisplayNameKey = "option.network.autoRetry.name",
            DescriptionKey = "option.network.autoRetry.desc",
            Default = true,
            Order = 60,
        };

        yield return new OptionDefinition<int>
        {
            Key = "network.maxRetryAttempts",
            Kind = OptionKind.Integer,
            Scope = OptionScope.Global,
            CategoryKey = "category.network",
            DisplayNameKey = "option.network.maxRetryAttempts.name",
            DescriptionKey = "option.network.maxRetryAttempts.desc",
            Default = 3,
            Min = 1,
            Max = 10,
            Step = 1,
            VisibleWhenKey = "network.autoRetry",
            VisibleWhenValue = "true",
            Order = 70,
        };

        yield return new OptionDefinition<string>
        {
            Key = "java.defaultPath",
            Kind = OptionKind.Path,
            Scope = OptionScope.Global,
            CategoryKey = "category.java",
            DisplayNameKey = "option.java.defaultPath.name",
            DescriptionKey = "option.java.defaultPath.desc",
            Default = string.Empty,
            Order = 10,
        };

        // 全局 Java 候选优先级（有序的可执行文件路径，分号分隔）。
        // 由设置中心“Java 管理”页的列表维护：只有用户实际调整顺序后才写入，
        // 未配置时仍使用发现默认顺序（页面规格 §6/§7）。不在通用设置页渲染。
        yield return new OptionDefinition<string>
        {
            Key = "java.priority",
            Kind = OptionKind.Text,
            Scope = OptionScope.Global,
            CategoryKey = "category.java",
            DisplayNameKey = "option.java.priority.name",
            DescriptionKey = "option.java.priority.desc",
            Default = string.Empty,
            IsHidden = true,
            Order = 15,
        };


        // Internal acknowledgement state. Hidden from the generic settings editor.
        yield return new OptionDefinition<string?>
        {
            Key = "launcher.java.incompatiblePromptAcknowledged",
            Kind = OptionKind.Text,
            Scope = OptionScope.Global,
            CategoryKey = "category.java",
            DisplayNameKey = "option.java.defaultPath.name",
            DescriptionKey = "option.java.defaultPath.desc",
            Default = null,
            EmptyIsNull = true,
            IsHidden = true,
            Order = 999,
        };

        // 全局内存策略（settings-game-launch.md §4.1，D-03）：
        // 默认自动；自动算法归启动服务，页面只显示结果，不自行计算。
        // java.defaultMemoryMb 继续作为手动数值保存，策略切到自动时数值保留但暂不生效。
        yield return new OptionDefinition<string>
        {
            Key = "java.memoryPolicy",
            Kind = OptionKind.Select,
            Scope = OptionScope.Global,
            CategoryKey = "category.java",
            DisplayNameKey = "option.java.memoryPolicy.name",
            DescriptionKey = "option.java.memoryPolicy.desc",
            Default = "auto",
            Order = 18,
            Choices =
            [
                new OptionChoice("auto", "option.java.memoryPolicy.auto"),
                new OptionChoice("manual", "option.java.memoryPolicy.manual"),
            ],
        };

        yield return new OptionDefinition<int>
        {
            Key = "java.defaultMemoryMb",
            Kind = OptionKind.Integer,
            Scope = OptionScope.Global,
            CategoryKey = "category.java",
            DisplayNameKey = "option.java.defaultMemoryMb.name",
            DescriptionKey = "option.java.defaultMemoryMb.desc",
            UnitKey = "unit.mb",
            Default = 4096,
            Min = 1024,
            Max = 65536,
            Step = 512,
            VisibleWhenKey = "java.memoryPolicy",
            VisibleWhenValue = "manual",
            Order = 20,
        };

        yield return new OptionDefinition<string>
        {
            Key = "java.extraArguments",
            Kind = OptionKind.Text,
            Scope = OptionScope.Global,
            CategoryKey = "category.java",
            DisplayNameKey = "option.java.extraArguments.name",
            DescriptionKey = "option.java.extraArguments.desc",
            Default = string.Empty,
            Order = 30,
        };

        yield return new OptionDefinition<string>
        {
            Key = "java.jvmPreset",
            Kind = OptionKind.Select,
            Scope = OptionScope.Global,
            CategoryKey = "category.java",
            DisplayNameKey = "option.java.jvmPreset.name",
            DescriptionKey = "option.java.jvmPreset.desc",
            Default = "g1",
            Order = 35,
            Choices =
            [
                new OptionChoice("default", "option.java.jvmPreset.default"),
                new OptionChoice("g1", "option.java.jvmPreset.g1"),
                new OptionChoice("zgc", "option.java.jvmPreset.zgc"),
                new OptionChoice("aikar", "option.java.jvmPreset.aikar"),
            ],
        };

        yield return new OptionDefinition<string>
        {
            Key = "java.runtimeProvider",
            Kind = OptionKind.Select,
            Scope = OptionScope.Global,
            CategoryKey = "category.java",
            DisplayNameKey = "option.java.runtimeProvider.name",
            DescriptionKey = "option.java.runtimeProvider.desc",
            Default = "mojang",
            Order = 40,
            Choices =
            [
                new OptionChoice("mojang", "option.java.runtimeProvider.mojang"),
                new OptionChoice("adoptium", "option.java.runtimeProvider.adoptium"),
                new OptionChoice("zulu", "option.java.runtimeProvider.zulu"),
                new OptionChoice("liberica", "option.java.runtimeProvider.liberica"),
            ],
        };

        yield return new OptionDefinition<int>
        {
            Key = "game.windowWidth",
            Kind = OptionKind.Integer,
            Scope = OptionScope.Global,
            CategoryKey = "category.game",
            DisplayNameKey = "option.game.windowWidth.name",
            DescriptionKey = "option.game.windowWidth.desc",
            UnitKey = "unit.px",
            Default = 1280,
            Min = 854,
            Max = 7680,
            Step = 1,
            Order = 10,
        };

        yield return new OptionDefinition<int>
        {
            Key = "game.windowHeight",
            Kind = OptionKind.Integer,
            Scope = OptionScope.Global,
            CategoryKey = "category.game",
            DisplayNameKey = "option.game.windowHeight.name",
            DescriptionKey = "option.game.windowHeight.desc",
            UnitKey = "unit.px",
            Default = 720,
            Min = 480,
            Max = 4320,
            Step = 1,
            Order = 20,
        };

        // 默认全屏（settings-game-launch.md §6）。窗口宽高在开启全屏时仍保留，
        // 关闭全屏后可继续使用；页面只展示 ECL 提供的启动默认值。
        yield return new OptionDefinition<bool>
        {
            Key = "game.fullscreen",
            Kind = OptionKind.Boolean,
            Scope = OptionScope.Global,
            CategoryKey = "category.game",
            DisplayNameKey = "option.game.fullscreen.name",
            DescriptionKey = "option.game.fullscreen.desc",
            Default = false,
            Order = 25,
        };

        yield return new OptionDefinition<bool>
        {
            Key = "game.showConsole",
            Kind = OptionKind.Boolean,
            Scope = OptionScope.Global,
            CategoryKey = "category.game",
            DisplayNameKey = "option.game.showConsole.name",
            DescriptionKey = "option.game.showConsole.desc",
            Default = false,
            Order = 40,
        };

        yield return new OptionDefinition<string>
        {
            Key = "game.extraArguments",
            Kind = OptionKind.Text,
            Scope = OptionScope.Global,
            CategoryKey = "category.game",
            DisplayNameKey = "option.game.extraArguments.name",
            DescriptionKey = "option.game.extraArguments.desc",
            Default = string.Empty,
            Order = 60,
        };
    }
}
