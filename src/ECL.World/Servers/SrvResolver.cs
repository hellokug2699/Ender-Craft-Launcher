// SPDX-License-Identifier: GPL-3.0-or-later

using System.Runtime.InteropServices;

namespace Ecl.World.Servers;

/// <summary>
/// Minecraft Java Edition 的 SRV 解析（<c>_minecraft._tcp.&lt;host&gt;</c>）。
/// Windows 使用系统 DNS 客户端；其他平台返回 null，由调用方回退到默认端口。
/// </summary>
internal static class SrvResolver
{
    private const ushort DnsTypeSrv = 33;
    private const int DnsFreeRecordList = 1;

    public static Task<ServerAddress?> ResolveAsync(string host, CancellationToken cancellationToken) =>
        Task.Run(
            () =>
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!OperatingSystem.IsWindows() || string.IsNullOrWhiteSpace(host))
                {
                    return (ServerAddress?)null;
                }

                var queryName = $"_minecraft._tcp.{host.TrimEnd('.')}";
                var status = DnsQuery(queryName, DnsTypeSrv, 0, IntPtr.Zero, out var results, IntPtr.Zero);
                if (status != 0 || results == IntPtr.Zero)
                {
                    return null;
                }

                try
                {
                    var candidates = new List<(ushort Priority, ushort Weight, string Host, ushort Port)>();
                    var current = results;
                    while (current != IntPtr.Zero)
                    {
                        var record = Marshal.PtrToStructure<DnsRecord>(current);
                        if (record.Type == DnsTypeSrv && record.DataLength >= 8)
                        {
                            var dataPointer = IntPtr.Add(current, Marshal.SizeOf<DnsRecord>());
                            var data = Marshal.PtrToStructure<DnsSrvData>(dataPointer);
                            var target = Marshal.PtrToStringUni(data.TargetName)?.TrimEnd('.');
                            if (!string.IsNullOrWhiteSpace(target))
                            {
                                candidates.Add((data.Priority, data.Weight, target, data.Port));
                            }
                        }

                        current = record.Next;
                    }

                    var selected = candidates
                        .OrderBy(candidate => candidate.Priority)
                        .ThenByDescending(candidate => candidate.Weight)
                        .FirstOrDefault();
                    return selected.Host is null
                        ? null
                        : (ServerAddress?)new ServerAddress(selected.Host, selected.Port);
                }
                finally
                {
                    DnsFree(results, DnsFreeRecordList);
                }
            },
            cancellationToken);

    [DllImport("dnsapi.dll", EntryPoint = "DnsQuery_W", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern int DnsQuery(
        string name,
        ushort type,
        uint options,
        IntPtr extra,
        out IntPtr results,
        IntPtr reserved);

    [DllImport("dnsapi.dll")]
    private static extern void DnsFree(IntPtr pointer, int freeType);

    [StructLayout(LayoutKind.Sequential)]
    private struct DnsRecord
    {
        public IntPtr Next;
        public IntPtr Name;
        public ushort Type;
        public ushort DataLength;
        public uint Flags;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct DnsSrvData
    {
        public IntPtr TargetName;
        public ushort Priority;
        public ushort Weight;
        public ushort Port;
        public ushort Padding;
    }
}
