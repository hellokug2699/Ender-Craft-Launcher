// SPDX-License-Identifier: GPL-3.0-or-later

using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Ecl.Core;
using Microsoft.Extensions.Logging;

namespace Ecl.Auth;

/// <summary>
/// Stores account secrets encrypted with Windows DPAPI under the current Windows user.
/// Microsoft credentials must never be persisted in the normal accounts.json document.
/// </summary>
internal sealed class WindowsDpapiCredentialStore(IAppPaths paths, ILogger<WindowsDpapiCredentialStore> logger) : ISecureCredentialStore
{
    private const string FileName = "credentials.dat";
    private static readonly byte[] Entropy = SHA256.HashData(Encoding.UTF8.GetBytes("EnderCraftLauncher.AccountCredentials.v1"));

    public async Task<Result> SaveAsync(string accountId, AccountCredentials credentials, CancellationToken cancellationToken = default)
    {
        if (!OperatingSystem.IsWindows())
        {
            return Result.Fail("auth.secure_storage.unsupported", "当前平台暂不支持 Microsoft 账户安全存储。请在 Windows 上完成登录。");
        }

        if (!IsValidAccountId(accountId))
        {
            return Result.Fail("auth.secure_storage.invalid_id", "账户 ID 无效。");
        }

        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            Directory.CreateDirectory(paths.AccountsDirectory);
            var payload = JsonSerializer.SerializeToUtf8Bytes(credentials);
            var encrypted = Protect(payload);
            var path = GetPath(accountId);
            var temporaryPath = $"{path}.{Guid.NewGuid():N}.tmp";
            await File.WriteAllBytesAsync(temporaryPath, encrypted, cancellationToken).ConfigureAwait(false);
            File.Move(temporaryPath, path, overwrite: true);
            return Result.Ok();
        }
        catch (Exception exception) when (exception is CryptographicException or IOException or UnauthorizedAccessException or Win32Exception)
        {
            logger.LogWarning(exception, "保存账户凭据失败：{AccountId}", accountId);
            return Result.Fail("auth.secure_storage.save_failed", "无法安全保存 Microsoft 账户凭据。", exception.Message);
        }
    }

    public async Task<Result<AccountCredentials>> GetAsync(string accountId, CancellationToken cancellationToken = default)
    {
        if (!OperatingSystem.IsWindows())
        {
            return Result<AccountCredentials>.Fail("auth.secure_storage.unsupported", "当前平台暂不支持 Microsoft 账户安全存储。");
        }

        if (!IsValidAccountId(accountId))
        {
            return Result<AccountCredentials>.Fail("auth.secure_storage.invalid_id", "账户 ID 无效。");
        }

        try
        {
            var path = GetPath(accountId);
            if (!File.Exists(path))
            {
                return Result<AccountCredentials>.Fail("auth.credentials_missing", "Microsoft 账户凭据不存在，需要重新登录。");
            }

            var encrypted = await File.ReadAllBytesAsync(path, cancellationToken).ConfigureAwait(false);
            var payload = Unprotect(encrypted);
            var credentials = JsonSerializer.Deserialize<AccountCredentials>(payload);
            return credentials is null
                ? Result<AccountCredentials>.Fail("auth.secure_storage.invalid_data", "账户凭据数据无效，需要重新登录。")
                : Result<AccountCredentials>.Ok(credentials);
        }
        catch (Exception exception) when (exception is CryptographicException or JsonException or IOException or UnauthorizedAccessException or Win32Exception)
        {
            logger.LogWarning(exception, "读取账户凭据失败：{AccountId}", accountId);
            return Result<AccountCredentials>.Fail("auth.secure_storage.read_failed", "无法读取 Microsoft 账户安全凭据，需要重新登录。", exception.Message);
        }
    }

    public Task<Result> RemoveAsync(string accountId, CancellationToken cancellationToken = default)
    {
        if (!OperatingSystem.IsWindows())
        {
            return Task.FromResult(Result.Fail("auth.secure_storage.unsupported", "当前平台暂不支持 Microsoft 账户安全存储。"));
        }

        if (!IsValidAccountId(accountId))
        {
            return Task.FromResult(Result.Fail("auth.secure_storage.invalid_id", "账户 ID 无效。"));
        }

        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            File.Delete(GetPath(accountId));
            return Task.FromResult(Result.Ok());
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            logger.LogWarning(exception, "删除账户凭据失败：{AccountId}", accountId);
            return Task.FromResult(Result.Fail("auth.secure_storage.remove_failed", "无法删除 Microsoft 账户安全凭据。", exception.Message));
        }
    }

    private string GetPath(string accountId) => Path.Combine(paths.AccountsDirectory, $"{FileName}.{accountId}");

    private static bool IsValidAccountId(string accountId) =>
        !string.IsNullOrWhiteSpace(accountId) && accountId.Length <= 128 && accountId.All(char.IsLetterOrDigit);

    private static byte[] Protect(byte[] payload)
    {
        return CryptProtectData(payload, Entropy);
    }

    private static byte[] Unprotect(byte[] payload)
    {
        return CryptUnprotectData(payload, Entropy);
    }

    private static byte[] CryptProtectData(byte[] payload, byte[] entropy)
    {
        var input = CreateBlob(payload);
        var optionalEntropy = CreateBlob(entropy);
        try
        {
            if (!CryptProtectData(ref input, "ECL account credentials", ref optionalEntropy, IntPtr.Zero, IntPtr.Zero, 0, out var output))
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
        if (blob.pbData != IntPtr.Zero)
        {
            var zero = new byte[blob.cbData];
            Marshal.Copy(zero, 0, blob.pbData, zero.Length);
            Marshal.FreeHGlobal(blob.pbData);
            blob.pbData = IntPtr.Zero;
            blob.cbData = 0;
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct DATA_BLOB
    {
        public int cbData;
        public IntPtr pbData;
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
