namespace ScreenGuide.Core.Tasking;

public sealed record CommandRegistrationResult(bool Accepted, CommandRecord Command);

public sealed record RecoveryResult(IReadOnlyList<Guid> InterruptedTaskIds);

public interface ILocalTaskStore : IAsyncDisposable
{
    Task InitializeAsync(CancellationToken cancellationToken = default);

    Task<int> GetSchemaVersionAsync(CancellationToken cancellationToken = default);

    Task UpsertDeviceAsync(DeviceRecord device, CancellationToken cancellationToken = default);

    Task SetProjectAuthorizationAsync(ProjectRecord project, CancellationToken cancellationToken = default);

    Task<DeviceRecord?> GetDeviceAsync(Guid deviceId, CancellationToken cancellationToken = default);

    Task<DeviceRecord?> GetLocalHostDeviceAsync(CancellationToken cancellationToken = default);

    Task<ProjectRecord?> GetProjectAsync(Guid projectId, CancellationToken cancellationToken = default);

    Task<ProjectAuthorizationRecord?> GetProjectAuthorizationAsync(
        Guid projectId,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<ProjectRecord>> GetAuthorizedProjectsAsync(
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<ProjectRecord>> GetProjectsAsync(
        bool includeRevoked = false,
        CancellationToken cancellationToken = default);

    Task<AgentTask?> GetTaskAsync(Guid taskId, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<AgentTask>> GetTasksAsync(
        Guid? projectId = null,
        CancellationToken cancellationToken = default);

    Task<CommandRecord?> GetCommandAsync(Guid commandId, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<CommandRecord>> GetTaskCommandsAsync(
        Guid taskId,
        CancellationToken cancellationToken = default);

    Task<AgentRunRecord?> GetAgentRunByTaskAsync(
        Guid taskId,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<AgentAttemptRecord>> GetAgentAttemptsAsync(
        Guid taskId,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<ResourceScopeRecord>> GetResourceScopesAsync(
        Guid taskId,
        CancellationToken cancellationToken = default);

    Task UpsertResourceScopeAsync(
        ResourceScopeRecord scope,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<SkillInvocationRecord>> GetSkillInvocationsAsync(
        Guid taskId,
        CancellationToken cancellationToken = default);

    Task UpsertSkillInvocationAsync(
        SkillInvocationRecord invocation,
        CancellationToken cancellationToken = default);

    Task<DecisionRequestRecord?> GetPendingDecisionRequestAsync(
        Guid taskId,
        CancellationToken cancellationToken = default);

    Task<TaskEvidence?> GetTaskEvidenceAsync(
        Guid taskId,
        CancellationToken cancellationToken = default);

    Task<CommandRegistrationResult> RegisterCommandAsync(
        CommandRecord command,
        CancellationToken cancellationToken = default);

    Task CompleteCommandAsync(
        Guid commandId,
        CommandStatus status,
        DateTimeOffset processedAtUtc,
        string? rejectionReason = null,
        CancellationToken cancellationToken = default);

    Task CreateTaskAsync(
        AgentTask task,
        Guid sourceCommandId,
        CancellationToken cancellationToken = default);

    Task CreateAgentAttemptAsync(
        AgentRunRecord run,
        AgentAttemptRecord attempt,
        CancellationToken cancellationToken = default);

    Task RecordAgentStartedAsync(
        Guid taskId,
        Guid agentRunId,
        Guid attemptId,
        string externalRunId,
        string connectorVersion,
        int processId,
        DateTimeOffset startedAtUtc,
        CancellationToken cancellationToken = default);

    Task RecordAgentCancellationRequestedAsync(
        Guid taskId,
        Guid attemptId,
        DateTimeOffset requestedAtUtc,
        CancellationToken cancellationToken = default);

    Task<AgentEventApplyResult> ApplyAgentEventAsync(
        AgentEventApplyRequest request,
        CancellationToken cancellationToken = default);

    Task UpsertTaskEvidenceAsync(
        TaskEvidence evidence,
        CancellationToken cancellationToken = default);

    Task<AgentTask> TransitionTaskAsync(
        Guid taskId,
        TaskStatus newStatus,
        TaskEventSource source,
        string message,
        Guid? sourceDeviceId = null,
        Guid? commandId = null,
        string? dataJson = null,
        string? failureCode = null,
        string? failureMessage = null,
        CancellationToken cancellationToken = default);

    Task<AgentTask> TransitionTaskPhaseAsync(
        Guid taskId,
        TaskPhase newPhase,
        TaskEventSource source,
        string message,
        Guid? sourceDeviceId = null,
        Guid? commandId = null,
        string? dataJson = null,
        CancellationToken cancellationToken = default);

    Task<bool> RequestCancellationAsync(
        Guid taskId,
        Guid sourceDeviceId,
        Guid? commandId = null,
        CancellationToken cancellationToken = default,
        long? expectedVersion = null);

    Task<RecoveryResult> RecoverInterruptedTasksAsync(
        DateTimeOffset recoveredAtUtc,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<TaskEventRecord>> GetTaskEventsAsync(
        Guid taskId,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<AuditLogEntry>> GetAuditLogAsync(
        CancellationToken cancellationToken = default);

    Task AppendAuditAsync(
        AuditLogEntry entry,
        CancellationToken cancellationToken = default);

    Task<int> DeleteTerminalTaskHistoryAsync(
        DateTimeOffset deletedAtUtc,
        Guid actorDeviceId,
        CancellationToken cancellationToken = default);
}
