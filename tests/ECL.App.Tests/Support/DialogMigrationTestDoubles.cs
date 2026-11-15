// SPDX-License-Identifier: GPL-3.0-or-later

using System.Collections.ObjectModel;
using Ecl.Additions;
using Ecl.App.Dialogs;
using Ecl.App.Services;
using Ecl.Auth;
using Ecl.Core;
using Ecl.Core.Storage;
using Ecl.Minecraft.Instances;
using Ecl.Operations;

namespace Ecl.App.Tests.Support;

/// <summary>B10/B11 测试共用的弹窗会话：行为与真实会话一致，忙碌时拒绝普通关闭。</summary>
internal sealed class FakeDialogSession : IDialogSession
{
    public bool IsOpen { get; private set; } = true;

    public bool IsBusy { get; private set; }

    public DialogResult? Result { get; private set; }

    public event EventHandler? Closed;

    public void SetBusy(bool isBusy) => IsBusy = isBusy;

    public bool TryClose(DialogOutcome outcome, object? value = null)
    {
        if (!IsOpen || (IsBusy && outcome is not DialogOutcome.Failed))
        {
            return false;
        }

        IsOpen = false;
        Result = new DialogResult(outcome, value);
        Closed?.Invoke(this, EventArgs.Empty);
        return true;
    }
}

/// <summary>可编程的弹窗引擎：记录请求并返回预设结果。</summary>
internal sealed class FakeDialogEngine : IDialogEngine
{
    public bool IsAvailable { get; set; } = true;

    public Queue<DialogResult> Results { get; } = new();

    public List<DialogRequest> Requests { get; } = [];

    public DialogResult Show(DialogRequest request) => ShowAsync(request).GetAwaiter().GetResult();

    public Task<DialogResult> ShowAsync(DialogRequest request, CancellationToken cancellationToken = default)
    {
        if (!IsAvailable)
        {
            throw new DialogHostUnavailableException();
        }

        Requests.Add(request);
        return Task.FromResult(Results.Count > 0 ? Results.Dequeue() : DialogResult.Closed);
    }

    public bool CloseTop(DialogOutcome outcome) => true;
}

/// <summary>可编程的工作区服务：只实现向导用到的读取、校验与提交路径。</summary>
internal sealed class FakeWorkspaceService : IWorkspaceService
{
    public List<WorkspaceSuggestion> Suggested { get; } = [];

    public WorkspaceLegacyInfo Legacy { get; set; } = new(false, string.Empty, 0, 0);

    public WorkspacePathCheck CheckResult { get; set; } =
        new("/tmp/ecl", true, WorkspaceStatus.Ready, null, null, 10L * 1024 * 1024 * 1024,
            ["workspace.warning.systemDrive"]);

    public Result<WorkspaceRoot> InitializeResult { get; set; } =
        Result<WorkspaceRoot>.Ok(new WorkspaceRoot("root", "Root", "/tmp/ecl", WorkspaceKind.Managed));

    public Result<WorkspaceRoot> AdoptResult { get; set; } =
        Result<WorkspaceRoot>.Ok(new WorkspaceRoot("legacy", "Legacy", "/tmp/legacy", WorkspaceKind.Managed));

    public int InitializeCalls { get; private set; }

    public int AdoptCalls { get; private set; }

    public int SkipCalls { get; private set; }

    public int CheckCalls { get; private set; }

    public WorkspaceFile State { get; set; } = new();

    public WorkspaceRoot? Primary { get; set; }

    public IReadOnlyList<WorkspaceRoot> Roots => Primary is null ? [] : [Primary];

    public bool IsConfigured => Primary is not null;

    public event EventHandler? Changed
    {
        add { }
        remove { }
    }

    public IReadOnlyList<WorkspaceSuggestion> Suggest() => Suggested;

    public WorkspacePathCheck Check(string path, bool createIfMissing)
    {
        CheckCalls++;
        return CheckResult with { Path = path };
    }

    public Task<Result<WorkspaceRoot>> InitializeAsync(string path, CancellationToken cancellationToken = default)
    {
        InitializeCalls++;
        return Task.FromResult(InitializeResult);
    }

    public Task<Result<WorkspaceRoot>> RegisterExternalAsync(string path, CancellationToken cancellationToken = default) =>
        throw new NotSupportedException();

    public Task<Result<WorkspacePathCheck>> ValidatePrimaryAsync(CancellationToken cancellationToken = default) =>
        throw new NotSupportedException();

    public Task SkipOnboardingAsync(CancellationToken cancellationToken = default)
    {
        SkipCalls++;
        return Task.CompletedTask;
    }

    public Task UpdateRootAsync(WorkspaceRoot root, CancellationToken cancellationToken = default) =>
        throw new NotSupportedException();

    public Task<Result<bool>> RemoveRootAsync(string id, CancellationToken cancellationToken = default) =>
        throw new NotSupportedException();

    public WorkspaceLegacyInfo DetectLegacy() => Legacy;

    public Task<Result<WorkspaceRoot>> AdoptLegacyAsync(CancellationToken cancellationToken = default)
    {
        AdoptCalls++;
        return Task.FromResult(AdoptResult);
    }

    public StorageLayout GetStorageLayout() => throw new NotSupportedException();
}

/// <summary>版本导入测试用实例服务：只实现扫描与导入。</summary>
internal sealed class FakeInstanceService : IInstanceService
{
    public List<GameInstance> Instances { get; } = [];

    public Func<string, CancellationToken, Task<Result<GameInstance>>> Import { get; set; } =
        (id, _) => Task.FromResult(Result<GameInstance>.Ok(new GameInstance
        {
            Id = $"imported-{id}",
            Name = id,
            GameVersion = "1.20.1",
            VersionId = id,
        }));

    public List<string> ImportedIds { get; } = [];

    public Task<IReadOnlyList<GameInstance>> GetInstancesAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyList<GameInstance>>(Instances.ToArray());

    public Task<GameInstance?> GetAsync(string instanceId, CancellationToken cancellationToken = default) =>
        Task.FromResult<GameInstance?>(null);

    public Task<Result<GameInstance>> CreateAsync(
        string name,
        string gameVersion,
        string loader = "vanilla",
        string? loaderVersion = null,
        string? fabricApiVersion = null,
        string? optiFineVersion = null,
        CancellationToken cancellationToken = default) => throw new NotSupportedException();

    public Task<Result<GameInstance>> UpdateConfigurationAsync(
        string instanceId,
        string gameVersion,
        string loader,
        string? loaderVersion = null,
        string? fabricApiVersion = null,
        string? optiFineVersion = null,
        CancellationToken cancellationToken = default) => throw new NotSupportedException();

    public Task<Result<GameInstance>> RestoreConfigurationAsync(
        GameInstance restorePoint,
        CancellationToken cancellationToken = default) => throw new NotSupportedException();

    public Task<Result> DeleteAsync(string instanceId, CancellationToken cancellationToken = default) =>
        throw new NotSupportedException();

    public Task<Result<InstanceDeletionPlan>> GetDeletionPlanAsync(
        string instanceId,
        CancellationToken cancellationToken = default) => throw new NotSupportedException();

    public Task<Result> DeleteAsync(
        string instanceId,
        InstanceDeleteOptions options,
        CancellationToken cancellationToken = default) => throw new NotSupportedException();

    public Task<IReadOnlyList<DetectedVersion>> ScanVersionsAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyList<DetectedVersion>>([]);

    public Task<Result<GameInstance>> ImportAsync(
        string versionId,
        string? name = null,
        CancellationToken cancellationToken = default)
    {
        ImportedIds.Add(versionId);
        return Import(versionId, cancellationToken);
    }

    public Task TouchLastPlayedAsync(string instanceId, CancellationToken cancellationToken = default) =>
        Task.CompletedTask;

    public Task<Result<GameInstance>> RelocateAsync(
        string instanceId,
        string newGameDirectory,
        CancellationToken cancellationToken = default) => throw new NotSupportedException();

    public Task<Result<GameInstance>> CloneAsync(
        string sourceInstanceId,
        string name,
        CancellationToken cancellationToken = default) => throw new NotSupportedException();
}

/// <summary>认证服务器测试替身：按顺序记录调用并返回预设结果。</summary>
internal sealed class FakeAuthServerService : IAuthServerService
{
    public List<string> Calls { get; } = [];

    public Result<AuthServer> AddResult { get; set; } = Result<AuthServer>.Ok(
        new AuthServer { Id = "server-1", Url = "https://example.com/api/yggdrasil", Name = "Example" });

    public Result<AuthServerMetadata> ValidateResult { get; set; } = Result<AuthServerMetadata>.Ok(
        new AuthServerMetadata { Url = "https://example.com/api/yggdrasil", ServerName = "Renamed" });

    public Result<AuthServer> UpdateResult { get; set; } = Result<AuthServer>.Ok(
        new AuthServer { Id = "server-1", Url = "https://example.com/api/yggdrasil", Name = "Example" });

    public Task<IReadOnlyList<AuthServer>> GetServersAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyList<AuthServer>>([]);

    public Task<AuthServer?> GetAsync(string id, CancellationToken cancellationToken = default) =>
        Task.FromResult<AuthServer?>(null);

    public Task<Result<AuthServer>> ResolveAsync(string idOrUrl, CancellationToken cancellationToken = default) =>
        throw new NotSupportedException();

    public Task<Result<AuthServer>> AddAsync(
        string url,
        string? registerUrl = null,
        CancellationToken cancellationToken = default)
    {
        Calls.Add("add");
        return Task.FromResult(AddResult);
    }

    public Task<Result<AuthServerMetadata>> ValidateAsync(
        string url,
        CancellationToken cancellationToken = default)
    {
        Calls.Add("validate");
        return Task.FromResult(ValidateResult);
    }

    public Task<Result<AuthServer>> UpdateAsync(
        string id,
        string? url,
        string? registerUrl,
        CancellationToken cancellationToken = default)
    {
        Calls.Add("update");
        return Task.FromResult(UpdateResult);
    }

    public Task<Result> RemoveAsync(string id, CancellationToken cancellationToken = default) =>
        throw new NotSupportedException();
}

/// <summary>账户测试替身：只实现 Authlib 流程使用的方法。</summary>
internal sealed class FakeAccountService : IAccountService
{
    public List<string> Calls { get; } = [];

    public Result<Account> SignInResult { get; set; } =
        Result<Account>.Ok(Account("signed-in"));

    public Result<Account> ReauthenticateResult { get; set; } =
        Result<Account>.Ok(Account("reauthenticated"));

    public Result<AuthlibSession> PrepareResult { get; set; } = Result<AuthlibSession>.Ok(new AuthlibSession
    {
        AccessToken = "access",
        ClientToken = "client",
        PlayerName = "Player",
        Uuid = "00000000-0000-0000-0000-000000000000",
    });

    public Result<Account> ApplyResult { get; set; } =
        Result<Account>.Ok(Account("applied"));

    public Task<IReadOnlyList<Account>> GetAccountsAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyList<Account>>([]);

    public Task<Account?> GetDefaultAccountAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult<Account?>(null);

    public Task<Result<Account>> AddOfflineAsync(
        string name,
        bool makeDefault = false,
        CancellationToken cancellationToken = default) => throw new NotSupportedException();

    public Task<Result<Account>> AddAsync(
        Account account,
        bool makeDefault = false,
        CancellationToken cancellationToken = default) => throw new NotSupportedException();

    public Task<Result<Account>> AddMicrosoftAsync(
        Account account,
        AccountCredentials credentials,
        bool makeDefault = false,
        CancellationToken cancellationToken = default) => throw new NotSupportedException();

    public Task<Result<Account>> AddThirdPartyAsync(
        Account account,
        AccountCredentials credentials,
        bool makeDefault = false,
        CancellationToken cancellationToken = default) => throw new NotSupportedException();

    public Task<Result> RemoveAsync(string accountId, CancellationToken cancellationToken = default) =>
        throw new NotSupportedException();

    public Task<Result> SetDefaultAsync(string accountId, CancellationToken cancellationToken = default) =>
        throw new NotSupportedException();

    public Task<Result<Account>> RefreshAsync(
        string accountId,
        MicrosoftAuthenticationOptions options,
        CancellationToken cancellationToken = default) => throw new NotSupportedException();

    public Task<Result<Account>> EnsureFreshAsync(
        Account account,
        MicrosoftAuthenticationOptions options,
        TimeSpan margin,
        CancellationToken cancellationToken = default) => throw new NotSupportedException();

    public Task<Result<Account>> EnsureThirdPartyFreshAsync(
        Account account,
        CancellationToken cancellationToken = default) => throw new NotSupportedException();

    public Task<Result<Account>> SignInThirdPartyAsync(
        string authServerId,
        string username,
        string password,
        bool makeDefault = false,
        CancellationToken cancellationToken = default)
    {
        Calls.Add($"sign-in:{authServerId}:{username}:{password}");
        return Task.FromResult(SignInResult);
    }

    public Task<Result<Account>> ReauthenticateThirdPartyAsync(
        string accountId,
        string password,
        CancellationToken cancellationToken = default)
    {
        Calls.Add($"reauth:{accountId}:{password}");
        return Task.FromResult(ReauthenticateResult);
    }

    public Task<Result<AuthlibSession>> PrepareThirdPartyReauthenticationAsync(
        string accountId,
        string password,
        AuthServer targetServer,
        CancellationToken cancellationToken = default)
    {
        Calls.Add($"prepare:{accountId}:{targetServer.Url}");
        return Task.FromResult(PrepareResult);
    }

    public Task<Result<Account>> ApplyThirdPartyReauthenticationAsync(
        string accountId,
        AuthlibSession session,
        AuthServer targetServer,
        CancellationToken cancellationToken = default)
    {
        Calls.Add($"apply:{accountId}:{targetServer.Url}");
        return Task.FromResult(ApplyResult);
    }

    public static Account Account(string name) => new()
    {
        Id = $"account-{name}",
        Type = AccountType.ThirdParty,
        Name = name,
        Uuid = "00000000-0000-0000-0000-000000000001",
        AuthServerId = "server-1",
    };
}

/// <summary>记录调用的外部链接服务。</summary>
internal sealed class FakeLinkService : IExternalLinkService
{
    public List<string> Opened { get; } = [];

    public bool Result { get; set; } = true;

    public bool Open(string target)
    {
        Opened.Add(target);
        return Result;
    }
}

/// <summary>记录 Begin/Handle 行为的任务中心替身（与真实 Operation 生命周期语义一致）。</summary>
internal sealed class RecordingOperationService : IOperationService
{
    public List<OperationStart> Starts { get; } = [];

    public List<Guid> Controls { get; } = [];

    public RecordingHandle? Handle { get; private set; }

    public IOperationStore Store { get; } = new StoreStub();

    public IOperationHandle Begin(OperationStart start)
    {
        Starts.Add(start);
        Handle = new RecordingHandle(start);
        return Handle;
    }

    public bool AttachControl(Guid operationId, IOperationControl control)
    {
        Controls.Add(operationId);
        return true;
    }

    public bool TryCancel(Guid operationId) => Handle?.Control?.TryCancel() ?? false;

    public Task<bool> RetryAsync(Guid operationId, CancellationToken cancellationToken = default) =>
        Task.FromResult(false);

    public bool Remove(Guid operationId) => true;

    public int PruneCompleted(DateTimeOffset completedBefore) => 0;

    public sealed class RecordingHandle(OperationStart start) : IOperationHandle
    {
        public Guid Id { get; } = Guid.NewGuid();

        public OperationSnapshot Snapshot { get; private set; } = new()
        {
            Id = Guid.Empty,
            Kind = start.Kind,
            Title = start.Title,
            State = OperationState.Running,
            StartedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow,
        };

        public List<OperationProgressUpdate> Reports { get; } = [];

        public bool Completed { get; private set; }

        public bool Failed { get; private set; }

        public bool Cancelled { get; private set; }

        public OperationError? Error { get; private set; }

        public IOperationControl? Control { get; set; }

        public void Report(OperationProgressUpdate update) => Reports.Add(update);

        public void SetStage(string stageId, string? stageText = null)
        {
        }

        public void Retrying(int attempt, int maxAttempts, string? reason = null)
        {
        }

        public void Complete(OperationProgressUpdate? finalUpdate = null)
        {
            Completed = true;
            if (finalUpdate is not null)
            {
                Reports.Add(finalUpdate);
            }
        }

        public void Fail(OperationError error)
        {
            Failed = true;
            Error = error;
        }

        public void Cancel() => Cancelled = true;

        public void Pause()
        {
        }

        public void Resume()
        {
        }

        public void Dispose()
        {
        }
    }

    private sealed class StoreStub : IOperationStore
    {
        public IReadOnlyList<OperationSnapshot> Snapshots => [];

        public event EventHandler<OperationSnapshot>? Changed
        {
            add { }
            remove { }
        }

        public OperationSnapshot? TryGet(Guid operationId) => null;
    }
}
