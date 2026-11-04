// SPDX-License-Identifier: GPL-3.0-or-later

using System.Text;
using System.Text.Json;
using Ecl.Core.Json;

namespace Ecl.Sessions;

/// <summary>带序号的追加式生命周期事件。</summary>
public sealed record SessionEvent(long Sequence, DateTimeOffset TimestampUtc, string Type, string? Detail = null);

/// <summary>
/// 会话 journal：逐行追加、带序号。恢复时只忽略最后一条不完整记录，
/// 之前的有效记录必须保留。
/// </summary>
public static class SessionJournal
{
    public static void Append(string path, SessionEvent entry)
    {
        var line = JsonSerializer.Serialize(entry, EclJson.Default) + "\n";
        using var stream = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.Read);
        var bytes = Encoding.UTF8.GetBytes(line);
        stream.Write(bytes);
        stream.Flush(flushToDisk: true);
    }

    public static IReadOnlyList<SessionEvent> ReadAll(string path, out bool ignoredTornRecord)
    {
        ignoredTornRecord = false;
        if (!File.Exists(path))
        {
            return [];
        }

        var events = new List<SessionEvent>();
        var lines = File.ReadAllLines(path);
        for (var index = 0; index < lines.Length; index++)
        {
            var line = lines[index];
            if (string.IsNullOrWhiteSpace(line))
            {
                continue;
            }

            try
            {
                var entry = JsonSerializer.Deserialize<SessionEvent>(line, EclJson.Default);
                if (entry is not null)
                {
                    events.Add(entry);
                }
            }
            catch (JsonException)
            {
                // 只有最后一行允许是写入中断留下的半条记录。
                if (index == lines.Length - 1)
                {
                    ignoredTornRecord = true;
                }
            }
        }

        return events;
    }

    public static long LastSequence(string path)
    {
        var events = ReadAll(path, out _);
        return events.Count == 0 ? 0 : events[^1].Sequence;
    }
}
