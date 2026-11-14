// SPDX-License-Identifier: GPL-3.0-or-later

using System.IO;
using Ecl.Additions;
using Ecl.App.Services;
using Ecl.Core;
using Ecl.Deployment;

namespace Ecl.App.Tests.Support;

/// <summary>
/// 扩展管理页的领域替身：状态、目录与写入都只在内存里，测试可以精确断言“有没有真的调用领域”，
/// 不需要真实扩展包或网络。默认没有任何安装记录，也没有目录。
/// </summary>
internal sealed class FakeAdditionsService : IAdditionsService
{
    private readonly AdditionPlanner _planner = new();

    public List<InstalledAddition> Installed { get; } = [];

    public bool Corrupt { get; set; }

    public string? CorruptError { get; set; }

    public AdditionCatalogSnapshot? Catalog { get; set; }

    public EclError? CatalogError { get; set; }

    public EclError? SetEnabledError { get; set; }

    public EclError? UninstallError { get; set; }

    public EclError? RollbackError { get; set; }

    public EclError? ApplyError { get; set; }

    public int CatalogCalls { get; private set; }

    public int PlanCalls { get; private set; }

    public int ApplyCalls { get; private set; }

    public int UninstallCalls { get; private set; }

    public int RollbackCalls { get; private set; }

    public List<(AdditionId Id, bool Enabled)> EnabledRequests { get; } = [];

    public AdditionStateLoad LoadState()
    {
        var document = new InstalledAdditionsDocument { Additions = [.. Installed] };
        return Corrupt
            ? new AdditionStateLoad(document, IsCorrupt: true, CorruptError)
            : new AdditionStateLoad(document, IsCorrupt: false, null);
    }

    public Task<Result<AdditionCatalogSnapshot>> GetCatalogAsync(
        bool refresh = false,
        CancellationToken cancellationToken = default)
    {
        CatalogCalls++;
        return Task.FromResult(
            CatalogError is { } error
                ? Result<AdditionCatalogSnapshot>.Fail(error)
                : Catalog is null
                    ? Result<AdditionCatalogSnapshot>.Fail(
                        "additions.catalog_not_loaded",
                        "尚未读取官方扩展目录。")
                    : Result<AdditionCatalogSnapshot>.Ok(Catalog));
    }

    public Task<Result<AdditionInstallPlan>> CreatePlanAsync(
        IReadOnlyList<AdditionId> targets,
        CancellationToken cancellationToken = default)
    {
        PlanCalls++;
        if (Catalog is null)
        {
            return Task.FromResult(Result<AdditionInstallPlan>.Fail(
                "additions.catalog_not_loaded",
                "尚未读取官方扩展目录。"));
        }

        return Task.FromResult(_planner.Plan(
            Catalog.Catalog,
            Installed,
            targets,
            AdditionCoreApi.Version,
            "windows"));
    }

    public Task<Result> ApplyPlanAsync(
        AdditionInstallPlan plan,
        CancellationToken cancellationToken = default)
    {
        ApplyCalls++;
        if (ApplyError is { } error)
        {
            return Task.FromResult(Result.Fail(error));
        }

        foreach (var item in plan.Install.Concat(plan.Update))
        {
            Installed.RemoveAll(record => record.Id == item.Entry.Id);
            Installed.Add(InstalledRecord(item.Entry));
        }

        return Task.FromResult(Result.Ok());
    }

    public Task<Result> SetEnabledAsync(
        AdditionId id,
        bool enabled,
        CancellationToken cancellationToken = default)
    {
        EnabledRequests.Add((id, enabled));
        if (SetEnabledError is { } error)
        {
            return Task.FromResult(Result.Fail(error));
        }

        var index = Installed.FindIndex(record => record.Id == id);
        if (index >= 0)
        {
            Installed[index] = Installed[index] with { Enabled = enabled };
        }

        return Task.FromResult(Result.Ok());
    }

    public Task<Result> UninstallAsync(AdditionId id, CancellationToken cancellationToken = default)
    {
        UninstallCalls++;
        if (UninstallError is { } error)
        {
            return Task.FromResult(Result.Fail(error));
        }

        Installed.RemoveAll(record => record.Id == id);
        return Task.FromResult(Result.Ok());
    }

    public Task<Result<InstalledAddition>> RollbackAsync(
        AdditionId id,
        CancellationToken cancellationToken = default)
    {
        RollbackCalls++;
        if (RollbackError is { } error)
        {
            return Task.FromResult(Result<InstalledAddition>.Fail(error));
        }

        var index = Installed.FindIndex(record => record.Id == id);
        if (index < 0 || Installed[index].PreviousVersion is not { } previous)
        {
            return Task.FromResult(Result<InstalledAddition>.Fail(
                "additions.no_previous_version",
                "该扩展没有可回滚的上一版本。"));
        }

        var rolled = Installed[index] with { Version = previous, PreviousVersion = null };
        Installed[index] = rolled;
        return Task.FromResult(Result<InstalledAddition>.Ok(rolled));
    }

    private static InstalledAddition InstalledRecord(AdditionEntry entry) =>
        new(
            entry.Id,
            entry.Type,
            entry.Version,
            Path.Combine(Path.GetTempPath(), "ecl-additions-tests", entry.Id.Value, entry.Version.ToString()),
            "Ecl.TestAddition.dll",
            Enabled: true,
            DataSchemaVersion: null,
            Files: [],
            DateTimeOffset.UtcNow)
        {
            Dependencies = entry.Dependencies.Where(dependency => !dependency.Optional)
                .Select(dependency => dependency.Id)
                .ToArray(),
            CoreApiMin = entry.CoreApi.Min,
            CoreApiMax = entry.CoreApi.Max,
        };
}
