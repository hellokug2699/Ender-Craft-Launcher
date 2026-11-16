// SPDX-License-Identifier: GPL-3.0-or-later

using System.Text;

namespace Ecl.Core.Storage;

/// <summary>
/// 跨进程文件锁（F-02 §8.7）。锁文件位于 ECL 数据目录 <c>locks/</c> 下，
/// 用独占句柄实现互斥；句柄关闭即释放。文件本身保留，避免删除与打开之间的竞态。
/// 等待有界：超时返回 <c>null</c>，调用方必须返回稳定的 busy 错误码，不得无限等待 UI。
/// </summary>
public sealed class CrossProcessFileLock : IDisposable
{
    private readonly FileStream _stream;
    private int _disposed;

    private CrossProcessFileLock(FileStream stream) => _stream = stream;

    public string LockPath { get; private init; } = string.Empty;

    /// <summary>
    /// 在 <paramref name="dataRoot"/> 下获取名为 <paramref name="relativeName"/>（例如
    /// <c>instance-engine/workspace.lock</c>）的跨进程锁。非 Windows 平台仍使用同一
    /// 文件独占语义（.NET 的 FileShare 由运行时保证），失败时返回 <c>null</c>。
    /// </summary>
    public static CrossProcessFileLock? TryAcquire(
        string dataRoot,
        string relativeName,
        TimeSpan timeout,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(dataRoot) || string.IsNullOrWhiteSpace(relativeName))
        {
            return null;
        }

        var path = Path.Combine(dataRoot, "locks", "instance-engine", relativeName);
        var directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(directory))
        {
            try
            {
                Directory.CreateDirectory(directory);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                return null;
            }
        }

        var deadline = DateTimeOffset.UtcNow + (timeout <= TimeSpan.Zero ? TimeSpan.FromMilliseconds(1) : timeout);
        do
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                var stream = new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None, 1, FileOptions.None);
                try
                {
                    stream.SetLength(0);
                    var payload = Encoding.UTF8.GetBytes(
                        $"pid={Environment.ProcessId};started={DateTimeOffset.UtcNow:O}");
                    stream.Write(payload, 0, payload.Length);
                    stream.Flush(flushToDisk: true);
                }
                catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
                {
                    // 诊断元数据失败不影响锁本身。
                }

                return new CrossProcessFileLock(stream) { LockPath = path };
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                if (DateTimeOffset.UtcNow >= deadline)
                {
                    return null;
                }
            }

            try
            {
                Thread.Sleep(25);
            }
            catch (ThreadInterruptedException)
            {
                return null;
            }
        }
        while (DateTimeOffset.UtcNow < deadline);

        return null;
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        try
        {
            _stream.Dispose();
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // 释放失败已无补救手段；锁随进程退出释放。
        }
    }
}
