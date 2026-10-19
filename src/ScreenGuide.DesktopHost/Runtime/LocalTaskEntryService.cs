using System.Text.Json;
using ScreenGuide.Core.Tasking;

namespace ScreenGuide.DesktopHost.Runtime;

public sealed record CreateLocalTaskRequest(
    Guid ProjectId,
    string Instruction,
    string? Title = null,
    string WorkingDirectoryRelativePath = ".",
    string? IdempotencyKey = null);

public sealed record LocalTaskCommandResult(Guid TaskId, bool WasDuplicate, bool Accepted = true);

public sealed record LocalTaskDetails(
    AgentTask Task,
    ProjectRecord Project,
    AgentRunRecord? AgentRun,
    IReadOnlyList<AgentAttemptRecord> Attempts,
    DecisionRequestRecord? PendingDecision,
    IReadOnlyList<TaskEventRecord> Events,
    TaskEvidence? Evidence,
    IReadOnlyList<ResourceScopeRecord> ResourceScopes,
    IReadOnlyList<SkillInvocationRecord> SkillInvocations);

/// <summary>
/// Desktop Host 为本机 UI 暴露的唯一任务入口。Codex 协议和 SQLite 细节不越过此边界。
/// </summary>
public sealed class LocalTaskEntryService(
    ILocalTaskStore store,
    AgentTaskExecutionService executionService,
    DesktopHostState hostState,
    ProjectInspector projectInspector,
    TimeProvider timeProvider) : ILocalTaskStateReader
{
    public Task<IReadOnlyList<ProjectRecord>> GetAuthorizedProjectsAsync(
        CancellationToken cancellationToken = default) =>
        store.GetAuthorizedProjectsAsync(cancellationToken);

    public Task<int> GetSchemaVersionAsync(CancellationToken cancellationToken = default) =>
        store.GetSchemaVersionAsync(cancellationToken);

    public Task<IReadOnlyList<ProjectRecord>> GetProjectsAsync(
        bool includeRevoked = true,
        CancellationToken cancellationToken = default) =>
        store.GetProjectsAsync(includeRevoked, cancellationToken);

    public async Task<ProjectRecord> AddProjectAsync(
        string rootPath,
        string? name = null,
        CancellationToken cancellationToken = default)
    {
        var root = ProjectPathPolicy.NormalizeExistingRoot(rootPath);
        var inspection = await projectInspector.InspectAsync(root, cancellationToken)
            .ConfigureAwait(false);
        if (!inspection.IsGitRepository)
        {
            throw new InvalidOperationException("选择的目录不是 Git 项目。");
        }

        var state = RequireStartedHost();
        var now = timeProvider.GetUtcNow();
        var existing = (await store.GetProjectsAsync(true, cancellationToken).ConfigureAwait(false))
            .FirstOrDefault(project => string.Equals(
                Path.GetFullPath(project.RootPath),
                root,
                StringComparison.OrdinalIgnoreCase));
        var project = existing is null
            ? new ProjectRecord
            {
                Id = Guid.NewGuid(),
                Name = NormalizeProjectName(name, root),
                RootPath = root,
                AuthorizationState = ProjectAuthorizationState.Authorized,
                AuthorizedByDeviceId = state.LocalDevice!.Id,
                AuthorizedAtUtc = now,
                CreatedAtUtc = now,
                UpdatedAtUtc = now
            }
            : existing with
            {
                Name = NormalizeProjectName(name, root),
                AuthorizationState = ProjectAuthorizationState.Authorized,
                AuthorizedByDeviceId = state.LocalDevice!.Id,
                AuthorizedAtUtc = now,
                RevokedAtUtc = null,
                UpdatedAtUtc = now
            };
        await store.SetProjectAuthorizationAsync(project, cancellationToken).ConfigureAwait(false);
        return project;
    }

    public async Task<ProjectRecord> RevokeProjectAsync(
        Guid projectId,
        CancellationToken cancellationToken = default)
    {
        var existing = await store.GetProjectAsync(projectId, cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidOperationException("项目不存在。");
        if (existing.AuthorizationState == ProjectAuthorizationState.Revoked)
        {
            return existing;
        }

        if ((await store.GetTasksAsync(projectId, cancellationToken).ConfigureAwait(false))
            .Any(DesktopApiMapper.IsActive))
        {
            throw new InvalidOperationException("项目仍有任务正在运行，暂时不能删除授权。");
        }

        var now = timeProvider.GetUtcNow();
        var revoked = existing with
        {
            AuthorizationState = ProjectAuthorizationState.Revoked,
            RevokedAtUtc = now,
            UpdatedAtUtc = now
        };
        await store.SetProjectAuthorizationAsync(revoked, cancellationToken).ConfigureAwait(false);
        return revoked;
    }

    public Task<int> ClearTerminalHistoryAsync(CancellationToken cancellationToken = default) =>
        store.DeleteTerminalTaskHistoryAsync(
            timeProvider.GetUtcNow(),
            RequireStartedHost().LocalDevice!.Id,
            cancellationToken);

    public Task<IReadOnlyList<AgentTask>> GetTasksAsync(
        Guid? projectId = null,
        CancellationToken cancellationToken = default) =>
        store.GetTasksAsync(projectId, cancellationToken);

    public async Task<LocalTaskDetails?> GetTaskDetailsAsync(
        Guid taskId,
        CancellationToken cancellationToken = default)
    {
        var task = await store.GetTaskAsync(taskId, cancellationToken).ConfigureAwait(false);
        if (task is null)
        {
            return null;
        }

        var project = await store.GetProjectAsync(task.ProjectId, cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidDataException("任务关联的项目不存在。");
        var runTask = store.GetAgentRunByTaskAsync(taskId, cancellationToken);
        var attemptsTask = store.GetAgentAttemptsAsync(taskId, cancellationToken);
        var decisionTask = store.GetPendingDecisionRequestAsync(taskId, cancellationToken);
        var eventsTask = store.GetTaskEventsAsync(taskId, cancellationToken);
        var evidenceTask = store.GetTaskEvidenceAsync(taskId, cancellationToken);
        var resourceScopesTask = store.GetResourceScopesAsync(taskId, cancellationToken);
        var skillInvocationsTask = store.GetSkillInvocationsAsync(taskId, cancellationToken);
        await Task.WhenAll(
                runTask,
                attemptsTask,
                decisionTask,
                eventsTask,
                evidenceTask,
                resourceScopesTask,
                skillInvocationsTask)
            .ConfigureAwait(false);
        return new LocalTaskDetails(
            task,
            project,
            await runTask.ConfigureAwait(false),
            await attemptsTask.ConfigureAwait(false),
            await decisionTask.ConfigureAwait(false),
            await eventsTask.ConfigureAwait(false),
            await evidenceTask.ConfigureAwait(false),
            await resourceScopesTask.ConfigureAwait(false),
            await skillInvocationsTask.ConfigureAwait(false));
    }

    public async Task<LocalTaskStateSnapshot?> ReadTaskStateAsync(
        Guid taskId,
        CancellationToken cancellationToken = default)
    {
        var taskTask = store.GetTaskAsync(taskId, cancellationToken);
        var runTask = store.GetAgentRunByTaskAsync(taskId, cancellationToken);
        var evidenceTask = store.GetTaskEvidenceAsync(taskId, cancellationToken);
        await Task.WhenAll(taskTask, runTask, evidenceTask).ConfigureAwait(false);
        var task = await taskTask.ConfigureAwait(false);
        if (task is null)
        {
            return null;
        }

        var run = await runTask.ConfigureAwait(false);
        var evidence = await evidenceTask.ConfigureAwait(false);
        var terminal = task.Status is Core.Tasking.TaskStatus.Succeeded
            or Core.Tasking.TaskStatus.Failed
            or Core.Tasking.TaskStatus.Cancelled
            or Core.Tasking.TaskStatus.Interrupted;
        return new LocalTaskStateSnapshot(
            task.Id,
            task.Version,
            run?.LastEventSequence ?? 0,
            terminal && evidence is not null,
            task.Status,
            evidence?.UserSummary,
            task.FailureCode ?? (task.Status == Core.Tasking.TaskStatus.Interrupted
                ? run?.FailureCode
                : null),
            task.FailureMessage ?? (task.Status == Core.Tasking.TaskStatus.Interrupted
                ? run?.FailureMessage
                : null),
            task.CompletedAtUtc);
    }

    public async Task<LocalTaskCommandResult> CreateTaskAsync(
        CreateLocalTaskRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var instruction = RequireText(request.Instruction, nameof(request.Instruction));
        var state = RequireStartedHost();
        var project = await RequireAuthorizedProjectAsync(request.ProjectId, cancellationToken)
            .ConfigureAwait(false);
        _ = ProjectPathPolicy.ResolveWithinRoot(project.RootPath, request.WorkingDirectoryRelativePath);
        var now = timeProvider.GetUtcNow();
        var command = NewCommand(
            state.LocalDevice!.Id,
            project.Id,
            null,
            CommandType.CreateTask,
            request.IdempotencyKey,
            JsonSerializer.Serialize(new
            {
                instruction,
                title = request.Title,
                workingDirectoryRelativePath = request.WorkingDirectoryRelativePath
            }),
            now);
        var registration = await store.RegisterCommandAsync(command, cancellationToken)
            .ConfigureAwait(false);
        if (!registration.Accepted)
        {
            if (registration.Command.TaskId is not { } existingTaskId)
            {
                throw new InvalidOperationException("重复的创建命令尚未关联任务，请稍后刷新。");
            }

            return new LocalTaskCommandResult(existingTaskId, true);
        }

        var task = new AgentTask
        {
            Id = Guid.NewGuid(),
            ProjectId = project.Id,
            CreatedByDeviceId = state.LocalDevice.Id,
            Title = NormalizeTitle(request.Title, instruction),
            Instruction = instruction,
            WorkingDirectoryRelativePath = request.WorkingDirectoryRelativePath,
            Executor = "codex",
            Status = Core.Tasking.TaskStatus.Pending,
            CreatedAtUtc = now,
            UpdatedAtUtc = now,
            Version = 0
        };
        await store.CreateTaskAsync(task, command.Id, cancellationToken).ConfigureAwait(false);
        await executionService.StartTaskAsync(task.Id, command.Id, cancellationToken).ConfigureAwait(false);
        return new LocalTaskCommandResult(task.Id, false);
    }

    public async Task<LocalTaskCommandResult> ContinueTaskAsync(
        Guid taskId,
        string responseText,
        string? idempotencyKey = null,
        CancellationToken cancellationToken = default)
    {
        var response = RequireText(responseText, nameof(responseText));
        var task = await RequireTaskAsync(taskId, cancellationToken).ConfigureAwait(false);
        var command = NewCommand(
            RequireStartedHost().LocalDevice!.Id,
            task.ProjectId,
            task.Id,
            CommandType.UserResponse,
            idempotencyKey,
            JsonSerializer.Serialize(new { response }),
            timeProvider.GetUtcNow());
        var registration = await store.RegisterCommandAsync(command, cancellationToken)
            .ConfigureAwait(false);
        if (!registration.Accepted)
        {
            return new LocalTaskCommandResult(taskId, true);
        }

        try
        {
            await executionService.ContinueTaskAsync(taskId, response, command.Id, cancellationToken)
                .ConfigureAwait(false);
            await store.CompleteCommandAsync(
                command.Id,
                CommandStatus.Processed,
                timeProvider.GetUtcNow(),
                cancellationToken: cancellationToken).ConfigureAwait(false);
            return new LocalTaskCommandResult(taskId, false);
        }
        catch (Exception exception)
        {
            await TryMarkCommandFailedAsync(command.Id, exception, cancellationToken).ConfigureAwait(false);
            throw;
        }
    }

    public async Task<LocalTaskCommandResult> CancelTaskAsync(
        Guid taskId,
        string? idempotencyKey = null,
        CancellationToken cancellationToken = default,
        long? expectedVersion = null)
    {
        var task = await RequireTaskAsync(taskId, cancellationToken).ConfigureAwait(false);
        var command = NewCommand(
            RequireStartedHost().LocalDevice!.Id,
            task.ProjectId,
            task.Id,
            CommandType.CancelTask,
            idempotencyKey,
            expectedVersion is null ? "{}" : JsonSerializer.Serialize(new { expectedVersion }),
            timeProvider.GetUtcNow());
        var registration = await store.RegisterCommandAsync(command, cancellationToken)
            .ConfigureAwait(false);
        if (!registration.Accepted)
        {
            var previous = registration.Command;
            if (previous.CommandType != CommandType.CancelTask || previous.TaskId != taskId
                || previous.ProjectId != task.ProjectId || previous.PayloadJson != command.PayloadJson)
            {
                throw new InvalidOperationException("request_conflict");
            }

            return new LocalTaskCommandResult(taskId, true, previous.Status == CommandStatus.Processed);
        }

        try
        {
            var accepted = await executionService.CancelTaskAsync(
                taskId,
                command.SourceDeviceId,
                command.Id,
                cancellationToken,
                expectedVersion).ConfigureAwait(false);
            await store.CompleteCommandAsync(
                command.Id,
                accepted ? CommandStatus.Processed : CommandStatus.Rejected,
                timeProvider.GetUtcNow(),
                accepted ? null : "task_not_cancellable",
                cancellationToken).ConfigureAwait(false);
            return new LocalTaskCommandResult(taskId, false, accepted);
        }
        catch (Exception exception)
        {
            await TryMarkCommandFailedAsync(command.Id, exception, cancellationToken).ConfigureAwait(false);
            throw;
        }
    }

    private DesktopHostSnapshot RequireStartedHost()
    {
        var snapshot = hostState.Snapshot;
        if (!snapshot.IsStarted || snapshot.LocalDevice is null)
        {
            throw new InvalidOperationException("Desktop Host 尚未启动。");
        }

        return snapshot;
    }

    private async Task<ProjectRecord> RequireAuthorizedProjectAsync(
        Guid projectId,
        CancellationToken cancellationToken)
    {
        var project = await store.GetProjectAsync(projectId, cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidOperationException("项目不存在。");
        if (project.AuthorizationState != ProjectAuthorizationState.Authorized)
        {
            throw new UnauthorizedAccessException("项目未获得授权。");
        }

        return project;
    }

    private async Task<AgentTask> RequireTaskAsync(Guid taskId, CancellationToken cancellationToken) =>
        await store.GetTaskAsync(taskId, cancellationToken).ConfigureAwait(false)
        ?? throw new InvalidOperationException("任务不存在。");

    private CommandRecord NewCommand(
        Guid sourceDeviceId,
        Guid projectId,
        Guid? taskId,
        CommandType type,
        string? idempotencyKey,
        string payloadJson,
        DateTimeOffset now) => new()
        {
            Id = Guid.NewGuid(),
            SourceDeviceId = sourceDeviceId,
            ProjectId = projectId,
            TaskId = taskId,
            IdempotencyKey = string.IsNullOrWhiteSpace(idempotencyKey)
                ? $"desktop-{type}-{Guid.NewGuid():N}"
                : idempotencyKey.Trim(),
            CommandType = type,
            PayloadJson = payloadJson,
            ReceivedAtUtc = now,
            ExpiresAtUtc = now.AddMinutes(10),
            Status = CommandStatus.Received
        };

    private async Task TryMarkCommandFailedAsync(
        Guid commandId,
        Exception exception,
        CancellationToken cancellationToken)
    {
        try
        {
            await store.CompleteCommandAsync(
                commandId,
                CommandStatus.Failed,
                timeProvider.GetUtcNow(),
                exception.GetType().Name,
                cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            // 保留原始执行异常；命令完成失败会由 Host 日志和任务事件继续暴露。
        }
    }

    private static string RequireText(string value, string parameterName) =>
        string.IsNullOrWhiteSpace(value)
            ? throw new ArgumentException("内容不能为空。", parameterName)
            : value.Trim();

    private static string NormalizeTitle(string? title, string instruction)
    {
        var value = string.IsNullOrWhiteSpace(title)
            ? instruction.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries).FirstOrDefault()
              ?? "新任务"
            : title.Trim();
        return value.Length <= 80 ? value : value[..80];
    }

    private static string NormalizeProjectName(string? name, string rootPath)
    {
        var value = string.IsNullOrWhiteSpace(name)
            ? new DirectoryInfo(rootPath).Name
            : name.Trim();
        if (string.IsNullOrWhiteSpace(value))
        {
            value = "本地项目";
        }

        return value.Length <= 80 ? value : value[..80];
    }
}
