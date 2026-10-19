using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using ScreenGuide.Agent.Abstractions;
using ScreenGuide.Agent.Codex;
using ScreenGuide.Core.Security;
using ScreenGuide.Core.Tasking;
using ScreenGuide.Evidence;
using ScreenGuide.Persistence.Runtime;
using ScreenGuide.Skills.Abstractions;
using AgentTaskStatus = ScreenGuide.Core.Tasking.TaskStatus;

namespace ScreenGuide.DesktopHost.Runtime;

internal sealed class CodexTaskStartException(Exception innerException)
    : InvalidOperationException("Codex project task process failed to start.", innerException);

public sealed class AgentTaskExecutionService(
    ILocalTaskStore store,
    AgentConnectorRegistry connectorRegistry,
    TaskSkillRouter skillRouter,
    SkillAdapterRegistry skillAdapters,
    CapabilityPolicyEngine policyEngine,
    TaskCancellationService cancellationService,
    TaskCancellationRegistry cancellationRegistry,
    TaskEvidenceService evidenceService,
    TaskStateChangeHub taskStateChanges,
    TimeProvider timeProvider) : IAsyncDisposable
{
    private readonly ConcurrentDictionary<Guid, Task> _attemptPumps = new();
    private readonly ConcurrentDictionary<Guid, SemaphoreSlim> _taskGates = new();
    private bool _disposed;

    public async Task<AgentRunReference> StartTaskAsync(
        Guid taskId,
        Guid? commandId = null,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        var gate = _taskGates.GetOrAdd(taskId, static _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var task = await RequireTaskAsync(taskId, cancellationToken).ConfigureAwait(false);
            if (task.Status != AgentTaskStatus.Pending)
            {
                throw new InvalidOperationException("只有 Pending 任务可以首次启动 Agent。");
            }

            if (await store.GetAgentRunByTaskAsync(taskId, cancellationToken).ConfigureAwait(false)
                is not null)
            {
                throw new InvalidOperationException("任务已经存在 Agent Run，不能创建第二个 Thread。");
            }

            var project = await RequireAuthorizedProjectAsync(task.ProjectId, cancellationToken)
                .ConfigureAwait(false);
            var actionCommand = await RequireActionCommandAsync(
                    task,
                    commandId,
                    CommandType.CreateTask,
                    allowTaskCommandLookup: true,
                    cancellationToken)
                .ConfigureAwait(false);
            var workingDirectory = ProjectPathPolicy.ResolveWithinRoot(
                project.RootPath,
                task.WorkingDirectoryRelativePath);
            var route = skillRouter.Route(task);
            var scope = await RequireProjectScopeAsync(task, project, cancellationToken)
                .ConfigureAwait(false);
            var now = timeProvider.GetUtcNow();
            var invocation = new SkillInvocationRecord
            {
                Id = Guid.NewGuid(),
                TaskId = task.Id,
                SequenceNumber = 1,
                SkillId = route.Adapter.Descriptor.Id,
                SkillVersion = route.Adapter.Descriptor.Version,
                Capability = route.Capability,
                InputJson = JsonSerializer.Serialize(new CodexSkillInput(
                    project.RootPath,
                    workingDirectory,
                    task.Instruction)),
                Status = SkillInvocationStatus.Pending,
                CreatedAtUtc = now
            };
            await store.TransitionTaskPhaseAsync(
                    task.Id,
                    TaskPhase.Routing,
                    TaskEventSource.System,
                    "正在选择并检查任务执行能力。",
                    task.CreatedByDeviceId,
                    actionCommand.Id,
                    cancellationToken: cancellationToken)
                .ConfigureAwait(false);
            await store.UpsertSkillInvocationAsync(invocation, cancellationToken).ConfigureAwait(false);

            var authorizationId = actionCommand.Id;
            var skillRequest = new SkillInvocationRequest(
                task.Id,
                invocation.Id,
                invocation.SkillId,
                invocation.Capability,
                invocation.InputJson,
                [scope],
                SkillAuthorizationOrigin.ExplicitUser,
                authorizationId);
            await AuthorizeAsync(
                    route.Adapter,
                    skillRequest,
                    task.CreatedByDeviceId,
                    cancellationToken)
                .ConfigureAwait(false);
            await evidenceService.CaptureBaselineAsync(task, project, cancellationToken)
                .ConfigureAwait(false);
            await store.TransitionTaskPhaseAsync(
                    task.Id,
                    TaskPhase.Executing,
                    TaskEventSource.System,
                    "权限检查通过，开始执行 Codex Skill。",
                    task.CreatedByDeviceId,
                    actionCommand.Id,
                    cancellationToken: cancellationToken)
                .ConfigureAwait(false);

            var run = new AgentRunRecord
            {
                Id = Guid.NewGuid(),
                TaskId = task.Id,
                ConnectorId = task.Executor,
                Transport = $"skill/{invocation.SkillId}",
                Status = AgentRunStatus.Starting,
                CreatedAtUtc = now,
                UpdatedAtUtc = now,
                LastEventSequence = 0
            };
            var attempt = NewAttempt(
                run,
                task,
                actionCommand.Id,
                1,
                AgentAttemptOperation.Start,
                task.Instruction,
                now);
            await store.CreateAgentAttemptAsync(run, attempt, cancellationToken).ConfigureAwait(false);
            invocation = invocation with
            {
                Status = SkillInvocationStatus.Running,
                StartedAtUtc = now
            };
            await store.UpsertSkillInvocationAsync(invocation, cancellationToken).ConfigureAwait(false);
            _ = cancellationRegistry.GetOrCreateToken(task.Id);

            SkillStartResult start;
            try
            {
                start = await route.Adapter.StartAsync(
                        skillRequest with { AttemptId = attempt.Id },
                        cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (Exception exception) when (
                exception is not OperationCanceledException
                && string.Equals(
                    route.Adapter.Descriptor.Id,
                    "codex.project-task",
                    StringComparison.Ordinal))
            {
                throw new CodexTaskStartException(exception);
            }
            if (start.Status == SkillExecutionStatus.Running)
            {
                await RecordStartedAsync(task.Id, run.Id, attempt.Id, start, cancellationToken)
                    .ConfigureAwait(false);
            }

            StartPump(
                route.Adapter,
                start.Run,
                run.Id,
                attempt.Id,
                invocation.Id,
                run.LastEventSequence);
            return ToAgentRun(start.Run);
        }
        finally
        {
            gate.Release();
        }
    }

    public async Task<AgentRunReference> ContinueTaskAsync(
        Guid taskId,
        string responseText,
        Guid? commandId = null,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (string.IsNullOrWhiteSpace(responseText))
        {
            throw new ArgumentException("用户补充指令不能为空。", nameof(responseText));
        }

        var gate = _taskGates.GetOrAdd(taskId, static _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var task = await RequireTaskAsync(taskId, cancellationToken).ConfigureAwait(false);
            if (task.Status != AgentTaskStatus.WaitingForUser)
            {
                throw new InvalidOperationException("只有 WaitingForUser 任务可以继续当前 Codex Thread。");
            }


            var actionCommand = await RequireActionCommandAsync(
                    task,
                    commandId,
                    CommandType.UserResponse,
                    allowTaskCommandLookup: false,
                    cancellationToken)
                .ConfigureAwait(false);

            var run = await store.GetAgentRunByTaskAsync(taskId, cancellationToken).ConfigureAwait(false)
                ?? throw new InvalidOperationException("任务没有可续接的 Agent Run。");
            if (string.IsNullOrWhiteSpace(run.ExternalRunId))
            {
                throw new InvalidOperationException("Agent Run 没有持久化 ExternalRunId。");
            }

            var decision = await store.GetPendingDecisionRequestAsync(taskId, cancellationToken)
                .ConfigureAwait(false)
                ?? throw new InvalidOperationException("任务没有待回答的 Decision Request。");
            var invocation = (await store.GetSkillInvocationsAsync(taskId, cancellationToken)
                    .ConfigureAwait(false))
                .OrderByDescending(item => item.SequenceNumber)
                .FirstOrDefault()
                ?? throw new InvalidOperationException("任务没有可续接的 Skill Invocation。");
            if (invocation.Status != SkillInvocationStatus.WaitingForUser)
            {
                throw new InvalidOperationException("Skill Invocation 当前不等待用户回答。");
            }

            var project = await RequireAuthorizedProjectAsync(task.ProjectId, cancellationToken)
                .ConfigureAwait(false);
            var scope = await RequireProjectScopeAsync(task, project, cancellationToken)
                .ConfigureAwait(false);
            var attempts = await store.GetAgentAttemptsAsync(taskId, cancellationToken)
                .ConfigureAwait(false);
            var now = timeProvider.GetUtcNow();
            var attempt = NewAttempt(
                run,
                task,
                actionCommand.Id,
                attempts.Count + 1,
                AgentAttemptOperation.Resume,
                responseText,
                now);
            var adapter = skillAdapters.GetRequired(invocation.SkillId);
            await store.TransitionTaskPhaseAsync(
                    task.Id,
                    TaskPhase.Routing,
                    TaskEventSource.User,
                    "已收到补充指令，正在重新检查执行权限。",
                    task.CreatedByDeviceId,
                    actionCommand.Id,
                    cancellationToken: cancellationToken)
                .ConfigureAwait(false);
            var authorizationRequest = new SkillInvocationRequest(
                task.Id,
                invocation.Id,
                invocation.SkillId,
                invocation.Capability,
                invocation.InputJson,
                [scope],
                SkillAuthorizationOrigin.ExplicitUser,
                actionCommand.Id,
                attempt.Id);
            await AuthorizeAsync(
                    adapter,
                    authorizationRequest,
                    task.CreatedByDeviceId,
                    cancellationToken)
                .ConfigureAwait(false);
            await store.TransitionTaskPhaseAsync(
                    task.Id,
                    TaskPhase.Executing,
                    TaskEventSource.System,
                    "补充指令权限检查通过，继续原 Codex Thread。",
                    task.CreatedByDeviceId,
                    actionCommand.Id,
                    cancellationToken: cancellationToken)
                .ConfigureAwait(false);
            await store.CreateAgentAttemptAsync(run, attempt, cancellationToken).ConfigureAwait(false);
            invocation = invocation with
            {
                Status = SkillInvocationStatus.Running,
                CompletedAtUtc = null,
                FailureCode = null,
                FailureMessage = null
            };
            await store.UpsertSkillInvocationAsync(invocation, cancellationToken).ConfigureAwait(false);
            _ = cancellationRegistry.GetOrCreateToken(task.Id);

            var start = await adapter.RespondAsync(
                    new SkillDecisionResponse(
                        new SkillRunReference(
                            task.Id,
                            invocation.Id,
                            run.ExternalRunId,
                            attempt.Id),
                        decision.Id.ToString("D"),
                        responseText.Trim(),
                        attempt.Id,
                        run.LastEventSequence),
                    cancellationToken)
                .ConfigureAwait(false);
            if (start.Status == SkillExecutionStatus.Running)
            {
                await RecordStartedAsync(task.Id, run.Id, attempt.Id, start, cancellationToken)
                    .ConfigureAwait(false);
            }

            StartPump(
                adapter,
                start.Run,
                run.Id,
                attempt.Id,
                invocation.Id,
                run.LastEventSequence);
            return ToAgentRun(start.Run);
        }
        finally
        {
            gate.Release();
        }
    }

    public async Task<bool> CancelTaskAsync(
        Guid taskId,
        Guid sourceDeviceId,
        Guid? commandId = null,
        CancellationToken cancellationToken = default,
        long? expectedVersion = null)
    {
        var run = await store.GetAgentRunByTaskAsync(taskId, cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidOperationException("任务没有 Agent Run。");
        var attempts = await store.GetAgentAttemptsAsync(taskId, cancellationToken).ConfigureAwait(false);
        var attempt = attempts.LastOrDefault()
            ?? throw new InvalidOperationException("任务没有 Agent Attempt。");
        var invocation = (await store.GetSkillInvocationsAsync(taskId, cancellationToken)
                .ConfigureAwait(false))
            .OrderByDescending(item => item.SequenceNumber)
            .FirstOrDefault()
            ?? throw new InvalidOperationException("任务没有 Skill Invocation。");
        var accepted = await cancellationService.RequestAsync(
                taskId,
                sourceDeviceId,
                commandId,
                cancellationToken,
                expectedVersion)
            .ConfigureAwait(false);
        if (!accepted)
        {
            return false;
        }

        var requestedAt = timeProvider.GetUtcNow();
        await store.RecordAgentCancellationRequestedAsync(
                taskId,
                attempt.Id,
                requestedAt,
                cancellationToken)
            .ConfigureAwait(false);
        await PublishPersistedTaskStateAsync(taskId, finalized: false, cancellationToken)
            .ConfigureAwait(false);
        var adapter = skillAdapters.GetRequired(invocation.SkillId);
        await adapter.CancelAsync(
                new SkillRunReference(taskId, invocation.Id, run.ExternalRunId, attempt.Id),
                cancellationToken)
            .ConfigureAwait(false);
        await WaitForAttemptAsync(attempt.Id, cancellationToken).ConfigureAwait(false);
        return true;
    }

    public Task WaitForAttemptAsync(Guid attemptId, CancellationToken cancellationToken = default) =>
        _attemptPumps.TryGetValue(attemptId, out var pump)
            ? pump.WaitAsync(cancellationToken)
            : Task.CompletedTask;

    public async Task WaitForTaskAsync(Guid taskId, CancellationToken cancellationToken = default)
    {
        var attempts = await store.GetAgentAttemptsAsync(taskId, cancellationToken).ConfigureAwait(false);
        if (attempts.LastOrDefault() is { } attempt)
        {
            await WaitForAttemptAsync(attempt.Id, cancellationToken).ConfigureAwait(false);
        }
    }

    public async Task StopAsync(CancellationToken cancellationToken = default)
    {
        if (!connectorRegistry.IsInitialized)
        {
            return;
        }

        foreach (var connector in connectorRegistry.Connectors.OfType<IAsyncDisposable>())
        {
            await connector.DisposeAsync().ConfigureAwait(false);
        }

        var pumps = _attemptPumps.Values.ToArray();
        if (pumps.Length > 0)
        {
            await Task.WhenAll(pumps).WaitAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        await StopAsync(CancellationToken.None).ConfigureAwait(false);
        foreach (var gate in _taskGates.Values)
        {
            gate.Dispose();
        }
    }

    private void StartPump(
        ISkillAdapter adapter,
        SkillRunReference reference,
        Guid agentRunId,
        Guid attemptId,
        Guid invocationId,
        long afterSequence)
    {
        var pump = PumpEventsAsync(
            adapter,
            reference with { AttemptId = attemptId },
            agentRunId,
            attemptId,
            invocationId,
            afterSequence);
        if (!_attemptPumps.TryAdd(attemptId, pump))
        {
            throw new InvalidOperationException("Agent Attempt 事件泵重复启动。");
        }
    }

    private async Task PumpEventsAsync(
        ISkillAdapter adapter,
        SkillRunReference reference,
        Guid agentRunId,
        Guid attemptId,
        Guid invocationId,
        long afterSequence)
    {
        Exception? eventFeedFailure = null;
        Exception? completionFailure = null;
        var taskReachedTerminal = false;
        try
        {
            try
            {
                await foreach (var skillEvent in adapter.GetEventsAsync(
                                   reference,
                                   afterSequence,
                                   CancellationToken.None).ConfigureAwait(false))
                {
                    await TransitionPhaseForEventAsync(
                            reference.TaskId,
                            skillEvent,
                            CancellationToken.None)
                        .ConfigureAwait(false);
                    var request = await MapEventAsync(
                            adapter,
                            reference,
                            agentRunId,
                            attemptId,
                            skillEvent)
                        .ConfigureAwait(false);
                    var applied = await store.ApplyAgentEventAsync(request, CancellationToken.None)
                        .ConfigureAwait(false);
                    if (applied.Applied)
                    {
                        await UpdateInvocationForEventAsync(
                                reference.TaskId,
                                invocationId,
                                skillEvent,
                                CancellationToken.None)
                            .ConfigureAwait(false);
                        if (!IsTerminal(applied.Task.Status))
                        {
                            await PublishPersistedTaskStateAsync(
                                    reference.TaskId,
                                    finalized: false,
                                    CancellationToken.None)
                                .ConfigureAwait(false);
                        }
                    }
                }
            }
            catch (Exception exception)
            {
                eventFeedFailure = exception;
            }

            await PersistUnexpectedEventFeedEndAsync(
                    reference.TaskId,
                    agentRunId,
                    attemptId,
                    invocationId,
                    CancellationToken.None)
                .ConfigureAwait(false);
            cancellationRegistry.Complete(reference.TaskId);
            var evidence = await evidenceService.FinalizeIfTerminalAsync(
                    reference.TaskId,
                    CancellationToken.None)
                .ConfigureAwait(false);
            if (evidence is not null)
            {
                await PublishPersistedTaskStateAsync(
                        reference.TaskId,
                        finalized: true,
                        CancellationToken.None)
                    .ConfigureAwait(false);
            }

            taskReachedTerminal = (await store.GetTaskAsync(reference.TaskId, CancellationToken.None)
                    .ConfigureAwait(false)) is { Status: var status }
                && IsTerminal(status);
        }
        catch (Exception exception)
        {
            completionFailure = exception;
            throw;
        }
        finally
        {
            if (taskReachedTerminal || completionFailure is not null)
            {
                taskStateChanges.Complete(
                    reference.TaskId,
                    completionFailure ?? eventFeedFailure);
            }
        }
    }

    private async Task PersistUnexpectedEventFeedEndAsync(
        Guid taskId,
        Guid agentRunId,
        Guid attemptId,
        Guid invocationId,
        CancellationToken cancellationToken)
    {
        var task = await store.GetTaskAsync(taskId, cancellationToken).ConfigureAwait(false)
                   ?? throw new InvalidOperationException("任务状态已经不存在。");
        if (IsTerminal(task.Status) || task.Status == AgentTaskStatus.WaitingForUser)
        {
            return;
        }

        var cancellationWon = task.Status == AgentTaskStatus.CancellationRequested;
        var targetTaskStatus = cancellationWon
            ? AgentTaskStatus.Cancelled
            : AgentTaskStatus.Interrupted;
        var eventKind = cancellationWon ? "Cancelled" : "Interrupted";
        var safeCode = cancellationWon ? "user_cancelled" : "task_state_sync_failed";
        var safeMessage = cancellationWon
            ? "用户取消了编程任务。"
            : "编程任务状态事件源提前结束，已安全中断。";
        if (task.Phase != TaskPhase.Verifying
            && TaskPhaseStateMachine.CanTransition(task.Phase, TaskPhase.Verifying))
        {
            await store.TransitionTaskPhaseAsync(
                    taskId,
                    TaskPhase.Verifying,
                    TaskEventSource.System,
                    cancellationWon
                        ? "编程任务取消已进入最终核验。"
                        : "编程任务状态事件源提前结束，正在核验中断结果。",
                    cancellationToken: cancellationToken)
                .ConfigureAwait(false);
        }

        var run = await store.GetAgentRunByTaskAsync(taskId, cancellationToken)
            .ConfigureAwait(false)
            ?? throw new InvalidOperationException("任务没有 Agent Run。");
        var request = new AgentEventApplyRequest
        {
            TaskId = taskId,
            AgentRunId = agentRunId,
            AttemptId = attemptId,
            SequenceNumber = run.LastEventSequence + 1,
            ExternalEventId = $"host:{eventKind.ToLowerInvariant()}:{attemptId:D}",
            EventKind = eventKind,
            RunStatus = cancellationWon ? AgentRunStatus.Cancelled : AgentRunStatus.Interrupted,
            AttemptStatus = cancellationWon
                ? AgentAttemptStatus.Cancelled
                : AgentAttemptStatus.Interrupted,
            TaskStatus = targetTaskStatus,
            OccurredAtUtc = timeProvider.GetUtcNow(),
            Message = safeMessage,
            DataJson = JsonSerializer.Serialize(new { failureCode = safeCode }),
            FailureCode = safeCode,
            FailureMessage = safeMessage
        };
        AgentEventApplyResult applied;
        try
        {
            applied = await store.ApplyAgentEventAsync(request, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (InvalidOperationException)
        {
            var raced = await store.GetTaskAsync(taskId, cancellationToken).ConfigureAwait(false);
            if (raced is not null && IsTerminal(raced.Status))
            {
                return;
            }

            throw;
        }

        if (!applied.Applied)
        {
            return;
        }

        var invocation = (await store.GetSkillInvocationsAsync(taskId, cancellationToken)
                .ConfigureAwait(false))
            .Single(item => item.Id == invocationId);
        if (invocation.Status is not (
                SkillInvocationStatus.Succeeded or
                SkillInvocationStatus.Failed or
                SkillInvocationStatus.Cancelled or
                SkillInvocationStatus.Interrupted))
        {
            await store.UpsertSkillInvocationAsync(
                    invocation with
                    {
                        Status = cancellationWon
                            ? SkillInvocationStatus.Cancelled
                            : SkillInvocationStatus.Interrupted,
                        CompletedAtUtc = request.OccurredAtUtc,
                        FailureCode = safeCode,
                        FailureMessage = safeMessage
                    },
                    cancellationToken)
                .ConfigureAwait(false);
        }
    }

    private async Task PublishPersistedTaskStateAsync(
        Guid taskId,
        bool finalized,
        CancellationToken cancellationToken)
    {
        var taskTask = store.GetTaskAsync(taskId, cancellationToken);
        var runTask = store.GetAgentRunByTaskAsync(taskId, cancellationToken);
        var evidenceTask = finalized
            ? store.GetTaskEvidenceAsync(taskId, cancellationToken)
            : Task.FromResult<TaskEvidence?>(null);
        await Task.WhenAll(taskTask, runTask, evidenceTask).ConfigureAwait(false);
        var task = await taskTask.ConfigureAwait(false);
        if (task is null)
        {
            return;
        }

        var run = await runTask.ConfigureAwait(false);
        var evidence = await evidenceTask.ConfigureAwait(false);
        taskStateChanges.Publish(new LocalTaskStateSnapshot(
            task.Id,
            task.Version,
            run?.LastEventSequence ?? 0,
            finalized && evidence is not null,
            task.Status,
            evidence?.UserSummary,
            task.FailureCode,
            task.FailureMessage,
            task.CompletedAtUtc));
    }

    private static bool IsTerminal(AgentTaskStatus status) => status is
        AgentTaskStatus.Succeeded or AgentTaskStatus.Failed or AgentTaskStatus.Cancelled
        or AgentTaskStatus.Interrupted;

    private async Task<AgentEventApplyRequest> MapEventAsync(
        ISkillAdapter adapter,
        SkillRunReference reference,
        Guid agentRunId,
        Guid attemptId,
        SkillEvent skillEvent)
    {
        var status = skillEvent.Status ?? InferStatus(skillEvent.EventKind);
        var taskStatus = skillEvent.EventKind switch
        {
            SkillEventKind.DecisionRequested => AgentTaskStatus.WaitingForUser,
            SkillEventKind.Completed => AgentTaskStatus.Succeeded,
            SkillEventKind.Failed => AgentTaskStatus.Failed,
            SkillEventKind.Cancelled => AgentTaskStatus.Cancelled,
            SkillEventKind.Interrupted => AgentTaskStatus.Interrupted,
            _ => null as AgentTaskStatus?
        };
        SkillFinalResult? final = null;
        if (IsTerminalOrWaiting(skillEvent.EventKind))
        {
            final = await adapter.GetFinalResultAsync(reference, CancellationToken.None)
                .ConfigureAwait(false);
        }

        var decision = skillEvent.EventKind == SkillEventKind.DecisionRequested
            ? ParseDecisionRequest(reference.TaskId, agentRunId, attemptId, skillEvent)
            : null;
        var diagnostics = ParseDiagnostics(skillEvent.DataJson);
        return new AgentEventApplyRequest
        {
            TaskId = reference.TaskId,
            AgentRunId = agentRunId,
            AttemptId = attemptId,
            SequenceNumber = skillEvent.SequenceNumber,
            ExternalEventId = skillEvent.ExternalEventId
                ?? $"{attemptId:D}:{skillEvent.SequenceNumber}",
            EventKind = skillEvent.EventKind.ToString(),
            RunStatus = MapRunStatus(status),
            AttemptStatus = MapAttemptStatus(status),
            TaskStatus = taskStatus,
            OccurredAtUtc = skillEvent.OccurredAtUtc,
            Message = skillEvent.Message,
            DataJson = skillEvent.DataJson,
            FinalSummary = final?.Summary,
            FinalResultJson = final?.DataJson,
            FailureCode = skillEvent.EventKind == SkillEventKind.Failed
                ? diagnostics.FailureCode ?? "agent_failed"
                : null,
            FailureMessage = skillEvent.EventKind == SkillEventKind.Failed
                ? diagnostics.FailureMessage ?? skillEvent.Message
                : null,
            DecisionRequest = decision,
            ExitCode = final?.ExitCode ?? diagnostics.ExitCode
        };
    }

    private async Task TransitionPhaseForEventAsync(
        Guid taskId,
        SkillEvent skillEvent,
        CancellationToken cancellationToken)
    {
        var target = skillEvent.EventKind switch
        {
            SkillEventKind.DecisionRequested => TaskPhase.AwaitingPermission,
            SkillEventKind.Completed or SkillEventKind.Failed or SkillEventKind.Cancelled
                or SkillEventKind.Interrupted => TaskPhase.Verifying,
            _ => null as TaskPhase?
        };
        if (target is null)
        {
            return;
        }

        var task = await RequireTaskAsync(taskId, cancellationToken).ConfigureAwait(false);
        if (task.Phase == target || !TaskPhaseStateMachine.CanTransition(task.Phase, target.Value))
        {
            return;
        }

        await store.TransitionTaskPhaseAsync(
                taskId,
                target.Value,
                TaskEventSource.Agent,
                target == TaskPhase.AwaitingPermission
                    ? "Codex Skill 需要用户补充决定。"
                    : "Codex Skill 已结束执行，正在核验结果。",
                cancellationToken: cancellationToken)
            .ConfigureAwait(false);
    }

    private async Task UpdateInvocationForEventAsync(
        Guid taskId,
        Guid invocationId,
        SkillEvent skillEvent,
        CancellationToken cancellationToken)
    {
        if (skillEvent.EventKind is SkillEventKind.Progress or SkillEventKind.Diagnostic)
        {
            return;
        }

        var invocation = (await store.GetSkillInvocationsAsync(taskId, cancellationToken)
                .ConfigureAwait(false))
            .Single(item => item.Id == invocationId);
        var nextStatus = skillEvent.EventKind switch
        {
            SkillEventKind.Started => SkillInvocationStatus.Running,
            SkillEventKind.DecisionRequested => SkillInvocationStatus.WaitingForUser,
            SkillEventKind.Completed => SkillInvocationStatus.Succeeded,
            SkillEventKind.Failed => SkillInvocationStatus.Failed,
            SkillEventKind.Cancelled => SkillInvocationStatus.Cancelled,
            SkillEventKind.Interrupted => SkillInvocationStatus.Interrupted,
            _ => invocation.Status
        };
        if (nextStatus == invocation.Status)
        {
            return;
        }

        var terminal = nextStatus is SkillInvocationStatus.Succeeded
            or SkillInvocationStatus.Failed
            or SkillInvocationStatus.Cancelled
            or SkillInvocationStatus.Interrupted;
        var diagnostics = ParseDiagnostics(skillEvent.DataJson);
        await store.UpsertSkillInvocationAsync(
                invocation with
                {
                    Status = nextStatus,
                    StartedAtUtc = invocation.StartedAtUtc ?? skillEvent.OccurredAtUtc,
                    CompletedAtUtc = terminal ? skillEvent.OccurredAtUtc : null,
                    FailureCode = nextStatus == SkillInvocationStatus.Failed
                        ? diagnostics.FailureCode ?? "skill_failed"
                        : null,
                    FailureMessage = nextStatus == SkillInvocationStatus.Failed
                        ? diagnostics.FailureMessage ?? skillEvent.Message
                        : null
                },
                cancellationToken)
            .ConfigureAwait(false);
    }

    private async Task AuthorizeAsync(
        ISkillAdapter adapter,
        SkillInvocationRequest request,
        Guid actorDeviceId,
        CancellationToken cancellationToken)
    {
        var decision = policyEngine.AuthorizeOnce(adapter.Descriptor, request);
        await store.AppendAuditAsync(
                new AuditLogEntry
                {
                    Id = Guid.NewGuid(),
                    OccurredAtUtc = timeProvider.GetUtcNow(),
                    ActorDeviceId = actorDeviceId,
                    Action = decision.IsAllowed
                        ? "SkillAuthorizationAllowed"
                        : "SkillAuthorizationDenied",
                    EntityType = "SkillInvocation",
                    EntityId = request.InvocationId.ToString("D"),
                    Outcome = decision.IsAllowed ? AuditOutcome.Success : AuditOutcome.Rejected,
                    DetailsJson = JsonSerializer.Serialize(new
                    {
                        request.SkillId,
                        request.Capability,
                        request.ActionAuthorizationId,
                        decision.Code
                    })
                },
                cancellationToken)
            .ConfigureAwait(false);
        if (!decision.IsAllowed)
        {
            var invocation = (await store.GetSkillInvocationsAsync(request.TaskId, cancellationToken)
                    .ConfigureAwait(false))
                .Single(item => item.Id == request.InvocationId);
            await store.UpsertSkillInvocationAsync(
                    invocation with
                    {
                        Status = SkillInvocationStatus.Failed,
                        CompletedAtUtc = timeProvider.GetUtcNow(),
                        FailureCode = decision.Code,
                        FailureMessage = decision.UserMessage
                    },
                    cancellationToken)
                .ConfigureAwait(false);
            throw new UnauthorizedAccessException(decision.UserMessage);
        }
    }

    private async Task<SkillResourceScope> RequireProjectScopeAsync(
        AgentTask task,
        ProjectRecord project,
        CancellationToken cancellationToken)
    {
        var now = timeProvider.GetUtcNow();
        var scopes = await store.GetResourceScopesAsync(task.Id, cancellationToken).ConfigureAwait(false);
        var scope = scopes.SingleOrDefault(item =>
            item.ScopeType == ResourceScopeType.Project
            && item.ResourceId == project.Id
            && item.AccessMode == ResourceAccessMode.Execute
            && item.RevokedAtUtc is null
            && (item.ExpiresAtUtc is null || item.ExpiresAtUtc > now))
            ?? throw new UnauthorizedAccessException("任务缺少有效的项目执行范围授权。");
        return new SkillResourceScope(
            scope.ScopeType.ToString(),
            scope.ResourceId,
            project.RootPath,
            scope.AccessMode.ToString());
    }

    private static DecisionRequestRecord ParseDecisionRequest(
        Guid taskId,
        Guid agentRunId,
        Guid attemptId,
        SkillEvent skillEvent)
    {
        if (skillEvent.DataJson is null)
        {
            throw new InvalidDataException("DecisionRequested 事件缺少结构化数据。");
        }

        using var document = JsonDocument.Parse(skillEvent.DataJson);
        var root = document.RootElement;
        var requestId = Guid.Parse(root.GetProperty("decisionRequestId").GetString()!);
        var question = root.GetProperty("question").GetString();
        if (string.IsNullOrWhiteSpace(question))
        {
            throw new InvalidDataException("DecisionRequested 事件缺少 question。");
        }

        return new DecisionRequestRecord
        {
            Id = requestId,
            TaskId = taskId,
            AgentRunId = agentRunId,
            AttemptId = attemptId,
            Question = question,
            OptionsJson = root.GetProperty("decisionOptions").GetRawText(),
            Status = DecisionRequestStatus.Pending,
            CreatedAtUtc = skillEvent.OccurredAtUtc
        };
    }

    private static (string? FailureCode, string? FailureMessage, int? ExitCode) ParseDiagnostics(
        string? dataJson)
    {
        if (string.IsNullOrWhiteSpace(dataJson))
        {
            return (null, null, null);
        }

        using var document = JsonDocument.Parse(dataJson);
        var root = document.RootElement;
        var code = root.TryGetProperty("failureCode", out var codeElement)
            && codeElement.ValueKind == JsonValueKind.String
                ? codeElement.GetString()
                : null;
        var message = root.TryGetProperty("failureMessage", out var messageElement)
            && messageElement.ValueKind == JsonValueKind.String
                ? messageElement.GetString()
                : null;
        int? exitCode = null;
        if (root.TryGetProperty("ExitCode", out var exitElement)
            && exitElement.ValueKind == JsonValueKind.Number
            && exitElement.TryGetInt32(out var parsed))
        {
            exitCode = parsed;
        }

        return (code, message, exitCode);
    }

    private async Task RecordStartedAsync(
        Guid taskId,
        Guid runId,
        Guid attemptId,
        SkillStartResult start,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(start.Run.ExternalRunId)
            || string.IsNullOrWhiteSpace(start.AdapterVersion)
            || start.ProcessId is null)
        {
            throw new InvalidDataException("Skill Adapter 返回 Running，但缺少 Thread ID、版本或进程 ID。");
        }

        await store.RecordAgentStartedAsync(
                taskId,
                runId,
                attemptId,
                start.Run.ExternalRunId,
                start.AdapterVersion,
                start.ProcessId.Value,
                start.StartedAtUtc,
                cancellationToken)
            .ConfigureAwait(false);
    }

    private async Task<AgentTask> RequireTaskAsync(Guid taskId, CancellationToken cancellationToken) =>
        await store.GetTaskAsync(taskId, cancellationToken).ConfigureAwait(false)
        ?? throw new InvalidOperationException("任务不存在。");

    private async Task<ProjectRecord> RequireAuthorizedProjectAsync(
        Guid projectId,
        CancellationToken cancellationToken)
    {
        var project = await store.GetProjectAsync(projectId, cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidOperationException("任务项目不存在。");
        if (project.AuthorizationState != ProjectAuthorizationState.Authorized)
        {
            throw new UnauthorizedAccessException("项目未授权，禁止启动 Codex。");
        }

        var authorization = await store.GetProjectAuthorizationAsync(projectId, cancellationToken)
            .ConfigureAwait(false);
        if (authorization is null
            || authorization.State != ProjectAuthorizationState.Authorized
            || authorization.RevokedAtUtc is not null
            || !string.Equals(
                Path.GetFullPath(authorization.ScopeValue),
                Path.GetFullPath(project.RootPath),
                StringComparison.OrdinalIgnoreCase))
        {
            throw new UnauthorizedAccessException("项目授权记录无效，禁止启动 Codex。");
        }

        return project;
    }

    private async Task<CommandRecord> RequireActionCommandAsync(
        AgentTask task,
        Guid? commandId,
        CommandType expectedType,
        bool allowTaskCommandLookup,
        CancellationToken cancellationToken)
    {
        CommandRecord? command = null;
        if (commandId is not null)
        {
            command = await store.GetCommandAsync(commandId.Value, cancellationToken)
                .ConfigureAwait(false);
        }
        else if (allowTaskCommandLookup)
        {
            command = (await store.GetTaskCommandsAsync(task.Id, cancellationToken)
                    .ConfigureAwait(false))
                .LastOrDefault(item => item.CommandType == expectedType);
        }

        if (command is null
            || command.CommandType != expectedType
            || command.TaskId != task.Id
            || command.ProjectId != task.ProjectId
            || command.SourceDeviceId != task.CreatedByDeviceId)
        {
            throw new UnauthorizedAccessException("任务缺少与本次操作匹配的用户命令授权。");
        }

        var expectedStatus = expectedType == CommandType.CreateTask
            ? CommandStatus.Processed
            : CommandStatus.Received;
        if (command.Status != expectedStatus
            || command.ExpiresAtUtc is { } expiresAt && expiresAt <= timeProvider.GetUtcNow())
        {
            throw new UnauthorizedAccessException("本次用户命令已经失效或不能用于当前操作。");
        }

        return command;
    }

    private static AgentAttemptRecord NewAttempt(
        AgentRunRecord run,
        AgentTask task,
        Guid? commandId,
        int attemptNumber,
        AgentAttemptOperation operation,
        string input,
        DateTimeOffset now) => new()
        {
            Id = Guid.NewGuid(),
            AgentRunId = run.Id,
            TaskId = task.Id,
            CommandId = commandId,
            AttemptNumber = attemptNumber,
            Operation = operation,
            Status = AgentAttemptStatus.Starting,
            StartedAtUtc = now,
            LastEventSequence = run.LastEventSequence,
            InputHash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(input)))
        };

    private static SkillExecutionStatus InferStatus(SkillEventKind kind) => kind switch
    {
        SkillEventKind.Started or SkillEventKind.Progress => SkillExecutionStatus.Running,
        SkillEventKind.DecisionRequested => SkillExecutionStatus.WaitingForUser,
        SkillEventKind.Completed => SkillExecutionStatus.Succeeded,
        SkillEventKind.Failed => SkillExecutionStatus.Failed,
        SkillEventKind.Cancelled => SkillExecutionStatus.Cancelled,
        SkillEventKind.Interrupted => SkillExecutionStatus.Interrupted,
        _ => SkillExecutionStatus.Unknown
    };

    private static AgentRunStatus MapRunStatus(SkillExecutionStatus status) => status switch
    {
        SkillExecutionStatus.Starting => AgentRunStatus.Starting,
        SkillExecutionStatus.Running or SkillExecutionStatus.Unknown => AgentRunStatus.Running,
        SkillExecutionStatus.WaitingForUser => AgentRunStatus.WaitingForUser,
        SkillExecutionStatus.Succeeded => AgentRunStatus.Succeeded,
        SkillExecutionStatus.Failed => AgentRunStatus.Failed,
        SkillExecutionStatus.Cancelled => AgentRunStatus.Cancelled,
        SkillExecutionStatus.Interrupted => AgentRunStatus.Interrupted,
        _ => throw new ArgumentOutOfRangeException(nameof(status))
    };

    private static AgentAttemptStatus MapAttemptStatus(SkillExecutionStatus status) => status switch
    {
        SkillExecutionStatus.Starting => AgentAttemptStatus.Starting,
        SkillExecutionStatus.Running or SkillExecutionStatus.Unknown => AgentAttemptStatus.Running,
        SkillExecutionStatus.WaitingForUser or SkillExecutionStatus.Succeeded =>
            AgentAttemptStatus.Completed,
        SkillExecutionStatus.Failed => AgentAttemptStatus.Failed,
        SkillExecutionStatus.Cancelled => AgentAttemptStatus.Cancelled,
        SkillExecutionStatus.Interrupted => AgentAttemptStatus.Interrupted,
        _ => throw new ArgumentOutOfRangeException(nameof(status))
    };

    private static bool IsTerminalOrWaiting(SkillEventKind kind) => kind is
        SkillEventKind.DecisionRequested or
        SkillEventKind.Completed or
        SkillEventKind.Failed or
        SkillEventKind.Cancelled or
        SkillEventKind.Interrupted;

    private static AgentRunReference ToAgentRun(SkillRunReference run) =>
        new(run.TaskId, run.ExternalRunId, run.AttemptId);
}
