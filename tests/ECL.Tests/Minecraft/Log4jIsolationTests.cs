// SPDX-License-Identifier: GPL-3.0-or-later

using Ecl.Minecraft.Launch;
using Xunit;

namespace Ecl.Tests.Minecraft;

public sealed class Log4jIsolationTests : IDisposable
{
    private readonly string _root;
    private readonly string _session;

    public Log4jIsolationTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "ecl-log4j-tests", Guid.NewGuid().ToString("N"));
        _session = Path.Combine(_root, "session");
        Directory.CreateDirectory(_session);
    }

    private string WriteConfig(string content)
    {
        var path = Path.Combine(_root, "client-1.12.xml");
        File.WriteAllText(path, content);
        return path;
    }

    private const string DefaultConfig = """
        <?xml version="1.0" encoding="UTF-8"?>
        <Configuration status="WARN">
            <Appenders>
                <Console name="SysOut" target="SYSTEM_OUT"><LegacyXMLLayout /></Console>
                <RollingRandomAccessFile name="File" fileName="logs/latest.log" filePattern="logs/%d{yyyy-MM-dd}-%i.log.gz">
                    <PatternLayout pattern="[%d{HH:mm:ss}] [%t/%level]: %msg{nolookups}%n" />
                    <Policies><TimeBasedTriggeringPolicy /><OnStartupTriggeringPolicy /></Policies>
                </RollingRandomAccessFile>
            </Appenders>
            <Loggers><Root level="info"><AppenderRef ref="SysOut"/><AppenderRef ref="File"/></Root></Loggers>
        </Configuration>
        """;

    [Fact]
    public void DefaultMojangConfigIsDerivedIntoSessionDirectory()
    {
        var config = WriteConfig(DefaultConfig);
        var command = new LaunchCommand("java", [$"-Dlog4j.configurationFile={config}", "Main"], "java");

        var result = Log4jIsolation.Prepare(command, _session);

        Assert.True(result.IsSuccess, result.Error?.ToString());
        var plan = result.Value!;
        Assert.Equal("pending", plan.Mode);
        Assert.NotNull(plan.ConfigPath);
        Assert.True(File.Exists(plan.ConfigPath));
        Assert.Equal(32, plan.Fingerprint!.Length);

        var derived = File.ReadAllText(plan.ConfigPath!);
        Assert.Contains(Path.Combine(_session, "game.log"), derived, StringComparison.Ordinal);
        Assert.DoesNotContain("logs/latest.log", derived, StringComparison.Ordinal);
        Assert.Contains(plan.ConfigPath!, plan.Arguments[0], StringComparison.Ordinal);
        // 原始配置不被修改。
        Assert.Contains("logs/latest.log", File.ReadAllText(config), StringComparison.Ordinal);
    }

    [Fact]
    public void CustomConfigIsLeftShared()
    {
        var config = WriteConfig("<Configuration><Appenders><File name=\"X\" fileName=\"custom.log\" /></Appenders></Configuration>");
        var command = new LaunchCommand("java", [$"-Dlog4j.configurationFile={config}", "Main"], "java");

        var result = Log4jIsolation.Prepare(command, _session);

        var plan = result.Value!;
        Assert.Equal("shared", plan.Mode);
        Assert.Null(plan.ConfigPath);
        Assert.Equal(command.Arguments, plan.Arguments);
        Assert.Contains("custom.log", File.ReadAllText(config), StringComparison.Ordinal);
    }

    [Fact]
    public void MissingArgumentReportsUnavailable()
    {
        var command = new LaunchCommand("java", ["Main"], "java");

        var result = Log4jIsolation.Prepare(command, _session);

        Assert.Equal("unavailable", result.Value!.Mode);
        Assert.Equal(command.Arguments, result.Value.Arguments);
    }

    [Fact]
    public void MissingConfigFileReportsUnavailable()
    {
        var command = new LaunchCommand(
            "java",
            [@"-Dlog4j.configurationFile=C:\does-not-exist\client.xml", "Main"],
            "java");

        var result = Log4jIsolation.Prepare(command, _session);

        Assert.Equal("unavailable", result.Value!.Mode);
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_root))
            {
                Directory.Delete(_root, recursive: true);
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // 清理失败不影响测试结论。
        }
    }
}
