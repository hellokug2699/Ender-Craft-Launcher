// SPDX-License-Identifier: GPL-3.0-or-later

using System.Buffers.Binary;

namespace Ecl.Java;

public enum JavaBitness
{
    Unknown,
    Bit32,
    Bit64,
}

/// <summary>
/// Java 运行时的架构/位数判定。32 位 Java 不能用于 Minecraft 启动，
/// 因此这里把“未知”与“32 位”严格区分：未知不能被当作 64 位使用。
/// </summary>
public static class JavaArchitecture
{
    private static readonly HashSet<string> Known64Bit = new(StringComparer.OrdinalIgnoreCase)
    {
        "amd64",
        "x86_64",
        "x86-64",
        "x64",
        "aarch64",
        "arm64",
        "ppc64",
        "ppc64le",
        "s390x",
        "riscv64",
        "mips64",
        "mips64el",
        "ia64",
    };

    private static readonly HashSet<string> Known32Bit = new(StringComparer.OrdinalIgnoreCase)
    {
        "x86",
        "i386",
        "i486",
        "i586",
        "i686",
        "x86_32",
        "x86-32",
        "x32",
        "arm",
        "arm32",
        "armv7",
        "armv7l",
        "armhf",
        "ppc",
        "mips",
        "mipsel",
        "riscv32",
    };

    public static JavaBitness Classify(string? architecture)
    {
        if (string.IsNullOrWhiteSpace(architecture))
        {
            return JavaBitness.Unknown;
        }

        var value = architecture.Trim().ToLowerInvariant();

        // 先匹配明确的 64 位标记，避免 "x86_64" 被 "x86" 误判。
        if (Known64Bit.Contains(value) ||
            value.Contains("64", StringComparison.Ordinal) ||
            value.Contains("aarch64", StringComparison.Ordinal))
        {
            return JavaBitness.Bit64;
        }

        if (Known32Bit.Contains(value) ||
            value.Contains("32", StringComparison.Ordinal) ||
            (value.Contains("x86", StringComparison.Ordinal) && !value.Contains("64", StringComparison.Ordinal)) ||
            (value.Contains("arm", StringComparison.Ordinal) &&
             !value.Contains("64", StringComparison.Ordinal) &&
             !value.Contains("aarch64", StringComparison.Ordinal)))
        {
            return JavaBitness.Bit32;
        }

        return JavaBitness.Unknown;
    }

    public static bool Is32Bit(string? architecture) => Classify(architecture) == JavaBitness.Bit32;

    public static bool Is64Bit(string? architecture) => Classify(architecture) == JavaBitness.Bit64;

    /// <summary>
    /// 读取可执行文件的 PE/ELF 头来判定位数，作为 release 文件缺少 OS_ARCH 时的补充证据。
    /// 只读取少量头部字节，不执行 Java 进程。
    /// </summary>
    public static JavaBitness DetectExecutableBitness(string executablePath)
    {
        if (string.IsNullOrWhiteSpace(executablePath) || !File.Exists(executablePath))
        {
            return JavaBitness.Unknown;
        }

        try
        {
            using var stream = new FileStream(
                executablePath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete);
            Span<byte> header = stackalloc byte[64];
            var read = stream.ReadAtLeast(header, header.Length, throwOnEndOfStream: false);
            if (read < 20)
            {
                return JavaBitness.Unknown;
            }

            // PE：MZ ... e_lfanew -> PE\0\0 + machine。
            if (header[0] == (byte)'M' && header[1] == (byte)'Z')
            {
                var peOffset = BinaryPrimitives.ReadInt32LittleEndian(header[0x3C..0x40]);
                if (peOffset <= 0 || peOffset > 1024 * 1024)
                {
                    return JavaBitness.Unknown;
                }

                Span<byte> peHeader = stackalloc byte[6];
                stream.Position = peOffset;
                if (stream.Read(peHeader) < peHeader.Length ||
                    peHeader[0] != (byte)'P' ||
                    peHeader[1] != (byte)'E' ||
                    peHeader[2] != 0 ||
                    peHeader[3] != 0)
                {
                    return JavaBitness.Unknown;
                }

                var machine = BinaryPrimitives.ReadUInt16LittleEndian(peHeader[4..6]);
                return machine switch
                {
                    0x014C => JavaBitness.Bit32, // IMAGE_FILE_MACHINE_I386
                    0x8664 => JavaBitness.Bit64, // IMAGE_FILE_MACHINE_AMD64
                    0xAA64 => JavaBitness.Bit64, // IMAGE_FILE_MACHINE_ARM64
                    0x01C4 => JavaBitness.Bit32, // IMAGE_FILE_MACHINE_ARMNT
                    0x0200 => JavaBitness.Bit64, // IMAGE_FILE_MACHINE_IA64
                    0x5032 => JavaBitness.Bit32, // IMAGE_FILE_MACHINE_RISCV32
                    0x5064 => JavaBitness.Bit64, // IMAGE_FILE_MACHINE_RISCV64
                    _ => JavaBitness.Unknown,
                };
            }

            // ELF：e_ident[EI_CLASS] 1=32 位，2=64 位。
            if (header[0] == 0x7F && header[1] == (byte)'E' && header[2] == (byte)'L' && header[3] == (byte)'F')
            {
                return header[4] switch
                {
                    1 => JavaBitness.Bit32,
                    2 => JavaBitness.Bit64,
                    _ => JavaBitness.Unknown,
                };
            }

            // macOS 通用二进制/Mach-O：头部魔数与 cputype。
            var magic = BinaryPrimitives.ReadUInt32BigEndian(header[0..4]);
            if (magic is 0xCAFEBABE or 0xCAFEBABF)
            {
                return JavaBitness.Unknown;
            }

            if (magic is 0xFEEDFACE or 0xCEFAEDFE)
            {
                return JavaBitness.Bit32;
            }

            if (magic is 0xFEEDFACF or 0xCFFAEDFE)
            {
                return JavaBitness.Bit64;
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // 无法读取的候选不阻断发现；位数保持 Unknown。
        }

        return JavaBitness.Unknown;
    }

    public static string Describe(JavaBitness bitness) => bitness switch
    {
        JavaBitness.Bit32 => "32-bit",
        JavaBitness.Bit64 => "64-bit",
        _ => "unknown",
    };
}
