// SPDX-License-Identifier: GPL-3.0-or-later

using Ecl.Core;
using Ecl.Minecraft.Engine.Identity;
using Ecl.Minecraft.Instances;

namespace Ecl.Minecraft.Engine.Lifecycle;

public enum InstanceOwnership
{
    EclManaged,
    External,
    SharedExternal,
    Unknown,
}

public sealed record InstanceOwnershipEvidence(
    string InstanceId,
    InstanceOwnership Ownership,
    string GameDirectory,
    IReadOnlyList<string> SharedWith,
    IReadOnlyList<string> Evidence,
    IReadOnlyList<string> Conflicts);

/// <summary>
/// 实例所有权分类（F-03 §4.1）。所有权是派生结果：外部/共享目录禁止 ECL 破坏性写入；
/// 证据不足或身份冲突时为 Unknown，破坏性操作必须阻断。
/// </summary>
public interface IInstanceOwnershipClassifier
{
    Task<Result<InstanceOwnershipEvidence>> ClassifyAsync(
        string instanceId,
        CancellationToken cancellationToken = default);
}

public sealed class InstanceOwnershipClassifier(
    IInstanceService instances,
    InstanceIdentityService identity,
    IAppPaths paths) : IInstanceOwnershipClassifier
{
    public async Task<Result<InstanceOwnershipEvidence>> ClassifyAsync(
        string instanceId,
        CancellationToken cancellationToken = default)
    {
        if (!AppPaths.IsValidInstanceId(instanceId))
        {
            return Result<InstanceOwnershipEvidence>.Fail("instance.invalid_id", "实例 ID 不合法。");
        }

        var instance = await instances.GetAsync(instanceId, cancellationToken).ConfigureAwait(false);
        if (instance is null)
        {
            return Result<InstanceOwnershipEvidence>.Fail("instance.not_found", "实例不存在。");
        }

        var gameDirectory = InstancePaths.GetGameDirectory(paths, instance);
        var evidence = new List<string>();
        var conflicts = new List<string>();

        var identityCheck = await identity.VerifyAsync(instanceId, cancellationToken).ConfigureAwait(false);
        if (identityCheck.IsSuccess && identityCheck.Value!.Status == InstanceRegistrationStatus.Conflict)
        {
            conflicts.Add(identityCheck.Value.Message);
        }

        var all = await instances.GetInstancesAsync(cancellationToken).ConfigureAwait(false);
        var sharedWith = all
            .Where(other => !string.Equals(other.Id, instanceId, StringComparison.Ordinal))
            .Where(other => PathsEqual(InstancePaths.GetGameDirectory(paths, other), gameDirectory))
            .Select(other => other.Id)
            .ToArray();

        InstanceOwnership ownership;
        if (conflicts.Count > 0)
        {
            ownership = InstanceOwnership.Unknown;
            evidence.Add("identity_conflict");
        }
        else if (sharedWith.Length > 0)
        {
            ownership = InstanceOwnership.SharedExternal;
            evidence.Add("multiple_instances_share_game_directory");
        }
        else if (instance.GameDirectory is null)
        {
            ownership = InstanceOwnership.EclManaged;
            evidence.Add("game_directory_is_ecl_managed");
        }
        else if (instance.GameDirectory is not null)
        {
            ownership = InstanceOwnership.External;
            evidence.Add("game_directory_is_absolute_external_reference");
        }
        else
        {
            ownership = InstanceOwnership.Unknown;
        }

        return Result<InstanceOwnershipEvidence>.Ok(new InstanceOwnershipEvidence(
            instanceId,
            ownership,
            gameDirectory,
            sharedWith,
            evidence,
            conflicts));
    }

    private static bool PathsEqual(string left, string right)
    {
        try
        {
            return string.Equals(
                Path.TrimEndingDirectorySeparator(Path.GetFullPath(left)),
                Path.TrimEndingDirectorySeparator(Path.GetFullPath(right)),
                OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);
        }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException)
        {
            return string.Equals(left, right, StringComparison.OrdinalIgnoreCase);
        }
    }
}
