// SPDX-License-Identifier: GPL-3.0-or-later

using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Ecl.Core;
using Microsoft.Extensions.Logging;

namespace Ecl.Configuration.Network;

/// <summary>
/// 代理认证信息的 Windows 用户范围保护存储（D-04）：
/// 使用 DPAPI <c>CurrentUser</c> 加密后写入 <c>config/proxy-credentials.dat</c>。
///
/// - 密码与用户名都在加密载荷内，不进入 <c>global.json</c>、日志、诊断包或可序列化 UI 状态；
/// - 文件是二进制 DPAPI blob，不是普通 JSON；
/// - 按规范化端点（协议 + 主机 + 端口）分别保存，切换代理端点不会复用旧凭据；
/// - 写入使用临时文件 + 原子替换；读取失败如实返回错误，不当作“没有凭据”。
/// </summary>
public sealed class WindowsDpapiProxyCredentialStore : IProxyCredentialStore
{
    private const string FileName = "proxy-credentials.dat";

    private static readonly byte[] Entropy = SHA256.HashData(
        Encoding.UTF8.GetBytes("EnderCraftLauncher.ProxyCredentials.v1"));

    private readonly string _path;
    private readonly ILogger<WindowsDpapiProxyCredentialStore> _logger;
    private readonly object _gate = new();

    public WindowsDpapiProxyCredentialStore(
        IAppPaths paths,
        ILogger<WindowsDpapiProxyCredentialStore> logger)
    {
        ArgumentNullException.ThrowIfNull(paths);
        _path = Path.Combine(paths.ConfigDirectory, FileName);
        _logger = logger;
    }

    public bool IsSupported => OperatingSystem.IsWindows();

    public Result<ProxyCredential?> Read(ProxyEndpoint endpoint)
    {
        ArgumentNullException.ThrowIfNull(endpoint);
        if (!IsSupported)
        {
            return Result<ProxyCredential?>.Fail(
                "network.credentials_unsupported",
                "当前平台不支持受保护的代理凭据存储。");
        }

        lock (_gate)
        {
            var loaded = Load();
            if (!loaded.IsSuccess)
            {
                return Result<ProxyCredential?>.Fail(loaded.Error!);
            }

            return loaded.Value!.Endpoints.TryGetValue(Key(endpoint), out var stored)
                ? Result<ProxyCredential?>.Ok(
                    new ProxyCredential(stored.UserName, stored.Password))
                : Result<ProxyCredential?>.Ok(null);
        }
    }

    public Result Save(ProxyEndpoint endpoint, ProxyCredential credential)
    {
        ArgumentNullException.ThrowIfNull(endpoint);
        ArgumentNullException.ThrowIfNull(credential);
        if (!IsSupported)
        {
            return Result.Fail(
                "network.credentials_unsupported",
                "当前平台不支持受保护的代理凭据存储。");
        }

        if (string.IsNullOrWhiteSpace(credential.UserName))
        {
            return Result.Fail("network.credentials_user_required", "代理用户名不能为空。");
        }

        lock (_gate)
        {
            var loaded = Load();
            if (!loaded.IsSuccess)
            {
                return Result.Fail(loaded.Error!);
            }

            var document = loaded.Value!;
            document.Endpoints[Key(endpoint)] = new StoredCredential
            {
                UserName = credential.UserName.Trim(),
                Password = credential.Password,
            };

            var saved = Save(document);
            if (saved.IsSuccess)
            {
                _logger.LogInformation("已保存代理凭据：{Endpoint}", endpoint);
            }

            return saved;
        }
    }

    public Result Delete(ProxyEndpoint endpoint)
    {
        ArgumentNullException.ThrowIfNull(endpoint);
        if (!IsSupported)
        {
            return Result.Fail(
                "network.credentials_unsupported",
                "当前平台不支持受保护的代理凭据存储。");
        }

        lock (_gate)
        {
            var loaded = Load();
            if (!loaded.IsSuccess)
            {
                return Result.Fail(loaded.Error!);
            }

            var document = loaded.Value!;
            if (!document.Endpoints.Remove(Key(endpoint)))
            {
                return Result.Ok();
            }

            var saved = Save(document);
            if (saved.IsSuccess)
            {
                _logger.LogInformation("已清除代理凭据：{Endpoint}", endpoint);
            }

            return saved;
        }
    }

    private static string Key(ProxyEndpoint endpoint) => endpoint.ToString();

    private Result<CredentialDocument> Load()
    {
        if (!File.Exists(_path))
        {
            return Result<CredentialDocument>.Ok(new CredentialDocument());
        }

        try
        {
            var encrypted = File.ReadAllBytes(_path);
            var payload = Unprotect(encrypted);
            var document = JsonSerializer.Deserialize<CredentialDocument>(payload) ?? new CredentialDocument();
            document.Endpoints ??= new Dictionary<string, StoredCredential>(StringComparer.Ordinal);
            return Result<CredentialDocument>.Ok(document);
        }
        catch (Exception exception) when (exception is
            CryptographicException or JsonException or IOException or UnauthorizedAccessException or Win32Exception)
        {
            _logger.LogWarning(exception, "读取代理凭据失败；不把损坏的存储当作空存储。");
            return Result<CredentialDocument>.Fail(
                "network.credentials_read_failed",
                "无法读取受保护的代理凭据。",
                exception.Message);
        }
    }

    private Result Save(CredentialDocument document)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            var payload = JsonSerializer.SerializeToUtf8Bytes(document);
            var encrypted = Protect(payload);
            var temporary = $"{_path}.{Guid.NewGuid():N}.tmp";
            try
            {
                File.WriteAllBytes(temporary, encrypted);
                File.Move(temporary, _path, overwrite: true);
            }
            finally
            {
                if (File.Exists(temporary))
                {
                    File.Delete(temporary);
                }
            }

            return Result.Ok();
        }
        catch (Exception exception) when (exception is
            CryptographicException or IOException or UnauthorizedAccessException or Win32Exception)
        {
            _logger.LogWarning(exception, "保存代理凭据失败。");
            return Result.Fail(
                "network.credentials_save_failed",
                "无法保存受保护的代理凭据。",
                exception.Message);
        }
    }

    private static byte[] Protect(byte[] payload) => CryptProtectData(payload, Entropy);

    private static byte[] Unprotect(byte[] payload) => CryptUnprotectData(payload, Entropy);

    private static byte[] CryptProtectData(byte[] payload, byte[] entropy)
    {
        var input = CreateBlob(payload);
        var optionalEntropy = CreateBlob(entropy);
        try
        {
            if (!CryptProtectData(ref input, "ECL proxy credentials", ref optionalEntropy, IntPtr.Zero, IntPtr.Zero, 0, out var output))
            {
                throw new Win32Exception(Marshal.GetLastWin32Error());
            }

            return CopyAndFree(output);
        }
        finally
        {
            FreeBlob(ref input);
            FreeBlob(ref optionalEntropy);
        }
    }

    private static byte[] CryptUnprotectData(byte[] payload, byte[] entropy)
    {
        var input = CreateBlob(payload);
        var optionalEntropy = CreateBlob(entropy);
        try
        {
            if (!CryptUnprotectData(ref input, IntPtr.Zero, ref optionalEntropy, IntPtr.Zero, IntPtr.Zero, 0, out var output))
            {
                throw new Win32Exception(Marshal.GetLastWin32Error());
            }

            return CopyAndFree(output);
        }
        finally
        {
            FreeBlob(ref input);
            FreeBlob(ref optionalEntropy);
        }
    }

    private static DATA_BLOB CreateBlob(byte[] value)
    {
        var pointer = Marshal.AllocHGlobal(value.Length);
        Marshal.Copy(value, 0, pointer, value.Length);
        return new DATA_BLOB { cbData = value.Length, pbData = pointer };
    }

    private static byte[] CopyAndFree(DATA_BLOB blob)
    {
        try
        {
            var value = new byte[blob.cbData];
            Marshal.Copy(blob.pbData, value, 0, value.Length);
            return value;
        }
        finally
        {
            LocalFree(blob.pbData);
        }
    }

    private static void FreeBlob(ref DATA_BLOB blob)
    {
        if (blob.pbData == IntPtr.Zero)
        {
            return;
        }

        var zero = new byte[blob.cbData];
        Marshal.Copy(zero, 0, blob.pbData, zero.Length);
        Marshal.FreeHGlobal(blob.pbData);
        blob.pbData = IntPtr.Zero;
        blob.cbData = 0;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct DATA_BLOB
    {
        public int cbData;
        public IntPtr pbData;
    }

    private sealed class CredentialDocument
    {
        public Dictionary<string, StoredCredential> Endpoints { get; set; } =
            new(StringComparer.Ordinal);
    }

    private sealed class StoredCredential
    {
        public string UserName { get; set; } = string.Empty;

        public string Password { get; set; } = string.Empty;
    }

    [DllImport("crypt32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CryptProtectData(
        ref DATA_BLOB pDataIn,
        string szDataDescr,
        ref DATA_BLOB pOptionalEntropy,
        IntPtr pvReserved,
        IntPtr pPromptStruct,
        int dwFlags,
        out DATA_BLOB pDataOut);

    [DllImport("crypt32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CryptUnprotectData(
        ref DATA_BLOB pDataIn,
        IntPtr ppszDataDescr,
        ref DATA_BLOB pOptionalEntropy,
        IntPtr pvReserved,
        IntPtr pPromptStruct,
        int dwFlags,
        out DATA_BLOB pDataOut);

    [DllImport("kernel32.dll")]
    private static extern IntPtr LocalFree(IntPtr hMem);
}
