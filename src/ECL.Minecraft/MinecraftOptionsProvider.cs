// SPDX-License-Identifier: GPL-3.0-or-later

using Ecl.Configuration;

namespace Ecl.Minecraft;

/// <summary>实例作用域的配置项由 Minecraft 模块拥有，便于以后扩展加载器/整合包相关选项。</summary>
internal sealed class MinecraftOptionsProvider : IOptionProvider
{
    public IEnumerable<OptionDefinition> GetOptions()
    {
        yield return new OptionDefinition<int?>
        {
            Key = "instance.java.memoryMb",
            Kind = OptionKind.Integer,
            Scope = OptionScope.Instance,
            CategoryKey = "category.instance",
            DisplayNameKey = "option.instance.memoryMb.name",
            DescriptionKey = "option.instance.memoryMb.desc",
            UnitKey = "unit.mb",
            Default = null,
            EmptyIsNull = true,
            Min = 1024,
            Max = 65536,
            Step = 512,
            Order = 10,
        };

        yield return new OptionDefinition<string?>
        {
            Key = "instance.java.executablePath",
            Kind = OptionKind.Path,
            Scope = OptionScope.Instance,
            CategoryKey = "category.instance",
            DisplayNameKey = "option.instance.javaPath.name",
            DescriptionKey = "option.instance.javaPath.desc",
            Default = null,
            EmptyIsNull = true,
            Order = 20,
        };

        yield return new OptionDefinition<bool>
        {
            Key = "instance.java.disableCompatibilityCheck",
            Kind = OptionKind.Boolean,
            Scope = OptionScope.Instance,
            CategoryKey = "category.instance",
            DisplayNameKey = "option.instance.disableJavaCheck.name",
            DescriptionKey = "option.instance.disableJavaCheck.desc",
            Default = false,
            Order = 25,
        };

        yield return new OptionDefinition<string?>
        {
            Key = "instance.java.extraArguments",
            Kind = OptionKind.Text,
            Scope = OptionScope.Instance,
            CategoryKey = "category.instance",
            DisplayNameKey = "option.instance.jvmArgs.name",
            DescriptionKey = "option.instance.jvmArgs.desc",
            Default = null,
            EmptyIsNull = true,
            Order = 30,
        };

        yield return new OptionDefinition<string?>
        {
            Key = "instance.game.extraArguments",
            Kind = OptionKind.Text,
            Scope = OptionScope.Instance,
            CategoryKey = "category.instance",
            DisplayNameKey = "option.instance.gameArgs.name",
            DescriptionKey = "option.instance.gameArgs.desc",
            Default = null,
            EmptyIsNull = true,
            Order = 40,
        };
    }
}
