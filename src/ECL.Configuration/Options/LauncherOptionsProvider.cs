// SPDX-License-Identifier: GPL-3.0-or-later

using Ecl.Core.Localization;

namespace Ecl.Configuration.Options;

internal sealed class LauncherOptionsProvider(ILocalizer localizer) : IOptionProvider
{
    public IEnumerable<OptionDefinition> GetOptions()
    {
        var defaultLanguage = localizer.Culture.Name;

        yield return new OptionDefinition<string>
        {
            Key = "launcher.language",
            Kind = OptionKind.Select,
            Scope = OptionScope.Global,
            CategoryKey = "category.launcher",
            DisplayNameKey = "option.launcher.language.name",
            DescriptionKey = "option.launcher.language.desc",
            Default = defaultLanguage,
            RequiresRestart = true,
            Order = 10,
            Choices =
            [
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
            Default = "system",
            Order = 20,
            Choices =
            [
                new OptionChoice("system", "option.network.proxyMode.system"),
                new OptionChoice("none", "option.network.proxyMode.none"),
                new OptionChoice("manual", "option.network.proxyMode.manual"),
            ],
        };

        yield return new OptionDefinition<string>
        {
            Key = "network.proxyAddress",
            Kind = OptionKind.Text,
            Scope = OptionScope.Global,
            CategoryKey = "category.network",
            DisplayNameKey = "option.network.proxyAddress.name",
            DescriptionKey = "option.network.proxyAddress.desc",
            Default = string.Empty,
            VisibleWhenKey = "network.proxyMode",
            VisibleWhenValue = "manual",
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
            UnitKey = "unit.px",
            Default = 720,
            Min = 480,
            Max = 4320,
            Step = 1,
            Order = 20,
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
