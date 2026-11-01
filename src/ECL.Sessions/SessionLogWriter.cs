// SPDX-License-Identifier: GPL-3.0-or-later

using System.Threading.Channels;
using Ecl.Core;

namespace Ecl.Sessions;

/// <summary>
/// 有界日志写入器：管道读取与磁盘写入解耦，队列满时丢弃并记录缺口，
/// 绝不让内存无界增长，也不阻塞游戏写入端。写盘失败会被记录为可观测故障。
/// </summary>
public sealed class SessionLogWriter : IAsyncDisposable
{
    private readonly Channel<byte[]> _channel;
    private readonly Stream _stream;
    private readonly bool _ownsStream;
    private readonly Task _pump;
    private long _droppedBytes;
    private long _writtenBytes;
    private int _completed;
    private int _disposed;
    private string? _fault;

    internal SessionLogWriter(Stream stream, int capacity, bool ownsStream)
    {
        _stream = stream;
        _ownsStream = ownsStream;
        _channel = Channel.CreateBounded<byte[]>(new BoundedChannelOptions(capacity)
        {
            // Wait 模式下 TryWrite 在队列满时返回 false，而不是“接受后丢弃”：
            // 我们据此显式累计丢弃字节，保证写入与丢弃之和等于输入总量。
            FullMode = BoundedChannelFullMode.Wait,
            SingleReader = true,
            SingleWriter = false,
        });
        _pump = PumpAsync();
    }

    public long WrittenBytes => Interlocked.Read(ref _writtenBytes);

    public long DroppedBytes => Interlocked.Read(ref _droppedBytes);

    /// <summary>写入失败（磁盘满、只读、卷离线等）的原因；null 表示未观察到故障。</summary>
    public string? Fault => Volatile.Read(ref _fault);

    public static SessionLogWriter Open(string path, int capacity = 1024)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var stream = new FileStream(
            path,
            FileMode.Append,
            FileAccess.Write,
            FileShare.ReadWrite | FileShare.Delete,
            64 * 1024,
            FileOptions.SequentialScan);
        return new SessionLogWriter(stream, capacity, ownsStream: true);
    }

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

    /// <summary>把已写入的缓冲刷新到磁盘；不结束采集。</summary>
    public async Task FlushAsync()
    {
        if (Volatile.Read(ref _disposed) == 1)
        {
            return;
        }

        try
        {
            await _stream.FlushAsync().ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is IOException or ObjectDisposedException or NotSupportedException)
        {
            Interlocked.CompareExchange(ref _fault, exception.Message, null);
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

        if (Fault is not null)
        {
            await DisposeStreamAsync().ConfigureAwait(false);
            return;
        }

        try
        {
            if (DroppedBytes > 0)
            {
                var marker = System.Text.Encoding.UTF8.GetBytes(
                    $"\n[ecl] 采集队列饱和，丢弃 {DroppedBytes} 字节；日志不完整。\n");
                await _stream.WriteAsync(marker).ConfigureAwait(false);
                Interlocked.Add(ref _writtenBytes, marker.Length);
            }

            await _stream.FlushAsync().ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is IOException or ObjectDisposedException or NotSupportedException)
        {
            Interlocked.CompareExchange(ref _fault, exception.Message, null);
        }

        await DisposeStreamAsync().ConfigureAwait(false);
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 1)
        {
            return;
        }

        await CompleteAsync().ConfigureAwait(false);
    }

    private async Task PumpAsync()
    {
        try
        {
            await foreach (var chunk in _channel.Reader.ReadAllAsync().ConfigureAwait(false))
            {
                if (Fault is not null)
                {
                    // 写入已失败：继续消费队列但计为丢弃，避免内存增长。
                    Interlocked.Add(ref _droppedBytes, chunk.Length);
                    continue;
                }

                try
                {
                    await _stream.WriteAsync(chunk).ConfigureAwait(false);
                    Interlocked.Add(ref _writtenBytes, chunk.Length);
                    if (CanFlushPeriodically())
                    {
                        await _stream.FlushAsync().ConfigureAwait(false);
                    }
                }
                catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ObjectDisposedException or NotSupportedException)
                {
                    Interlocked.CompareExchange(ref _fault, exception.Message, null);
                    Interlocked.Add(ref _droppedBytes, chunk.Length);
                }
            }
        }
        catch (Exception exception) when (exception is IOException or ObjectDisposedException)
        {
            Interlocked.CompareExchange(ref _fault, exception.Message, null);
        }
    }

    private bool CanFlushPeriodically()
    {
        try
        {
            return _stream.CanSeek && _stream.Position >= 256 * 1024;
        }
        catch (ObjectDisposedException)
        {
            return false;
        }
    }

    private async Task DisposeStreamAsync()
    {
        if (!_ownsStream)
        {
            return;
        }

        try
        {
            await _stream.DisposeAsync().ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is IOException or ObjectDisposedException)
        {
            Interlocked.CompareExchange(ref _fault, exception.Message, null);
        }
    }
}
