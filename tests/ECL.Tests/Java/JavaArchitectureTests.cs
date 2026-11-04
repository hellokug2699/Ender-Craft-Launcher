// SPDX-License-Identifier: GPL-3.0-or-later

using System.Buffers.Binary;
using Ecl.Java;
using Xunit;

namespace Ecl.Tests.Java;

public sealed class JavaArchitectureTests
{
    [Theory]
    [InlineData("x86", JavaBitness.Bit32)]
    [InlineData("i386", JavaBitness.Bit32)]
    [InlineData("x86_32", JavaBitness.Bit32)]
    [InlineData("arm", JavaBitness.Bit32)]
    [InlineData("armv7l", JavaBitness.Bit32)]
    [InlineData("amd64", JavaBitness.Bit64)]
    [InlineData("x86_64", JavaBitness.Bit64)]
    [InlineData("aarch64", JavaBitness.Bit64)]
    [InlineData("arm64", JavaBitness.Bit64)]
    [InlineData(null, JavaBitness.Unknown)]
    [InlineData("", JavaBitness.Unknown)]
    [InlineData("sparc", JavaBitness.Unknown)]
    public void ClassifiesCommonArchitectureNames(string? architecture, JavaBitness expected)
    {
        Assert.Equal(expected, JavaArchitecture.Classify(architecture));
    }

    [Fact]
    public void ReadsPeMachineWhenReleaseMetadataIsMissing()
    {
        var root = Path.Combine(Path.GetTempPath(), "ecl-pe-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var x64 = WritePe(Path.Combine(root, "java64.exe"), 0x8664);
            var x86 = WritePe(Path.Combine(root, "java32.exe"), 0x014C);
            var arm64 = WritePe(Path.Combine(root, "javaarm64.exe"), 0xAA64);

            Assert.Equal(JavaBitness.Bit64, JavaArchitecture.DetectExecutableBitness(x64));
            Assert.Equal(JavaBitness.Bit32, JavaArchitecture.DetectExecutableBitness(x86));
            Assert.Equal(JavaBitness.Bit64, JavaArchitecture.DetectExecutableBitness(arm64));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void ReadsElfClassBits()
    {
        var root = Path.Combine(Path.GetTempPath(), "ecl-elf-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var elf64 = Path.Combine(root, "java64");
            var elf32 = Path.Combine(root, "java32");
            var elf64Bytes = new byte[32];
            var elf32Bytes = new byte[32];
            elf64Bytes[0] = 0x7F;
            elf64Bytes[1] = (byte)'E';
            elf64Bytes[2] = (byte)'L';
            elf64Bytes[3] = (byte)'F';
            elf64Bytes[4] = 2;
            elf32Bytes[0] = 0x7F;
            elf32Bytes[1] = (byte)'E';
            elf32Bytes[2] = (byte)'L';
            elf32Bytes[3] = (byte)'F';
            elf32Bytes[4] = 1;
            File.WriteAllBytes(elf64, elf64Bytes);
            File.WriteAllBytes(elf32, elf32Bytes);

            Assert.Equal(JavaBitness.Bit64, JavaArchitecture.DetectExecutableBitness(elf64));
            Assert.Equal(JavaBitness.Bit32, JavaArchitecture.DetectExecutableBitness(elf32));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private static string WritePe(string path, ushort machine)
    {
        var bytes = new byte[0x100];
        bytes[0] = (byte)'M';
        bytes[1] = (byte)'Z';
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(0x3C, 4), 0x80);
        bytes[0x80] = (byte)'P';
        bytes[0x81] = (byte)'E';
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(0x84, 2), machine);
        File.WriteAllBytes(path, bytes);
        return path;
    }
}
