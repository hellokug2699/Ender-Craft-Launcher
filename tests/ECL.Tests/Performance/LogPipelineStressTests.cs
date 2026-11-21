// SPDX-License-Identifier: GPL-3.0-or-later

using System.Diagnostics;
using System.Text;
using Ecl.Sessions;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Ecl.Tests.Performance;

/// <summary>
/// 大型日志管线压力回归（性能交接 §6、§9.3、§9.5）：
/// 历史读取必须按偏移分段、有界且不重复；实时追加只读取新增字节。
/// 这些测试使用真实临时文件与真实 <see cref="SessionLogReader"/>。
/// </summary>
public sealed class LogPipelineStressTests : IDisposable
{
    private readonly TestEnvironment _environment = new();
    private readonly SessionLogReader _reader = new();

    [Fact]
    public async Task LatestReadOfLargeFileIsBounded()
    {
        var service = Service();
        var session = service.Create(Request());
        var path = Path.Combine(session.DirectoryPath, "game.log");
        var length = WriteLines(path, lineCount: 200_000);

        var stopwatch = Stopwatch.StartNew();
        var chunk = await _reader.ReadLatestAsync(
            service.Get(session.SessionId)!,
            SessionLogSourceKind.GameLog,
            maxBytes: SessionLogReader.DefaultChunkBytes);
        stopwatch.Stop();

        Assert.NotEmpty(chunk.Lines);
        Assert.Equal(length, chunk.EndOffset);
        Assert.True(chunk.StartOffset > 0, "大文件尾部读取不应从文件头开始。");
        // 512 KB 块中每行至少几个字节：读取结果不能是整份文件。
        Assert.True(chunk.Lines.Count < 200_000, "尾部读取不得把整份历史日志读入内存。");
        Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(10), $"尾部读取耗时异常：{stopwatch.Elapsed}。");
    }

    [Fact]
    public async Task PagingBackwardsOnlyReadsTheRequestedWindow()
    {
        var service = Service();
        var session = service.Create(Request());
        var path = Path.Combine(session.DirectoryPath, "game.log");
        WriteLines(path, lineCount: 200_000);
        var entity = service.Get(session.SessionId)!;

        var latest = await _reader.ReadLatestAsync(entity, SessionLogSourceKind.GameLog, maxBytes: 256 * 1024);
        var stopwatch = Stopwatch.StartNew();
        var older = await _reader.ReadBeforeAsync(
            entity,
            SessionLogSourceKind.GameLog,
            latest.StartOffset,
            maxBytes: 256 * 1024);
        stopwatch.Stop();

        Assert.NotEmpty(older.Lines);
        Assert.True(older.StartOffset < latest.StartOffset);
        Assert.Equal(latest.StartOffset, older.EndOffset);
        Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(10), $"向前翻页耗时异常：{stopwatch.Elapsed}。");
    }

    [Fact]
    public async Task IncrementalReadReturnsOnlyNewBytesWithoutDuplicates()
    {
        var service = Service();
        var session = service.Create(Request());
        var path = Path.Combine(session.DirectoryPath, "game.log");
        await File.WriteAllTextAsync(path, string.Join('\n', Enumerable.Range(0, 1000).Select(index => $"line-{index:0000}")) + "\n");
        var entity = service.Get(session.SessionId)!;

        var first = await _reader.ReadLatestAsync(entity, SessionLogSourceKind.GameLog);
        Assert.NotEmpty(first.Lines);

        await AppendLinesAsync(path, start: 1000, count: 25);
        var delta = await _reader.ReadFromAsync(entity, SessionLogSourceKind.GameLog, first.EndOffset);

        Assert.Equal(25, delta.Lines.Count);
        Assert.All(delta.Lines, line => Assert.Contains("line-", line, StringComparison.Ordinal));
        Assert.Equal(first.EndOffset, delta.StartOffset);
    }

    [Fact]
    public async Task RepeatedTailReadsStayBounded()
    {
        var service = Service();
        var session = service.Create(Request());
        var path = Path.Combine(session.DirectoryPath, "game.log");
        WriteLines(path, lineCount: 50_000);
        var entity = service.Get(session.SessionId)!;

        var firstCount = 0;
        for (var index = 0; index < 5; index++)
        {
            var chunk = await _reader.ReadLatestAsync(entity, SessionLogSourceKind.GameLog);
            Assert.NotEmpty(chunk.Lines);
            firstCount = firstCount == 0 ? chunk.Lines.Count : firstCount;
            Assert.Equal(firstCount, chunk.Lines.Count);
        }
    }

    private GameSessionService Service() =>
        new(_environment.Paths, NullLogger<GameSessionService>.Instance);

    private static SessionCreateRequest Request() => new()
    {
        InstanceId = "stress",
        InstanceName = "Stress",
        GameDirectory = Path.Combine(Path.GetTempPath(), "ecl-missing-game"),
        MinecraftVersion = "1.21.1",
        Loader = "fabric",
        LogIsolation = "direct",
    };

    private static long WriteLines(string path, int lineCount)
    {
        var builder = new StringBuilder(lineCount * 32);
        for (var index = 0; index < lineCount; index++)
        {
            builder.Append("[12:00:00] [main/INFO]: line-").Append(index).Append('\n');
        }

        File.WriteAllText(path, builder.ToString());
        return new FileInfo(path).Length;
    }

    private static async Task AppendLinesAsync(string path, int start, int count)
    {
        var builder = new StringBuilder(count * 32);
        for (var index = start; index < start + count; index++)
        {
            builder.Append("[12:00:00] [main/INFO]: line-").Append(index).Append('\n');
        }

        await File.AppendAllTextAsync(path, builder.ToString());
    }

    public void Dispose() => _environment.Dispose();
}
