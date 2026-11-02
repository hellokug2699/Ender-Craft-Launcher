// SPDX-License-Identifier: GPL-3.0-or-later

using Ecl.Auth;

namespace Ecl.Wardrobe.Appearance;

/// <summary>
/// 账户外观能力。标志位允许不同账户类型只声明真实支持的操作；
/// 未声明的能力绝不能由 UI 推断为可用。
/// </summary>
[Flags]
public enum AppearanceCapability
{
    None = 0,

    ReadCurrentAppearance = 1 << 0,
    ListAvailableTextures = 1 << 1,
    UploadSkin = 1 << 2,
    UploadCape = 1 << 3,
    SelectExistingCape = 1 << 4,
    DeleteSkin = 1 << 5,
    DeleteCape = 1 << 6,

    // 以下不是账户服务端能力，而是离线账户也能使用的本地能力。
    UseLocalWardrobe = 1 << 7,
    ApplyLocalToInstance = 1 << 8,
}

/// <summary>能力声明状态。声明（Declared）只代表服务端/Provider 声称支持，不代表请求一定成功。</summary>
public enum AppearanceCapabilityStatus
{
    /// <summary>服务端或 Provider 明确声明支持。</summary>
    Declared,

    /// <summary>服务端明确不声明该能力，或当前账户/角色不具备该能力。</summary>
    Unavailable,

    /// <summary>无法读取能力声明（资料缺失、格式异常或尚未检查）。不得据此宣称可用。</summary>
    Unknown,
}

/// <summary>能力报告。Reason 用于解释为什么某些能力不可用。</summary>
public sealed record AppearanceCapabilityReport
{
    public required AppearanceCapability Capabilities { get; init; }

    public string? Reason { get; init; }

    public bool Supports(AppearanceCapability capability) =>
        capability != AppearanceCapability.None && (Capabilities & capability) == capability;
}

/// <summary>账户外观操作。本地衣柜/实例配置操作由独立模块负责，不混入账户 Provider。</summary>
public enum AppearanceOperation
{
    ReadCurrentAppearance,
    ListAvailableTextures,
    UploadSkin,
    UploadCape,
    SelectExistingCape,
    DeleteSkin,
    DeleteCape,
}

/// <summary>
/// 外观操作结果状态。必须区分“不支持”“未授权”“网络失败”“输入无效”和“成功”，
/// 不得把不支持伪装成网络错误，也不得在失败后返回成功。
/// </summary>
public enum AppearanceOperationStatus
{
    Succeeded,
    Unsupported,
    Unauthorized,
    NetworkFailed,
    InvalidInput,

    /// <summary>服务端返回重定向；为避免凭据泄露拒绝跟随。</summary>
    RedirectRejected,

    /// <summary>服务端已收到请求但返回了错误状态（非网络故障）。</summary>
    ServerRejected,
}

/// <summary>外观操作结果。Message 面向用户，Detail 只放可诊断且不含令牌的信息。</summary>
public sealed record AppearanceOperationResult
{
    public required AppearanceOperationStatus Status { get; init; }

    public string Message { get; init; } = string.Empty;

    public string? Detail { get; init; }

    public bool IsSuccess => Status == AppearanceOperationStatus.Succeeded;

    public static AppearanceOperationResult Succeeded(string message) =>
        new() { Status = AppearanceOperationStatus.Succeeded, Message = message };

    public static AppearanceOperationResult Unsupported(string message, string? detail = null) =>
        new() { Status = AppearanceOperationStatus.Unsupported, Message = message, Detail = detail };

    public static AppearanceOperationResult Unauthorized(string message, string? detail = null) =>
        new() { Status = AppearanceOperationStatus.Unauthorized, Message = message, Detail = detail };

    public static AppearanceOperationResult NetworkFailed(string message, string? detail = null) =>
        new() { Status = AppearanceOperationStatus.NetworkFailed, Message = message, Detail = detail };

    public static AppearanceOperationResult InvalidInput(string message, string? detail = null) =>
        new() { Status = AppearanceOperationStatus.InvalidInput, Message = message, Detail = detail };
}

/// <summary>一次外观操作请求。</summary>
public sealed record AppearanceRequest
{
    public required AppearanceOperation Operation { get; init; }

    public required Account Account { get; init; }

    /// <summary>衣柜资源 ID；上传/选择操作使用。</summary>
    public string? ItemId { get; init; }

    /// <summary>可选的已解码 PNG 内容；Provider 仍必须自行校验后再发送。</summary>
    public byte[]? TextureBytes { get; init; }

    public WardrobeTextureType? TextureType { get; init; }

    /// <summary>用户确认的皮肤模型；Unknown 表示不发送 model 参数。</summary>
    public SkinModel SkinModel { get; init; } = SkinModel.Unknown;
}

/// <summary>
/// 账户外观 Provider。实现必须只声明自己真实支持的能力，并复用账户模块的凭据生命周期。
/// </summary>
public interface IAppearanceProvider
{
    string ProviderId { get; }

    Task<AppearanceCapabilityReport> GetCapabilitiesAsync(
        Account account,
        CancellationToken cancellationToken = default);

    Task<AppearanceOperationResult> ExecuteAsync(
        AppearanceRequest request,
        CancellationToken cancellationToken = default);
}
