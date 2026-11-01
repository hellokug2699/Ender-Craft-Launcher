// SPDX-License-Identifier: GPL-3.0-or-later

using System.Threading.Channels;
using Ecl.Core;

namespace Ecl.Sessions;

/// <summary>
/// 有界日志写入器：管道读取与磁盘写入解耦，队列满时丢弃并记录缺口，
/// 绝不让内存无界增长，也不阻塞游戏写入端。
/// </summary>
public sealed class SessionLogWriter : IAsyncDisposable
{
    private readonly Channel<byte[]> _channel;
    private readonly FileStream _stream;
    private readonly Task _pump;
    private long _droppedBytes;
    private long _writtenBytes;
    private int _completed;

    private SessionLogWriter(string path, int capacity)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        _stream = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.Read, 64 * 1024, FileOptions.SequentialScan);
        _channel = Channel.CreateBounded<byte[]>(new BoundedChannelOptions(capacity)
        {
            FullMode = BoundedChannelFullMode.DropWrite,
            SingleReader = true,
            SingleWriter = false,
        });
        _pump = PumpAsync();
    }

    public long WrittenBytes => Interlocked.Read(ref _writtenBytes);

    public long DroppedBytes => Interlocked.Read(ref _droppedBytes);

    public static SessionLogWriter Open(string path, int capacity = 1024) => new(path, capacity);

    /// <summary>复制一份字节并投递；队列饱和时立即返回并计入丢弃量。</summary>
    public void Write(ReadOnlySpan<byte> data)
    {
        if (data.IsEmpty)
        {
            return;
        }

        var copy = data.ToArray();
        if (!_channel.Writer.TryWrite(copy))
        {
            Interlocked.Add(ref _droppedBytes, copy.Length);
        }
    }

    public async Task CompleteAsync()
    {
        if (Interlocked.Exchange(ref _completed, 1) == 1)
        {
            return;
        }

        _channel.Writer.TryComplete();
        await _pump.ConfigureAwait(false);

        if (DroppedBytes > 0)
        {
            var marker = System.Text.Encoding.UTF8.GetBytes(
                $"\n[ecl] 采集队列饱和，丢弃 {DroppedBytes} 字节；日志不完整。\n");
            await _stream.WriteAsync(marker).ConfigureAwait(false);
            Interlocked.Exchange(ref _writtenBytes, _writtenBytes + marker.Length);
        }

        await _stream.FlushAsync().ConfigureAwait(false);
        _stream.Flush(flushToDisk: true);
    }

    public async ValueTask DisposeAsync()
    {
        await CompleteAsync().ConfigureAwait(false);
        await _stream.DisposeAsync().ConfigureAwait(false);
    }

    private async Task PumpAsync()
    {
        try
        {
            await foreach (var chunk in _channel.Reader.ReadAllAsync().ConfigureAwait(false))
            {
                await _stream.WriteAsync(chunk).ConfigureAwait(false);
                Interlocked.Add(ref _writtenBytes, chunk.Length);
                if (_stream.Position >= 256 * 1024)
                {
                    await _stream.FlushAsync().ConfigureAwait(false);
                }
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ObjectDisposedException)
        {
            // 写盘失败由会话捕获状态记录；读循环仍在继续，避免阻塞游戏。
        }
    }
}
