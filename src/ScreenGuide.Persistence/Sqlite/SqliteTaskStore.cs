using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Data.Sqlite;
using ScreenGuide.Core.Tasking;
using TaskStatus = ScreenGuide.Core.Tasking.TaskStatus;

namespace ScreenGuide.Persistence.Sqlite;

public sealed class SqliteTaskStore : ILocalTaskStore
{
    private static readonly JsonSerializerOptions EvidenceJsonOptions = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() }
    };
    private readonly string _databasePath;
    private readonly string _connectionString;

    public SqliteTaskStore(string databasePath)
    {
        if (string.IsNullOrWhiteSpace(databasePath))
        {
            throw new ArgumentException("数据库路径不能为空。", nameof(databasePath));
        }

        _databasePath = Path.GetFullPath(databasePath.Trim());
        _connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = _databasePath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Cache = SqliteCacheMode.Shared,
            ForeignKeys = true,
            Pooling = false
        }.ToString();
    }

    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        var parent = Path.GetDirectoryName(_databasePath);
        if (!string.IsNullOrEmpty(parent))
        {
            Directory.CreateDirectory(parent);
        }

        await using var connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            CREATE TABLE IF NOT EXISTS schema_info (
                version INTEGER NOT NULL PRIMARY KEY,
                applied_at_utc TEXT NOT NULL
            );
            """;
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        command.CommandText = "SELECT COALESCE(MAX(version), 0) FROM schema_info;";
        var storedVersion = Convert.ToInt32(
            await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false),
            CultureInfo.InvariantCulture);
        if (storedVersion > V02Contract.SchemaVersion)
        {
            throw new NotSupportedException(
                $"数据库版本 {storedVersion} 高于当前支持的版本 {V02Contract.SchemaVersion}。");
        }

        if (storedVersion == 8)
        {
            await ValidateVersion8ForVersion9MigrationAsync(connection, cancellationToken)
                .ConfigureAwait(false);
        }

        if (storedVersion == 9)
        {
            await ValidateVersion9ForVersion10MigrationAsync(connection, cancellationToken)
                .ConfigureAwait(false);
        }

        if (storedVersion == 10)
        {
            await ValidateVersion10Async(connection, cancellationToken).ConfigureAwait(false);
        }

        string? backupPath = null;
        if (storedVersion > 0 && storedVersion < V02Contract.SchemaVersion)
        {
            backupPath = await CreateMigrationBackupAsync(connection, storedVersion, cancellationToken)
                .ConfigureAwait(false);
        }

        try
        {
            if (storedVersion < 1)
            {
                command.CommandText = SqliteSchema.CreateVersion1;
                await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
                await RecordSchemaVersionAsync(connection, null, 1, cancellationToken).ConfigureAwait(false);
            }

            if (storedVersion < 2)
            {
                await ApplyMigrationAsync(connection, 2, SqliteSchema.CreateVersion2, cancellationToken)
                    .ConfigureAwait(false);
            }

            if (storedVersion < 3)
            {
                await ApplyMigrationAsync(connection, 3, SqliteSchema.CreateVersion3, cancellationToken)
                    .ConfigureAwait(false);
            }

            if (storedVersion < 4)
            {
                await ApplyMigrationAsync(connection, 4, SqliteSchema.CreateVersion4, cancellationToken)
                    .ConfigureAwait(false);
            }

            if (storedVersion < 5)
            {
                await ApplyMigrationAsync(connection, 5, SqliteSchema.CreateVersion5, cancellationToken)
                    .ConfigureAwait(false);
            }

            if (storedVersion < 6)
            {
                await ApplyMigrationAsync(connection, 6, SqliteSchema.CreateVersion6, cancellationToken)
                    .ConfigureAwait(false);
            }

            if (storedVersion < 7)
            {
                await ApplyMigrationAsync(connection, 7, SqliteSchema.CreateVersion7, cancellationToken)
                    .ConfigureAwait(false);
            }

            if (storedVersion < 8)
            {
                await ApplyMigrationAsync(connection, 8, SqliteSchema.CreateVersion8, cancellationToken)
                    .ConfigureAwait(false);
            }

            if (storedVersion < 9)
            {
                await ApplyMigrationAsync(connection, 9, SqliteSchema.CreateVersion9, cancellationToken)
                    .ConfigureAwait(false);
            }

            if (storedVersion < 10)
            {
                await ApplyMigrationAsync(connection, 10, SqliteSchema.CreateVersion10, cancellationToken)
                    .ConfigureAwait(false);
            }

            if (storedVersion < 11)
            {
                await ApplyMigrationAsync(connection, 11, SqliteSchema.CreateVersion11, cancellationToken)
                    .ConfigureAwait(false);
            }

            await ValidateVersion11Async(connection, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (backupPath is not null)
        {
            throw new InvalidOperationException(
                $"数据库升级失败，原数据备份保存在：{backupPath}",
                exception);
        }
    }

    public async Task<int> GetSchemaVersionAsync(CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT COALESCE(MAX(version), 0) FROM schema_info;";
        return Convert.ToInt32(
            await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false),
            CultureInfo.InvariantCulture);
    }

    public async Task UpsertDeviceAsync(
        DeviceRecord device,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(device);
        RequireText(device.DisplayName, nameof(device.DisplayName));

        await using var connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO devices(
                id, display_name, device_type, trust_state, public_key_thumbprint,
                created_at_utc, last_seen_at_utc, revoked_at_utc)
            VALUES(
                $id, $displayName, $deviceType, $trustState, $publicKeyThumbprint,
                $createdAtUtc, $lastSeenAtUtc, $revokedAtUtc)
            ON CONFLICT(id) DO UPDATE SET
                display_name = excluded.display_name,
                device_type = excluded.device_type,
                trust_state = excluded.trust_state,
                public_key_thumbprint = excluded.public_key_thumbprint,
                last_seen_at_utc = excluded.last_seen_at_utc,
                revoked_at_utc = excluded.revoked_at_utc;
            """;
        Add(command, "$id", device.Id);
        command.Parameters.AddWithValue("$displayName", device.DisplayName.Trim());
        command.Parameters.AddWithValue("$deviceType", device.DeviceType.ToString());
        command.Parameters.AddWithValue("$trustState", device.TrustState.ToString());
        AddNullable(command, "$publicKeyThumbprint", device.PublicKeyThumbprint);
        Add(command, "$createdAtUtc", device.CreatedAtUtc);
        AddNullable(command, "$lastSeenAtUtc", device.LastSeenAtUtc);
        AddNullable(command, "$revokedAtUtc", device.RevokedAtUtc);
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task SetProjectAuthorizationAsync(
        ProjectRecord project,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(project);
        RequireText(project.Name, nameof(project.Name));
        if (project.AuthorizationState == ProjectAuthorizationState.Authorized && project.RevokedAtUtc is not null)
        {
            throw new ArgumentException("已授权项目不能设置撤销时间。", nameof(project));
        }

        if (project.AuthorizationState == ProjectAuthorizationState.Revoked && project.RevokedAtUtc is null)
        {
            throw new ArgumentException("撤销项目必须记录撤销时间。", nameof(project));
        }

        var normalizedRoot = ProjectPathPolicy.NormalizeExistingRoot(project.RootPath);

        await using var connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = connection.BeginTransaction();
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO projects(
                id, name, root_path, authorization_state, authorized_by_device_id,
                authorized_at_utc, revoked_at_utc, created_at_utc, updated_at_utc)
            VALUES(
                $id, $name, $rootPath, $authorizationState, $authorizedByDeviceId,
                $authorizedAtUtc, $revokedAtUtc, $createdAtUtc, $updatedAtUtc)
            ON CONFLICT(id) DO UPDATE SET
                name = excluded.name,
                root_path = excluded.root_path,
                authorization_state = excluded.authorization_state,
                authorized_by_device_id = excluded.authorized_by_device_id,
                authorized_at_utc = excluded.authorized_at_utc,
                revoked_at_utc = excluded.revoked_at_utc,
                updated_at_utc = excluded.updated_at_utc;
            """;
        Add(command, "$id", project.Id);
        command.Parameters.AddWithValue("$name", project.Name.Trim());
        command.Parameters.AddWithValue("$rootPath", normalizedRoot);
        command.Parameters.AddWithValue("$authorizationState", project.AuthorizationState.ToString());
        Add(command, "$authorizedByDeviceId", project.AuthorizedByDeviceId);
        Add(command, "$authorizedAtUtc", project.AuthorizedAtUtc);
        AddNullable(command, "$revokedAtUtc", project.RevokedAtUtc);
        Add(command, "$createdAtUtc", project.CreatedAtUtc);
        Add(command, "$updatedAtUtc", project.UpdatedAtUtc);
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);

        await using (var authorization = connection.CreateCommand())
        {
            authorization.Transaction = transaction;
            authorization.CommandText = """
                INSERT INTO project_authorizations(
                    id, project_id, scope, scope_value, state,
                    authorized_by_device_id, authorized_at_utc, revoked_at_utc, updated_at_utc)
                VALUES(
                    $id, $projectId, $scope, $scopeValue, $state,
                    $authorizedByDeviceId, $authorizedAtUtc, $revokedAtUtc, $updatedAtUtc)
                ON CONFLICT(project_id) DO UPDATE SET
                    scope = excluded.scope,
                    scope_value = excluded.scope_value,
                    state = excluded.state,
                    authorized_by_device_id = excluded.authorized_by_device_id,
                    authorized_at_utc = excluded.authorized_at_utc,
                    revoked_at_utc = excluded.revoked_at_utc,
                    updated_at_utc = excluded.updated_at_utc;
                """;
            Add(authorization, "$id", project.Id);
            Add(authorization, "$projectId", project.Id);
            authorization.Parameters.AddWithValue(
                "$scope",
                ProjectAuthorizationScope.ProjectDirectory.ToString());
            authorization.Parameters.AddWithValue("$scopeValue", normalizedRoot);
            authorization.Parameters.AddWithValue("$state", project.AuthorizationState.ToString());
            Add(authorization, "$authorizedByDeviceId", project.AuthorizedByDeviceId);
            Add(authorization, "$authorizedAtUtc", project.AuthorizedAtUtc);
            AddNullable(authorization, "$revokedAtUtc", project.RevokedAtUtc);
            Add(authorization, "$updatedAtUtc", project.UpdatedAtUtc);
            await authorization.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        await InsertAuditAsync(
            connection,
            transaction,
            project.AuthorizedAtUtc,
            project.AuthorizedByDeviceId,
            project.AuthorizationState == ProjectAuthorizationState.Authorized
                ? "ProjectAuthorized"
                : "ProjectRevoked",
            "Project",
            project.Id.ToString("D"),
            AuditOutcome.Success,
            JsonSerializer.Serialize(new { rootPath = normalizedRoot }),
            cancellationToken).ConfigureAwait(false);
        transaction.Commit();
    }

    public Task<DeviceRecord?> GetDeviceAsync(Guid deviceId, CancellationToken cancellationToken = default) =>
        QuerySingleAsync(
            "SELECT * FROM devices WHERE id = $id;",
            deviceId,
            ReadDevice,
            cancellationToken);

    public async Task<DeviceRecord?> GetLocalHostDeviceAsync(
        CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT * FROM devices
            WHERE device_type = $deviceType AND trust_state = $trustState
            ORDER BY created_at_utc, id
            LIMIT 1;
            """;
        command.Parameters.AddWithValue("$deviceType", DeviceType.WindowsHost.ToString());
        command.Parameters.AddWithValue("$trustState", DeviceTrustState.Local.ToString());
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        return await reader.ReadAsync(cancellationToken).ConfigureAwait(false) ? ReadDevice(reader) : null;
    }

    public Task<ProjectRecord?> GetProjectAsync(Guid projectId, CancellationToken cancellationToken = default) =>
        QuerySingleAsync(
            "SELECT * FROM projects WHERE id = $id;",
            projectId,
            ReadProject,
            cancellationToken);

    public Task<ProjectAuthorizationRecord?> GetProjectAuthorizationAsync(
        Guid projectId,
        CancellationToken cancellationToken = default) =>
        QuerySingleAsync(
            "SELECT * FROM project_authorizations WHERE project_id = $id;",
            projectId,
            ReadProjectAuthorization,
            cancellationToken);

    public async Task<IReadOnlyList<ProjectRecord>> GetAuthorizedProjectsAsync(
        CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT * FROM projects
            WHERE authorization_state = $authorizationState
            ORDER BY name COLLATE NOCASE, id;
            """;
        command.Parameters.AddWithValue(
            "$authorizationState",
            ProjectAuthorizationState.Authorized.ToString());
        var projects = new List<ProjectRecord>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            projects.Add(ReadProject(reader));
        }

        return projects;
    }

    public async Task<IReadOnlyList<ProjectRecord>> GetProjectsAsync(
        bool includeRevoked = false,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = includeRevoked
            ? "SELECT * FROM projects ORDER BY name COLLATE NOCASE, id;"
            : "SELECT * FROM projects WHERE authorization_state = $authorizationState ORDER BY name COLLATE NOCASE, id;";
        if (!includeRevoked)
        {
            command.Parameters.AddWithValue(
                "$authorizationState",
                ProjectAuthorizationState.Authorized.ToString());
        }

        var projects = new List<ProjectRecord>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            projects.Add(ReadProject(reader));
        }

        return projects;
    }

    public Task<AgentTask?> GetTaskAsync(Guid taskId, CancellationToken cancellationToken = default) =>
        QuerySingleAsync(
            "SELECT * FROM tasks WHERE id = $id;",
            taskId,
            ReadTask,
            cancellationToken);

    public async Task<IReadOnlyList<AgentTask>> GetTasksAsync(
        Guid? projectId = null,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = projectId is null
            ? "SELECT * FROM tasks ORDER BY created_at_utc DESC, id DESC;"
            : "SELECT * FROM tasks WHERE project_id = $projectId ORDER BY created_at_utc DESC, id DESC;";
        if (projectId is { } id)
        {
            Add(command, "$projectId", id);
        }

        var tasks = new List<AgentTask>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            tasks.Add(ReadTask(reader));
        }

        return tasks;
    }

    public Task<CommandRecord?> GetCommandAsync(Guid commandId, CancellationToken cancellationToken = default) =>
        QuerySingleAsync(
            "SELECT * FROM commands WHERE id = $id;",
            commandId,
            ReadCommand,
            cancellationToken);

    public async Task<IReadOnlyList<CommandRecord>> GetTaskCommandsAsync(
        Guid taskId,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT * FROM commands
            WHERE task_id = $taskId
            ORDER BY received_at_utc, id;
            """;
        Add(command, "$taskId", taskId);
        var commands = new List<CommandRecord>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            commands.Add(ReadCommand(reader));
        }

        return commands;
    }

    public Task<AgentRunRecord?> GetAgentRunByTaskAsync(
        Guid taskId,
        CancellationToken cancellationToken = default) =>
        QuerySingleAsync(
            "SELECT * FROM agent_runs WHERE task_id = $id;",
            taskId,
            ReadAgentRun,
            cancellationToken);

    public async Task<IReadOnlyList<AgentAttemptRecord>> GetAgentAttemptsAsync(
        Guid taskId,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT * FROM agent_attempts
            WHERE task_id = $taskId
            ORDER BY attempt_number;
            """;
        Add(command, "$taskId", taskId);
        var attempts = new List<AgentAttemptRecord>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            attempts.Add(ReadAgentAttempt(reader));
        }

        return attempts;
    }

    public async Task<IReadOnlyList<ResourceScopeRecord>> GetResourceScopesAsync(
        Guid taskId,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT * FROM resource_scopes
            WHERE task_id = $taskId
            ORDER BY granted_at_utc, id;
            """;
        Add(command, "$taskId", taskId);
        var scopes = new List<ResourceScopeRecord>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            scopes.Add(ReadResourceScope(reader));
        }

        return scopes;
    }

    public async Task UpsertResourceScopeAsync(
        ResourceScopeRecord scope,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(scope);
        RequireText(scope.ScopeValue, nameof(scope.ScopeValue));
        await using var connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = connection.BeginTransaction();
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO resource_scopes(
                id, task_id, scope_type, resource_id, scope_value, access_mode,
                granted_by_device_id, granted_at_utc, expires_at_utc, revoked_at_utc)
            VALUES(
                $id, $taskId, $scopeType, $resourceId, $scopeValue, $accessMode,
                $grantedByDeviceId, $grantedAtUtc, $expiresAtUtc, $revokedAtUtc)
            ON CONFLICT(id) DO UPDATE SET
                scope_type = excluded.scope_type,
                resource_id = excluded.resource_id,
                scope_value = excluded.scope_value,
                access_mode = excluded.access_mode,
                expires_at_utc = excluded.expires_at_utc,
                revoked_at_utc = excluded.revoked_at_utc;
            """;
        Add(command, "$id", scope.Id);
        Add(command, "$taskId", scope.TaskId);
        command.Parameters.AddWithValue("$scopeType", scope.ScopeType.ToString());
        AddNullable(command, "$resourceId", scope.ResourceId);
        command.Parameters.AddWithValue("$scopeValue", scope.ScopeValue.Trim());
        command.Parameters.AddWithValue("$accessMode", scope.AccessMode.ToString());
        Add(command, "$grantedByDeviceId", scope.GrantedByDeviceId);
        Add(command, "$grantedAtUtc", scope.GrantedAtUtc);
        AddNullable(command, "$expiresAtUtc", scope.ExpiresAtUtc);
        AddNullable(command, "$revokedAtUtc", scope.RevokedAtUtc);
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        await InsertAuditAsync(
            connection,
            transaction,
            scope.GrantedAtUtc,
            scope.GrantedByDeviceId,
            "ResourceScopeRecorded",
            "Task",
            scope.TaskId.ToString("D"),
            AuditOutcome.Success,
            JsonSerializer.Serialize(new { scope.Id, scope.ScopeType, scope.AccessMode }),
            cancellationToken).ConfigureAwait(false);
        transaction.Commit();
    }

    public async Task<IReadOnlyList<SkillInvocationRecord>> GetSkillInvocationsAsync(
        Guid taskId,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT * FROM skill_invocations
            WHERE task_id = $taskId
            ORDER BY sequence_number;
            """;
        Add(command, "$taskId", taskId);
        var invocations = new List<SkillInvocationRecord>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            invocations.Add(ReadSkillInvocation(reader));
        }

        return invocations;
    }

    public async Task UpsertSkillInvocationAsync(
        SkillInvocationRecord invocation,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(invocation);
        RequireText(invocation.SkillId, nameof(invocation.SkillId));
        RequireText(invocation.SkillVersion, nameof(invocation.SkillVersion));
        RequireText(invocation.Capability, nameof(invocation.Capability));
        ValidateJson(invocation.InputJson, nameof(invocation.InputJson));
        if (invocation.SequenceNumber <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(invocation), "Skill 调用序号必须大于零。");
        }

        await using var connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = connection.BeginTransaction();
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO skill_invocations(
                id, task_id, sequence_number, skill_id, skill_version, capability,
                input_json, status, created_at_utc, started_at_utc, completed_at_utc,
                failure_code, failure_message)
            VALUES(
                $id, $taskId, $sequenceNumber, $skillId, $skillVersion, $capability,
                $inputJson, $status, $createdAtUtc, $startedAtUtc, $completedAtUtc,
                $failureCode, $failureMessage)
            ON CONFLICT(id) DO UPDATE SET
                skill_version = excluded.skill_version,
                capability = excluded.capability,
                input_json = excluded.input_json,
                status = excluded.status,
                started_at_utc = excluded.started_at_utc,
                completed_at_utc = excluded.completed_at_utc,
                failure_code = excluded.failure_code,
                failure_message = excluded.failure_message;
            """;
        Add(command, "$id", invocation.Id);
        Add(command, "$taskId", invocation.TaskId);
        command.Parameters.AddWithValue("$sequenceNumber", invocation.SequenceNumber);
        command.Parameters.AddWithValue("$skillId", invocation.SkillId.Trim());
        command.Parameters.AddWithValue("$skillVersion", invocation.SkillVersion.Trim());
        command.Parameters.AddWithValue("$capability", invocation.Capability.Trim());
        command.Parameters.AddWithValue("$inputJson", invocation.InputJson);
        command.Parameters.AddWithValue("$status", invocation.Status.ToString());
        Add(command, "$createdAtUtc", invocation.CreatedAtUtc);
        AddNullable(command, "$startedAtUtc", invocation.StartedAtUtc);
        AddNullable(command, "$completedAtUtc", invocation.CompletedAtUtc);
        AddNullable(command, "$failureCode", invocation.FailureCode);
        AddNullable(command, "$failureMessage", invocation.FailureMessage);
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        await InsertAuditAsync(
            connection,
            transaction,
            invocation.CompletedAtUtc ?? invocation.StartedAtUtc ?? invocation.CreatedAtUtc,
            null,
            "SkillInvocationRecorded",
            "Task",
            invocation.TaskId.ToString("D"),
            AuditOutcome.Success,
            JsonSerializer.Serialize(new
            {
                invocation.Id,
                invocation.SequenceNumber,
                invocation.SkillId,
                invocation.Status
            }),
            cancellationToken).ConfigureAwait(false);
        transaction.Commit();
    }

    public async Task<DecisionRequestRecord?> GetPendingDecisionRequestAsync(
        Guid taskId,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT * FROM decision_requests
            WHERE task_id = $taskId AND status = $status
            ORDER BY created_at_utc DESC
            LIMIT 1;
            """;
        Add(command, "$taskId", taskId);
        command.Parameters.AddWithValue("$status", DecisionRequestStatus.Pending.ToString());
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        return await reader.ReadAsync(cancellationToken).ConfigureAwait(false)
            ? ReadDecisionRequest(reader)
            : null;
    }

    public async Task<TaskEvidence?> GetTaskEvidenceAsync(
        Guid taskId,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT evidence_json FROM task_evidence WHERE task_id = $taskId;";
        Add(command, "$taskId", taskId);
        var value = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) as string;
        return value is null
            ? null
            : JsonSerializer.Deserialize<TaskEvidence>(value, EvidenceJsonOptions)
              ?? throw new InvalidDataException("TaskEvidence JSON 无效。 ");
    }

    public async Task<CommandRegistrationResult> RegisterCommandAsync(
        CommandRecord command,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(command);
        RequireText(command.IdempotencyKey, nameof(command.IdempotencyKey));
        ValidateJson(command.PayloadJson, nameof(command.PayloadJson));

        try
        {
            await using var connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
            await using var transaction = connection.BeginTransaction();
            await using var insert = connection.CreateCommand();
            insert.Transaction = transaction;
            insert.CommandText = """
                INSERT INTO commands(
                    id, source_device_id, project_id, task_id, idempotency_key,
                    command_type, payload_json, received_at_utc, expires_at_utc,
                    status, processed_at_utc, rejection_reason)
                VALUES(
                    $id, $sourceDeviceId, $projectId, $taskId, $idempotencyKey,
                    $commandType, $payloadJson, $receivedAtUtc, $expiresAtUtc,
                    $status, $processedAtUtc, $rejectionReason);
                """;
            Add(insert, "$id", command.Id);
            Add(insert, "$sourceDeviceId", command.SourceDeviceId);
            AddNullable(insert, "$projectId", command.ProjectId);
            AddNullable(insert, "$taskId", command.TaskId);
            insert.Parameters.AddWithValue("$idempotencyKey", command.IdempotencyKey.Trim());
            insert.Parameters.AddWithValue("$commandType", command.CommandType.ToString());
            insert.Parameters.AddWithValue("$payloadJson", command.PayloadJson);
            Add(insert, "$receivedAtUtc", command.ReceivedAtUtc);
            AddNullable(insert, "$expiresAtUtc", command.ExpiresAtUtc);
            insert.Parameters.AddWithValue("$status", command.Status.ToString());
            AddNullable(insert, "$processedAtUtc", command.ProcessedAtUtc);
            AddNullable(insert, "$rejectionReason", command.RejectionReason);
            await insert.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);

            await InsertAuditAsync(
                connection,
                transaction,
                command.ReceivedAtUtc,
                command.SourceDeviceId,
                "CommandRegistered",
                "Command",
                command.Id.ToString("D"),
                AuditOutcome.Success,
                JsonSerializer.Serialize(new { command.IdempotencyKey, command.CommandType }),
                cancellationToken).ConfigureAwait(false);
            transaction.Commit();
            return new CommandRegistrationResult(true, command);
        }
        catch (SqliteException exception) when (exception.SqliteErrorCode == 19)
        {
            var existing = await GetCommandByIdempotencyKeyAsync(
                command.SourceDeviceId,
                command.IdempotencyKey.Trim(),
                cancellationToken).ConfigureAwait(false);
            if (existing is null)
            {
                throw;
            }

            await RecordDuplicateCommandAsync(command, existing, cancellationToken).ConfigureAwait(false);
            return new CommandRegistrationResult(false, existing);
        }
    }

    public async Task CompleteCommandAsync(
        Guid commandId,
        CommandStatus status,
        DateTimeOffset processedAtUtc,
        string? rejectionReason = null,
        CancellationToken cancellationToken = default)
    {
        if (status is CommandStatus.Received)
        {
            throw new ArgumentException("完成命令不能保留 Received 状态。", nameof(status));
        }

        await using var connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = connection.BeginTransaction();
        var existing = await GetCommandInTransactionAsync(
            connection,
            transaction,
            commandId,
            cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidOperationException("命令不存在。");
        if (existing.Status != CommandStatus.Received)
        {
            if (existing.Status == status)
            {
                transaction.Commit();
                return;
            }

            throw new InvalidOperationException("命令已经结束，不能再次改变结果。");
        }

        await using var update = connection.CreateCommand();
        update.Transaction = transaction;
        update.CommandText = """
            UPDATE commands
            SET status = $status,
                processed_at_utc = $processedAtUtc,
                rejection_reason = $rejectionReason
            WHERE id = $commandId AND status = $expectedStatus;
            """;
        update.Parameters.AddWithValue("$status", status.ToString());
        Add(update, "$processedAtUtc", processedAtUtc);
        AddNullable(update, "$rejectionReason", rejectionReason);
        Add(update, "$commandId", commandId);
        update.Parameters.AddWithValue("$expectedStatus", CommandStatus.Received.ToString());
        if (await update.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) != 1)
        {
            throw new InvalidOperationException("命令状态发生并发变化。");
        }

        await InsertAuditAsync(
            connection,
            transaction,
            processedAtUtc,
            existing.SourceDeviceId,
            "CommandCompleted",
            "Command",
            commandId.ToString("D"),
            status switch
            {
                CommandStatus.Processed => AuditOutcome.Success,
                CommandStatus.Rejected => AuditOutcome.Rejected,
                _ => AuditOutcome.Failed
            },
            JsonSerializer.Serialize(new { status, rejectionReason }),
            cancellationToken).ConfigureAwait(false);
        transaction.Commit();
    }

    public async Task CreateTaskAsync(
        AgentTask task,
        Guid sourceCommandId,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(task);
        if (task.Status != TaskStatus.Pending
            || task.Phase != TaskPhase.Planning
            || task.Version != 0)
        {
            throw new InvalidOperationException("新任务必须以 Pending / Planning 状态和版本 0 创建。");
        }

        RequireText(task.Title, nameof(task.Title));
        RequireText(task.Instruction, nameof(task.Instruction));
        RequireText(task.Executor, nameof(task.Executor));

        await using var connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = connection.BeginTransaction();
        var project = await GetProjectInTransactionAsync(
            connection,
            transaction,
            task.ProjectId,
            cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidOperationException("任务关联的项目不存在。");
        if (project.AuthorizationState != ProjectAuthorizationState.Authorized)
        {
            throw new UnauthorizedAccessException("任务关联的项目未获得授权。");
        }

        _ = ProjectPathPolicy.ResolveWithinRoot(project.RootPath, task.WorkingDirectoryRelativePath);
        var sourceCommand = await GetCommandInTransactionAsync(
            connection,
            transaction,
            sourceCommandId,
            cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidOperationException("任务来源命令不存在。");
        ValidateCreateTaskCommand(task, sourceCommand);

        await using var insert = connection.CreateCommand();
        insert.Transaction = transaction;
        insert.CommandText = """
            INSERT INTO tasks(
                id, project_id, created_by_device_id, title, instruction,
                working_directory_relative_path, executor, status, phase,
                cancellation_requested_at_utc, created_at_utc, updated_at_utc,
                started_at_utc, completed_at_utc, last_heartbeat_at_utc,
                failure_code, failure_message, version)
            VALUES(
                $id, $projectId, $createdByDeviceId, $title, $instruction,
                $workingDirectoryRelativePath, $executor, $status, $phase,
                $cancellationRequestedAtUtc, $createdAtUtc, $updatedAtUtc,
                $startedAtUtc, $completedAtUtc, $lastHeartbeatAtUtc,
                $failureCode, $failureMessage, $version);
            """;
        BindTask(insert, task);
        await insert.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);

        var initialScopeId = Guid.NewGuid();
        await using (var insertScope = connection.CreateCommand())
        {
            insertScope.Transaction = transaction;
            insertScope.CommandText = """
                INSERT INTO resource_scopes(
                    id, task_id, scope_type, resource_id, scope_value, access_mode,
                    granted_by_device_id, granted_at_utc, expires_at_utc, revoked_at_utc)
                VALUES(
                    $id, $taskId, $scopeType, $resourceId, $scopeValue, $accessMode,
                    $grantedByDeviceId, $grantedAtUtc, NULL, NULL);
                """;
            Add(insertScope, "$id", initialScopeId);
            Add(insertScope, "$taskId", task.Id);
            insertScope.Parameters.AddWithValue("$scopeType", ResourceScopeType.Project.ToString());
            Add(insertScope, "$resourceId", project.Id);
            insertScope.Parameters.AddWithValue(
                "$scopeValue",
                project.RootPath);
            insertScope.Parameters.AddWithValue("$accessMode", ResourceAccessMode.Execute.ToString());
            Add(insertScope, "$grantedByDeviceId", task.CreatedByDeviceId);
            Add(insertScope, "$grantedAtUtc", task.CreatedAtUtc);
            await insertScope.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        await using var updateCommand = connection.CreateCommand();
        updateCommand.Transaction = transaction;
        updateCommand.CommandText = """
            UPDATE commands
            SET task_id = $taskId,
                status = $status,
                processed_at_utc = $processedAtUtc
            WHERE id = $commandId AND status = $expectedStatus;
            """;
        Add(updateCommand, "$taskId", task.Id);
        updateCommand.Parameters.AddWithValue("$status", CommandStatus.Processed.ToString());
        Add(updateCommand, "$processedAtUtc", task.CreatedAtUtc);
        Add(updateCommand, "$commandId", sourceCommandId);
        updateCommand.Parameters.AddWithValue("$expectedStatus", CommandStatus.Received.ToString());
        if (await updateCommand.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) != 1)
        {
            throw new InvalidOperationException("任务来源命令已经处理，不能重复创建任务。");
        }

        await InsertTaskEventAsync(
            connection,
            transaction,
            new TaskEventRecord
            {
                Id = Guid.NewGuid(),
                TaskId = task.Id,
                SequenceNumber = 1,
                EventType = TaskEventType.Created,
                FromStatus = null,
                ToStatus = TaskStatus.Pending,
                FromPhase = null,
                ToPhase = TaskPhase.Planning,
                Source = TaskEventSource.User,
                SourceDeviceId = task.CreatedByDeviceId,
                CommandId = sourceCommandId,
                OccurredAtUtc = task.CreatedAtUtc,
                Message = "任务已创建。"
            },
            cancellationToken).ConfigureAwait(false);
        await InsertAuditAsync(
            connection,
            transaction,
            task.CreatedAtUtc,
            task.CreatedByDeviceId,
            "TaskCreated",
            "Task",
            task.Id.ToString("D"),
            AuditOutcome.Success,
            JsonSerializer.Serialize(new { task.ProjectId, sourceCommandId, initialScopeId }),
            cancellationToken).ConfigureAwait(false);
        transaction.Commit();
    }

    public async Task CreateAgentAttemptAsync(
        AgentRunRecord run,
        AgentAttemptRecord attempt,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(run);
        ArgumentNullException.ThrowIfNull(attempt);
        RequireText(run.ConnectorId, nameof(run.ConnectorId));
        RequireText(run.Transport, nameof(run.Transport));
        RequireText(attempt.InputHash, nameof(attempt.InputHash));
        if (attempt.AgentRunId != run.Id || attempt.TaskId != run.TaskId)
        {
            throw new ArgumentException("Agent Attempt 与 Agent Run 不匹配。", nameof(attempt));
        }

        await using var connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = connection.BeginTransaction();
        var task = await GetTaskInTransactionAsync(
            connection,
            transaction,
            run.TaskId,
            cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidOperationException("任务不存在。");
        var existing = await GetAgentRunInTransactionAsync(
            connection,
            transaction,
            run.TaskId,
            cancellationToken).ConfigureAwait(false);

        if (existing is null)
        {
            if (attempt.Operation != AgentAttemptOperation.Start || task.Status != TaskStatus.Pending)
            {
                throw new InvalidOperationException("首次 Agent Attempt 必须从 Pending 任务启动。");
            }

            await InsertAgentRunAsync(connection, transaction, run, cancellationToken)
                .ConfigureAwait(false);
        }
        else
        {
            if (existing.Id != run.Id
                || !string.Equals(existing.ConnectorId, run.ConnectorId, StringComparison.OrdinalIgnoreCase)
                || !string.Equals(existing.Transport, run.Transport, StringComparison.Ordinal))
            {
                throw new InvalidOperationException("任务已经关联到另一个 Agent Run。");
            }

            if (attempt.Operation != AgentAttemptOperation.Resume
                || task.Status is not (TaskStatus.WaitingForUser or TaskStatus.Interrupted))
            {
                throw new InvalidOperationException("只有 WaitingForUser 或 Interrupted 任务可以续接 Agent Thread。");
            }

            await UpdateAgentRunForNewAttemptAsync(
                connection,
                transaction,
                existing.Id,
                attempt.StartedAtUtc,
                cancellationToken).ConfigureAwait(false);
        }

        var expectedAttemptNumber = await GetNextAttemptNumberAsync(
            connection,
            transaction,
            run.Id,
            cancellationToken).ConfigureAwait(false);
        if (attempt.AttemptNumber != expectedAttemptNumber)
        {
            throw new InvalidOperationException(
                $"Agent Attempt 序号必须为 {expectedAttemptNumber}。");
        }

        await InsertAgentAttemptAsync(connection, transaction, attempt, cancellationToken)
            .ConfigureAwait(false);
        if (attempt.Operation == AgentAttemptOperation.Resume)
        {
            await MarkPendingDecisionAnsweredAsync(
                connection,
                transaction,
                attempt.TaskId,
                attempt.CommandId,
                attempt.StartedAtUtc,
                cancellationToken).ConfigureAwait(false);
        }

        await InsertAuditAsync(
            connection,
            transaction,
            attempt.StartedAtUtc,
            null,
            "AgentAttemptCreated",
            "Task",
            attempt.TaskId.ToString("D"),
            AuditOutcome.Success,
            JsonSerializer.Serialize(new
            {
                agentRunId = run.Id,
                attemptId = attempt.Id,
                attempt.AttemptNumber,
                attempt.Operation,
                connectorId = run.ConnectorId
            }),
            cancellationToken).ConfigureAwait(false);
        transaction.Commit();
    }

    public async Task RecordAgentStartedAsync(
        Guid taskId,
        Guid agentRunId,
        Guid attemptId,
        string externalRunId,
        string connectorVersion,
        int processId,
        DateTimeOffset startedAtUtc,
        CancellationToken cancellationToken = default)
    {
        RequireText(externalRunId, nameof(externalRunId));
        RequireText(connectorVersion, nameof(connectorVersion));
        if (processId <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(processId));
        }

        await using var connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = connection.BeginTransaction();
        var task = await GetTaskInTransactionAsync(connection, transaction, taskId, cancellationToken)
            .ConfigureAwait(false)
            ?? throw new InvalidOperationException("任务不存在。");
        var run = await GetAgentRunByIdInTransactionAsync(
            connection,
            transaction,
            agentRunId,
            cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidOperationException("Agent Run 不存在。");
        var attempt = await GetAgentAttemptInTransactionAsync(
            connection,
            transaction,
            attemptId,
            cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidOperationException("Agent Attempt 不存在。");
        if (run.TaskId != taskId || attempt.AgentRunId != agentRunId || attempt.TaskId != taskId)
        {
            throw new InvalidOperationException("Agent 启动记录与任务不匹配。");
        }

        if (run.ExternalRunId is not null
            && !string.Equals(run.ExternalRunId, externalRunId.Trim(), StringComparison.Ordinal))
        {
            throw new InvalidOperationException("续接返回了不同的 ExternalRunId。");
        }

        await using (var updateRun = connection.CreateCommand())
        {
            updateRun.Transaction = transaction;
            updateRun.CommandText = """
                UPDATE agent_runs
                SET external_run_id = $externalRunId,
                    connector_version = $connectorVersion,
                    status = $status,
                    updated_at_utc = $updatedAtUtc
                WHERE id = $id;
                """;
            Add(updateRun, "$id", agentRunId);
            updateRun.Parameters.AddWithValue("$externalRunId", externalRunId.Trim());
            updateRun.Parameters.AddWithValue("$connectorVersion", connectorVersion.Trim());
            updateRun.Parameters.AddWithValue("$status", AgentRunStatus.Running.ToString());
            Add(updateRun, "$updatedAtUtc", startedAtUtc);
            await updateRun.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        await using (var updateAttempt = connection.CreateCommand())
        {
            updateAttempt.Transaction = transaction;
            updateAttempt.CommandText = """
                UPDATE agent_attempts
                SET status = $status,
                    process_id = $processId,
                    process_started_at_utc = $processStartedAtUtc
                WHERE id = $id;
                """;
            Add(updateAttempt, "$id", attemptId);
            updateAttempt.Parameters.AddWithValue("$status", AgentAttemptStatus.Running.ToString());
            updateAttempt.Parameters.AddWithValue("$processId", processId);
            Add(updateAttempt, "$processStartedAtUtc", startedAtUtc);
            await updateAttempt.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        if (task.Status != TaskStatus.Running)
        {
            TaskStateMachine.EnsureTransition(task.Status, TaskStatus.Running);
            var updatedTask = task with
            {
                Status = TaskStatus.Running,
                UpdatedAtUtc = startedAtUtc,
                StartedAtUtc = task.StartedAtUtc ?? startedAtUtc,
                CompletedAtUtc = null,
                Version = task.Version + 1
            };
            await UpdateTaskAsync(connection, transaction, task.Version, updatedTask, cancellationToken)
                .ConfigureAwait(false);
            await InsertTaskEventAsync(
                connection,
                transaction,
                new TaskEventRecord
                {
                    Id = Guid.NewGuid(),
                    TaskId = taskId,
                    SequenceNumber = await GetNextSequenceAsync(
                        connection,
                        transaction,
                        taskId,
                        cancellationToken).ConfigureAwait(false),
                    EventType = TaskEventType.StateChanged,
                    FromStatus = task.Status,
                    ToStatus = TaskStatus.Running,
                    Source = TaskEventSource.Agent,
                    OccurredAtUtc = startedAtUtc,
                    Message = "Codex Turn 已启动。",
                    DataJson = JsonSerializer.Serialize(new
                    {
                        agentRunId,
                        attemptId,
                        externalRunId = externalRunId.Trim()
                    })
                },
                cancellationToken).ConfigureAwait(false);
        }

        await InsertAuditAsync(
            connection,
            transaction,
            startedAtUtc,
            null,
            "AgentStarted",
            "Task",
            taskId.ToString("D"),
            AuditOutcome.Success,
            JsonSerializer.Serialize(new { agentRunId, attemptId, processId }),
            cancellationToken).ConfigureAwait(false);
        transaction.Commit();
    }

    public async Task RecordAgentCancellationRequestedAsync(
        Guid taskId,
        Guid attemptId,
        DateTimeOffset requestedAtUtc,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = connection.BeginTransaction();
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            UPDATE agent_attempts
            SET cancellation_requested_at_utc = COALESCE(cancellation_requested_at_utc, $requestedAtUtc)
            WHERE id = $attemptId AND task_id = $taskId;
            """;
        Add(command, "$attemptId", attemptId);
        Add(command, "$taskId", taskId);
        Add(command, "$requestedAtUtc", requestedAtUtc);
        if (await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) != 1)
        {
            throw new InvalidOperationException("找不到要取消的 Agent Attempt。");
        }

        await InsertAuditAsync(
            connection,
            transaction,
            requestedAtUtc,
            null,
            "AgentCancellationRequested",
            "Task",
            taskId.ToString("D"),
            AuditOutcome.Success,
            JsonSerializer.Serialize(new { attemptId }),
            cancellationToken).ConfigureAwait(false);
        transaction.Commit();
    }

    public async Task<AgentEventApplyResult> ApplyAgentEventAsync(
        AgentEventApplyRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        RequireText(request.ExternalEventId, nameof(request.ExternalEventId));
        RequireText(request.EventKind, nameof(request.EventKind));
        RequireText(request.Message, nameof(request.Message));
        if (request.DataJson is not null)
        {
            ValidateJson(request.DataJson, nameof(request.DataJson));
        }

        if (request.FinalResultJson is not null)
        {
            ValidateJson(request.FinalResultJson, nameof(request.FinalResultJson));
        }

        await using var connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = connection.BeginTransaction();
        var task = await GetTaskInTransactionAsync(
            connection,
            transaction,
            request.TaskId,
            cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidOperationException("任务不存在。");
        var run = await GetAgentRunByIdInTransactionAsync(
            connection,
            transaction,
            request.AgentRunId,
            cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidOperationException("Agent Run 不存在。");
        var attempt = await GetAgentAttemptInTransactionAsync(
            connection,
            transaction,
            request.AttemptId,
            cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidOperationException("Agent Attempt 不存在。");
        if (run.TaskId != task.Id || attempt.TaskId != task.Id || attempt.AgentRunId != run.Id)
        {
            throw new InvalidOperationException("Agent 事件与任务关系不一致。");
        }

        await using (var insertEvent = connection.CreateCommand())
        {
            insertEvent.Transaction = transaction;
            insertEvent.CommandText = """
                INSERT OR IGNORE INTO agent_connector_events(
                    id, agent_run_id, attempt_id, task_id, sequence_number,
                    external_event_id, event_kind, run_status, occurred_at_utc,
                    message, data_json)
                VALUES(
                    $id, $agentRunId, $attemptId, $taskId, $sequenceNumber,
                    $externalEventId, $eventKind, $runStatus, $occurredAtUtc,
                    $message, $dataJson);
                """;
            Add(insertEvent, "$id", Guid.NewGuid());
            Add(insertEvent, "$agentRunId", request.AgentRunId);
            Add(insertEvent, "$attemptId", request.AttemptId);
            Add(insertEvent, "$taskId", request.TaskId);
            insertEvent.Parameters.AddWithValue("$sequenceNumber", request.SequenceNumber);
            insertEvent.Parameters.AddWithValue("$externalEventId", request.ExternalEventId.Trim());
            insertEvent.Parameters.AddWithValue("$eventKind", request.EventKind.Trim());
            insertEvent.Parameters.AddWithValue("$runStatus", request.RunStatus.ToString());
            Add(insertEvent, "$occurredAtUtc", request.OccurredAtUtc);
            insertEvent.Parameters.AddWithValue("$message", request.Message.Trim());
            AddNullable(insertEvent, "$dataJson", request.DataJson);
            if (await insertEvent.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) == 0)
            {
                transaction.Commit();
                return new AgentEventApplyResult(false, task);
            }
        }

        var requestedTaskStatus = request.TaskStatus;
        var taskChanged = requestedTaskStatus.HasValue
            && requestedTaskStatus.Value != task.Status;
        var updatedTask = task;
        if (taskChanged)
        {
            var nextTaskStatus = requestedTaskStatus!.Value;
            TaskStateMachine.EnsureTransition(task.Status, nextTaskStatus);
            updatedTask = task with
            {
                Status = nextTaskStatus,
                UpdatedAtUtc = request.OccurredAtUtc,
                StartedAtUtc = nextTaskStatus == TaskStatus.Running
                    ? task.StartedAtUtc ?? request.OccurredAtUtc
                    : task.StartedAtUtc,
                CompletedAtUtc = TaskStateMachine.IsTerminal(nextTaskStatus)
                    ? request.OccurredAtUtc
                    : null,
                FailureCode = nextTaskStatus == TaskStatus.Failed
                    ? request.FailureCode
                    : task.FailureCode,
                FailureMessage = nextTaskStatus == TaskStatus.Failed
                    ? request.FailureMessage
                    : task.FailureMessage,
                Version = task.Version + 1
            };
            await UpdateTaskAsync(connection, transaction, task.Version, updatedTask, cancellationToken)
                .ConfigureAwait(false);
        }

        await using (var updateRun = connection.CreateCommand())
        {
            updateRun.Transaction = transaction;
            updateRun.CommandText = """
                UPDATE agent_runs SET
                    status = $status,
                    updated_at_utc = $updatedAtUtc,
                    last_event_sequence = MAX(last_event_sequence, $lastEventSequence),
                    last_event_type = $lastEventType,
                    last_event_at_utc = $lastEventAtUtc,
                    final_summary = COALESCE($finalSummary, final_summary),
                    final_result_json = COALESCE($finalResultJson, final_result_json),
                    failure_code = COALESCE($failureCode, failure_code),
                    failure_message = COALESCE($failureMessage, failure_message)
                WHERE id = $id;
                """;
            Add(updateRun, "$id", request.AgentRunId);
            updateRun.Parameters.AddWithValue("$status", request.RunStatus.ToString());
            Add(updateRun, "$updatedAtUtc", request.OccurredAtUtc);
            updateRun.Parameters.AddWithValue("$lastEventSequence", request.SequenceNumber);
            updateRun.Parameters.AddWithValue("$lastEventType", request.EventKind.Trim());
            Add(updateRun, "$lastEventAtUtc", request.OccurredAtUtc);
            AddNullable(updateRun, "$finalSummary", request.FinalSummary);
            AddNullable(updateRun, "$finalResultJson", request.FinalResultJson);
            AddNullable(updateRun, "$failureCode", request.FailureCode);
            AddNullable(updateRun, "$failureMessage", request.FailureMessage);
            await updateRun.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        await using (var updateAttempt = connection.CreateCommand())
        {
            updateAttempt.Transaction = transaction;
            var terminal = request.AttemptStatus is AgentAttemptStatus.Completed
                or AgentAttemptStatus.Failed
                or AgentAttemptStatus.Cancelled
                or AgentAttemptStatus.Interrupted;
            updateAttempt.CommandText = """
                UPDATE agent_attempts SET
                    status = $status,
                    terminal_event_at_utc = CASE WHEN $terminal THEN $terminalAtUtc ELSE terminal_event_at_utc END,
                    terminal_event_type = CASE WHEN $terminal THEN $terminalEventType ELSE terminal_event_type END,
                    exit_code = COALESCE($exitCode, exit_code),
                    cancellation_confirmed_at_utc = CASE
                        WHEN $cancelled THEN $terminalAtUtc
                        ELSE cancellation_confirmed_at_utc
                    END,
                    last_event_sequence = MAX(last_event_sequence, $lastEventSequence),
                    last_event_at_utc = $lastEventAtUtc
                WHERE id = $id;
                """;
            Add(updateAttempt, "$id", request.AttemptId);
            updateAttempt.Parameters.AddWithValue("$status", request.AttemptStatus.ToString());
            updateAttempt.Parameters.AddWithValue("$terminal", terminal);
            Add(updateAttempt, "$terminalAtUtc", request.OccurredAtUtc);
            updateAttempt.Parameters.AddWithValue("$terminalEventType", request.EventKind.Trim());
            updateAttempt.Parameters.AddWithValue(
                "$cancelled",
                request.AttemptStatus == AgentAttemptStatus.Cancelled);
            AddNullable(updateAttempt, "$exitCode", request.ExitCode);
            updateAttempt.Parameters.AddWithValue("$lastEventSequence", request.SequenceNumber);
            Add(updateAttempt, "$lastEventAtUtc", request.OccurredAtUtc);
            await updateAttempt.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        if (request.DecisionRequest is { } decision)
        {
            await InsertDecisionRequestAsync(
                connection,
                transaction,
                decision,
                cancellationToken).ConfigureAwait(false);
        }

        var taskEventType = taskChanged ? TaskEventType.StateChanged : TaskEventType.AgentEvent;
        await InsertTaskEventAsync(
            connection,
            transaction,
            new TaskEventRecord
            {
                Id = Guid.NewGuid(),
                TaskId = task.Id,
                SequenceNumber = await GetNextSequenceAsync(
                    connection,
                    transaction,
                    task.Id,
                    cancellationToken).ConfigureAwait(false),
                EventType = taskEventType,
                FromStatus = taskChanged ? task.Status : null,
                ToStatus = taskChanged ? updatedTask.Status : null,
                Source = TaskEventSource.Agent,
                OccurredAtUtc = request.OccurredAtUtc,
                Message = request.Message.Trim(),
                DataJson = request.DataJson
            },
            cancellationToken).ConfigureAwait(false);

        if (taskChanged || request.EventKind is "DecisionRequested" or "Completed" or "Failed" or "Cancelled" or "Interrupted")
        {
            await InsertAuditAsync(
                connection,
                transaction,
                request.OccurredAtUtc,
                null,
                $"Agent{request.EventKind.Trim()}",
                "Task",
                task.Id.ToString("D"),
                request.EventKind == "Failed" ? AuditOutcome.Failed : AuditOutcome.Success,
                JsonSerializer.Serialize(new
                {
                    request.AgentRunId,
                    request.AttemptId,
                    request.SequenceNumber,
                    taskStatus = request.TaskStatus
                }),
                cancellationToken).ConfigureAwait(false);
        }

        transaction.Commit();
        return new AgentEventApplyResult(true, updatedTask);
    }

    public async Task<AgentTask> TransitionTaskAsync(
        Guid taskId,
        TaskStatus newStatus,
        TaskEventSource source,
        string message,
        Guid? sourceDeviceId = null,
        Guid? commandId = null,
        string? dataJson = null,
        string? failureCode = null,
        string? failureMessage = null,
        CancellationToken cancellationToken = default)
    {
        RequireText(message, nameof(message));
        if (dataJson is not null)
        {
            ValidateJson(dataJson, nameof(dataJson));
        }

        await using var connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = connection.BeginTransaction();
        var current = await GetTaskInTransactionAsync(
            connection,
            transaction,
            taskId,
            cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidOperationException("任务不存在。");
        TaskStateMachine.EnsureTransition(current.Status, newStatus);

        var now = DateTimeOffset.UtcNow;
        var updated = current with
        {
            Status = newStatus,
            UpdatedAtUtc = now,
            StartedAtUtc = newStatus == TaskStatus.Running
                ? current.StartedAtUtc ?? now
                : current.StartedAtUtc,
            CompletedAtUtc = TaskStateMachine.IsTerminal(newStatus) ? now : null,
            FailureCode = newStatus == TaskStatus.Failed ? failureCode : current.FailureCode,
            FailureMessage = newStatus == TaskStatus.Failed ? failureMessage : current.FailureMessage,
            Version = current.Version + 1
        };
        await UpdateTaskAsync(connection, transaction, current.Version, updated, cancellationToken)
            .ConfigureAwait(false);
        var sequence = await GetNextSequenceAsync(connection, transaction, taskId, cancellationToken)
            .ConfigureAwait(false);
        await InsertTaskEventAsync(
            connection,
            transaction,
            new TaskEventRecord
            {
                Id = Guid.NewGuid(),
                TaskId = taskId,
                SequenceNumber = sequence,
                EventType = TaskEventType.StateChanged,
                FromStatus = current.Status,
                ToStatus = newStatus,
                Source = source,
                SourceDeviceId = sourceDeviceId,
                CommandId = commandId,
                OccurredAtUtc = now,
                Message = message.Trim(),
                DataJson = dataJson
            },
            cancellationToken).ConfigureAwait(false);
        await InsertAuditAsync(
            connection,
            transaction,
            now,
            sourceDeviceId,
            "TaskStateChanged",
            "Task",
            taskId.ToString("D"),
            AuditOutcome.Success,
            JsonSerializer.Serialize(new { from = current.Status, to = newStatus }),
            cancellationToken).ConfigureAwait(false);
        transaction.Commit();
        return updated;
    }

    public async Task<AgentTask> TransitionTaskPhaseAsync(
        Guid taskId,
        TaskPhase newPhase,
        TaskEventSource source,
        string message,
        Guid? sourceDeviceId = null,
        Guid? commandId = null,
        string? dataJson = null,
        CancellationToken cancellationToken = default)
    {
        RequireText(message, nameof(message));
        if (dataJson is not null)
        {
            ValidateJson(dataJson, nameof(dataJson));
        }

        await using var connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = connection.BeginTransaction();
        var current = await GetTaskInTransactionAsync(
            connection,
            transaction,
            taskId,
            cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidOperationException("任务不存在。");
        TaskPhaseStateMachine.EnsureTransition(current.Phase, newPhase);

        var now = DateTimeOffset.UtcNow;
        var updated = current with
        {
            Phase = newPhase,
            UpdatedAtUtc = now,
            Version = current.Version + 1
        };
        await UpdateTaskAsync(connection, transaction, current.Version, updated, cancellationToken)
            .ConfigureAwait(false);
        var sequence = await GetNextSequenceAsync(connection, transaction, taskId, cancellationToken)
            .ConfigureAwait(false);
        await InsertTaskEventAsync(
            connection,
            transaction,
            new TaskEventRecord
            {
                Id = Guid.NewGuid(),
                TaskId = taskId,
                SequenceNumber = sequence,
                EventType = TaskEventType.PhaseChanged,
                FromPhase = current.Phase,
                ToPhase = newPhase,
                Source = source,
                SourceDeviceId = sourceDeviceId,
                CommandId = commandId,
                OccurredAtUtc = now,
                Message = message.Trim(),
                DataJson = dataJson
            },
            cancellationToken).ConfigureAwait(false);
        await InsertAuditAsync(
            connection,
            transaction,
            now,
            sourceDeviceId,
            "TaskPhaseChanged",
            "Task",
            taskId.ToString("D"),
            AuditOutcome.Success,
            JsonSerializer.Serialize(new { from = current.Phase, to = newPhase }),
            cancellationToken).ConfigureAwait(false);
        transaction.Commit();
        return updated;
    }

    public async Task<bool> RequestCancellationAsync(
        Guid taskId,
        Guid sourceDeviceId,
        Guid? commandId = null,
        CancellationToken cancellationToken = default,
        long? expectedVersion = null)
    {
        await using var connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = connection.BeginTransaction();
        var current = await GetTaskInTransactionAsync(
            connection,
            transaction,
            taskId,
            cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidOperationException("任务不存在。");
        var now = DateTimeOffset.UtcNow;
        if (expectedVersion is { } expected && current.Version != expected)
            throw new InvalidOperationException("entity_version_mismatch");
        if (current.Status == TaskStatus.CancellationRequested || TaskStateMachine.IsTerminal(current.Status))
        {
            await InsertAuditAsync(
                connection,
                transaction,
                now,
                sourceDeviceId,
                "TaskCancellationRequested",
                "Task",
                taskId.ToString("D"),
                AuditOutcome.Rejected,
                JsonSerializer.Serialize(new { reason = "task_not_cancellable", current.Status }),
                cancellationToken).ConfigureAwait(false);
            transaction.Commit();
            return false;
        }

        TaskStateMachine.EnsureTransition(current.Status, TaskStatus.CancellationRequested);
        var updated = current with
        {
            Status = TaskStatus.CancellationRequested,
            CancellationRequestedAtUtc = now,
            UpdatedAtUtc = now,
            Version = current.Version + 1
        };
        await UpdateTaskAsync(connection, transaction, current.Version, updated, cancellationToken)
            .ConfigureAwait(false);
        var sequence = await GetNextSequenceAsync(connection, transaction, taskId, cancellationToken)
            .ConfigureAwait(false);
        await InsertTaskEventAsync(
            connection,
            transaction,
            new TaskEventRecord
            {
                Id = Guid.NewGuid(),
                TaskId = taskId,
                SequenceNumber = sequence,
                EventType = TaskEventType.CancellationRequested,
                FromStatus = current.Status,
                ToStatus = TaskStatus.CancellationRequested,
                Source = TaskEventSource.User,
                SourceDeviceId = sourceDeviceId,
                CommandId = commandId,
                OccurredAtUtc = now,
                Message = "用户请求取消任务。"
            },
            cancellationToken).ConfigureAwait(false);
        await InsertAuditAsync(
            connection,
            transaction,
            now,
            sourceDeviceId,
            "TaskCancellationRequested",
            "Task",
            taskId.ToString("D"),
            AuditOutcome.Success,
            null,
            cancellationToken).ConfigureAwait(false);
        transaction.Commit();
        return true;
    }

    public async Task<RecoveryResult> RecoverInterruptedTasksAsync(
        DateTimeOffset recoveredAtUtc,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = connection.BeginTransaction();
        var activeTasks = new List<AgentTask>();
        await using (var select = connection.CreateCommand())
        {
            select.Transaction = transaction;
            select.CommandText = """
                SELECT * FROM tasks
                WHERE status IN ($running, $waiting, $cancelling)
                ORDER BY created_at_utc;
                """;
            select.Parameters.AddWithValue("$running", TaskStatus.Running.ToString());
            select.Parameters.AddWithValue("$waiting", TaskStatus.WaitingForUser.ToString());
            select.Parameters.AddWithValue("$cancelling", TaskStatus.CancellationRequested.ToString());
            await using var reader = await select.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                activeTasks.Add(ReadTask(reader));
            }
        }

        foreach (var current in activeTasks)
        {
            TaskStateMachine.EnsureTransition(current.Status, TaskStatus.Interrupted);
            var updated = current with
            {
                Status = TaskStatus.Interrupted,
                Phase = TaskPhase.Verifying,
                UpdatedAtUtc = recoveredAtUtc,
                CompletedAtUtc = recoveredAtUtc,
                Version = current.Version + 1
            };
            await UpdateTaskAsync(connection, transaction, current.Version, updated, cancellationToken)
                .ConfigureAwait(false);
            var sequence = await GetNextSequenceAsync(connection, transaction, current.Id, cancellationToken)
                .ConfigureAwait(false);
            await InsertTaskEventAsync(
                connection,
                transaction,
                new TaskEventRecord
                {
                    Id = Guid.NewGuid(),
                    TaskId = current.Id,
                    SequenceNumber = sequence,
                    EventType = TaskEventType.RecoveryDetected,
                    FromStatus = current.Status,
                    ToStatus = TaskStatus.Interrupted,
                    FromPhase = current.Phase,
                    ToPhase = TaskPhase.Verifying,
                    Source = TaskEventSource.Recovery,
                    OccurredAtUtc = recoveredAtUtc,
                    Message = "程序启动时发现未正常结束的任务，已标记为 Interrupted。"
                },
                cancellationToken).ConfigureAwait(false);
            await InsertAuditAsync(
                connection,
                transaction,
                recoveredAtUtc,
                null,
                "TaskInterruptedDuringRecovery",
                "Task",
                current.Id.ToString("D"),
                AuditOutcome.Success,
                JsonSerializer.Serialize(new { previousStatus = current.Status }),
                cancellationToken).ConfigureAwait(false);

            await using (var updateRun = connection.CreateCommand())
            {
                updateRun.Transaction = transaction;
                updateRun.CommandText = """
                    UPDATE agent_runs
                    SET status = $status,
                        updated_at_utc = $updatedAtUtc,
                        failure_code = COALESCE(failure_code, $failureCode),
                        failure_message = COALESCE(failure_message, $failureMessage)
                    WHERE task_id = $taskId
                      AND status IN ($starting, $running, $waiting);
                    """;
                Add(updateRun, "$taskId", current.Id);
                updateRun.Parameters.AddWithValue("$status", AgentRunStatus.Interrupted.ToString());
                Add(updateRun, "$updatedAtUtc", recoveredAtUtc);
                updateRun.Parameters.AddWithValue("$failureCode", "host_restarted");
                updateRun.Parameters.AddWithValue(
                    "$failureMessage",
                    "Desktop Host 重启时 Agent 尚无权威终态。");
                updateRun.Parameters.AddWithValue("$starting", AgentRunStatus.Starting.ToString());
                updateRun.Parameters.AddWithValue("$running", AgentRunStatus.Running.ToString());
                updateRun.Parameters.AddWithValue("$waiting", AgentRunStatus.WaitingForUser.ToString());
                await updateRun.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            }

            await using (var updateAttempt = connection.CreateCommand())
            {
                updateAttempt.Transaction = transaction;
                updateAttempt.CommandText = """
                    UPDATE agent_attempts
                    SET status = $status,
                        terminal_event_at_utc = COALESCE(terminal_event_at_utc, $terminalAtUtc),
                        terminal_event_type = COALESCE(terminal_event_type, $terminalEventType)
                    WHERE task_id = $taskId
                      AND status IN ($starting, $running);
                    """;
                Add(updateAttempt, "$taskId", current.Id);
                updateAttempt.Parameters.AddWithValue("$status", AgentAttemptStatus.Interrupted.ToString());
                Add(updateAttempt, "$terminalAtUtc", recoveredAtUtc);
                updateAttempt.Parameters.AddWithValue("$terminalEventType", "HostRecovery");
                updateAttempt.Parameters.AddWithValue("$starting", AgentAttemptStatus.Starting.ToString());
                updateAttempt.Parameters.AddWithValue("$running", AgentAttemptStatus.Running.ToString());
                await updateAttempt.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            }

            await using (var updateInvocation = connection.CreateCommand())
            {
                updateInvocation.Transaction = transaction;
                updateInvocation.CommandText = """
                    UPDATE skill_invocations
                    SET status = $status,
                        completed_at_utc = COALESCE(completed_at_utc, $completedAtUtc),
                        failure_code = COALESCE(failure_code, $failureCode),
                        failure_message = COALESCE(failure_message, $failureMessage)
                    WHERE task_id = $taskId
                      AND status IN ($pending, $running, $waiting);
                    """;
                Add(updateInvocation, "$taskId", current.Id);
                updateInvocation.Parameters.AddWithValue(
                    "$status",
                    SkillInvocationStatus.Interrupted.ToString());
                Add(updateInvocation, "$completedAtUtc", recoveredAtUtc);
                updateInvocation.Parameters.AddWithValue("$failureCode", "host_restarted");
                updateInvocation.Parameters.AddWithValue(
                    "$failureMessage",
                    "Desktop Host 重启时 Skill Invocation 尚无权威终态。");
                updateInvocation.Parameters.AddWithValue(
                    "$pending",
                    SkillInvocationStatus.Pending.ToString());
                updateInvocation.Parameters.AddWithValue(
                    "$running",
                    SkillInvocationStatus.Running.ToString());
                updateInvocation.Parameters.AddWithValue(
                    "$waiting",
                    SkillInvocationStatus.WaitingForUser.ToString());
                await updateInvocation.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            }
        }

        transaction.Commit();
        return new RecoveryResult(activeTasks.Select(task => task.Id).ToArray());
    }

    public async Task<IReadOnlyList<TaskEventRecord>> GetTaskEventsAsync(
        Guid taskId,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT * FROM task_events WHERE task_id = $taskId ORDER BY sequence_number;";
        Add(command, "$taskId", taskId);
        var events = new List<TaskEventRecord>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            events.Add(ReadTaskEvent(reader));
        }

        return events;
    }

    public async Task<IReadOnlyList<AuditLogEntry>> GetAuditLogAsync(
        CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT * FROM audit_log ORDER BY occurred_at_utc, rowid;";
        var entries = new List<AuditLogEntry>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            entries.Add(ReadAudit(reader));
        }

        return entries;
    }

    public async Task AppendAuditAsync(
        AuditLogEntry entry,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(entry);
        RequireText(entry.Action, nameof(entry.Action));
        RequireText(entry.EntityType, nameof(entry.EntityType));
        RequireText(entry.EntityId, nameof(entry.EntityId));
        if (entry.DetailsJson is not null)
        {
            ValidateJson(entry.DetailsJson, nameof(entry.DetailsJson));
        }

        await using var connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = connection.BeginTransaction();
        await InsertAuditAsync(
            connection,
            transaction,
            entry.OccurredAtUtc,
            entry.ActorDeviceId,
            entry.Action.Trim(),
            entry.EntityType.Trim(),
            entry.EntityId.Trim(),
            entry.Outcome,
            entry.DetailsJson,
            cancellationToken,
            entry.Id).ConfigureAwait(false);
        transaction.Commit();
    }

    public async Task<int> DeleteTerminalTaskHistoryAsync(
        DateTimeOffset deletedAtUtc,
        Guid actorDeviceId,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = connection.BeginTransaction();
        await using var count = connection.CreateCommand();
        count.Transaction = transaction;
        count.CommandText = """
            SELECT COUNT(*) FROM tasks
            WHERE status IN ($succeeded, $failed, $cancelled, $interrupted);
            """;
        count.Parameters.AddWithValue("$succeeded", TaskStatus.Succeeded.ToString());
        count.Parameters.AddWithValue("$failed", TaskStatus.Failed.ToString());
        count.Parameters.AddWithValue("$cancelled", TaskStatus.Cancelled.ToString());
        count.Parameters.AddWithValue("$interrupted", TaskStatus.Interrupted.ToString());
        var deletedCount = Convert.ToInt32(
            await count.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false),
            CultureInfo.InvariantCulture);
        if (deletedCount == 0)
        {
            transaction.Commit();
            return 0;
        }

        await using (var detachCommands = connection.CreateCommand())
        {
            detachCommands.Transaction = transaction;
            detachCommands.CommandText = """
                UPDATE commands SET task_id = NULL
                WHERE task_id IN (
                    SELECT id FROM tasks
                    WHERE status IN ($succeeded, $failed, $cancelled, $interrupted));
                """;
            detachCommands.Parameters.AddWithValue("$succeeded", TaskStatus.Succeeded.ToString());
            detachCommands.Parameters.AddWithValue("$failed", TaskStatus.Failed.ToString());
            detachCommands.Parameters.AddWithValue("$cancelled", TaskStatus.Cancelled.ToString());
            detachCommands.Parameters.AddWithValue("$interrupted", TaskStatus.Interrupted.ToString());
            await detachCommands.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        await using (var delete = connection.CreateCommand())
        {
            delete.Transaction = transaction;
            delete.CommandText = """
                DELETE FROM tasks
                WHERE status IN ($succeeded, $failed, $cancelled, $interrupted);
                """;
            delete.Parameters.AddWithValue("$succeeded", TaskStatus.Succeeded.ToString());
            delete.Parameters.AddWithValue("$failed", TaskStatus.Failed.ToString());
            delete.Parameters.AddWithValue("$cancelled", TaskStatus.Cancelled.ToString());
            delete.Parameters.AddWithValue("$interrupted", TaskStatus.Interrupted.ToString());
            await delete.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        await InsertAuditAsync(
            connection,
            transaction,
            deletedAtUtc,
            actorDeviceId,
            "TaskHistoryDeleted",
            "TaskHistory",
            "terminal",
            AuditOutcome.Success,
            JsonSerializer.Serialize(new { deletedCount }),
            cancellationToken).ConfigureAwait(false);
        transaction.Commit();
        return deletedCount;
    }

    public async Task UpsertTaskEvidenceAsync(
        TaskEvidence evidence,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(evidence);
        RequireText(evidence.UserSummary, nameof(evidence.UserSummary));
        var json = JsonSerializer.Serialize(evidence, EvidenceJsonOptions);
        ValidateJson(json, nameof(evidence));
        await using var connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = connection.BeginTransaction();
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO task_evidence(
                id, task_id, generated_at_utc, verification_status, user_summary, evidence_json)
            VALUES(
                $id, $taskId, $generatedAtUtc, $verificationStatus, $userSummary, $evidenceJson)
            ON CONFLICT(task_id) DO NOTHING;
            """;
        Add(command, "$id", evidence.Id);
        Add(command, "$taskId", evidence.TaskId);
        Add(command, "$generatedAtUtc", evidence.GeneratedAtUtc);
        command.Parameters.AddWithValue("$verificationStatus", evidence.VerificationStatus.ToString());
        command.Parameters.AddWithValue("$userSummary", evidence.UserSummary);
        command.Parameters.AddWithValue("$evidenceJson", json);
        var inserted = await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        if (inserted > 0)
        {
            await InsertAuditAsync(
                    connection,
                    transaction,
                    evidence.GeneratedAtUtc,
                    null,
                    "TaskEvidenceRecorded",
                    "Task",
                    evidence.TaskId.ToString("D"),
                    AuditOutcome.Success,
                    JsonSerializer.Serialize(new
                    {
                        verificationStatus = evidence.VerificationStatus.ToString(),
                        changedFileCount = evidence.Git.ChangedFiles.Count,
                        testStatus = evidence.Tests.Status.ToString()
                    }),
                    cancellationToken)
                .ConfigureAwait(false);
        }

        transaction.Commit();
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    private Task<SqliteConnection> OpenConnectionAsync(CancellationToken cancellationToken) =>
        SqliteConnectionOpener.OpenAsync(_connectionString, cancellationToken);

    private async Task<T?> QuerySingleAsync<T>(
        string sql,
        Guid id,
        Func<SqliteDataReader, T> map,
        CancellationToken cancellationToken)
        where T : class
    {
        await using var connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        Add(command, "$id", id);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        return await reader.ReadAsync(cancellationToken).ConfigureAwait(false) ? map(reader) : null;
    }

    private async Task<string> CreateMigrationBackupAsync(
        SqliteConnection source,
        int storedVersion,
        CancellationToken cancellationToken)
    {
        var directory = Path.GetDirectoryName(_databasePath)
            ?? throw new InvalidOperationException("数据库目录无效。");
        var fileName = Path.GetFileNameWithoutExtension(_databasePath);
        var backupPath = Path.Combine(
            directory,
            $"{fileName}.pre-v{V02Contract.SchemaVersion}-from-v{storedVersion}-{DateTimeOffset.UtcNow:yyyyMMddHHmmssfff}.backup.db");
        var backupConnectionString = new SqliteConnectionStringBuilder
        {
            DataSource = backupPath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Pooling = false
        }.ToString();
        await using var destination = new SqliteConnection(backupConnectionString);
        await destination.OpenAsync(cancellationToken).ConfigureAwait(false);
        source.BackupDatabase(destination);
        return backupPath;
    }

    private static async Task ApplyMigrationAsync(
        SqliteConnection connection,
        int version,
        string sql,
        CancellationToken cancellationToken)
    {
        await using var transaction = connection.BeginTransaction();
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        await RecordSchemaVersionAsync(connection, transaction, version, cancellationToken)
            .ConfigureAwait(false);
        transaction.Commit();
    }

    private static async Task ValidateVersion8ForVersion9MigrationAsync(
        SqliteConnection connection,
        CancellationToken cancellationToken)
    {
        var requiredTurnColumns = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "frozen_route_status",
            "frozen_provider_id",
            "frozen_model_id",
            "frozen_data_destination",
            "frozen_sends_data_off_device",
            "frozen_at_utc",
            "frozen_route_failure_code"
        };
        await using (var command = connection.CreateCommand())
        {
            command.CommandText = "SELECT name FROM pragma_table_info('session_turns');";
            await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                requiredTurnColumns.Remove(reader.GetString(0));
            }
        }

        if (requiredTurnColumns.Count != 0)
        {
            throw new InvalidOperationException(
                $"schema v8 不完整，缺少列：{string.Join(", ", requiredTurnColumns.Order())}。");
        }

        await using var objectCommand = connection.CreateCommand();
        objectCommand.CommandText = """
            SELECT COUNT(*) FROM sqlite_master
            WHERE (type = 'table' AND name = 'ai_invocations')
               OR (type = 'index' AND name IN (
                    'ix_ai_invocations_conversation_turn',
                    'ix_ai_invocations_session_turn',
                    'ix_ai_invocations_provider_model'));
            """;
        if (Convert.ToInt32(
                await objectCommand.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false),
                CultureInfo.InvariantCulture) != 4)
        {
            throw new InvalidOperationException("schema v8 不完整，AI Invocation 结构缺失。");
        }
    }

    private static async Task ValidateVersion9ForVersion10MigrationAsync(
        SqliteConnection connection,
        CancellationToken cancellationToken)
    {
        var requiredMemoryColumns = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "id",
            "category",
            "scope_kind",
            "project_id",
            "protected_title",
            "protected_body",
            "status",
            "version"
        };
        await using (var command = connection.CreateCommand())
        {
            command.CommandText = "SELECT name FROM pragma_table_info('memory_items');";
            await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                requiredMemoryColumns.Remove(reader.GetString(0));
            }
        }

        if (requiredMemoryColumns.Count != 0)
        {
            throw new InvalidOperationException(
                $"schema v9 不完整，缺少记忆列：{string.Join(", ", requiredMemoryColumns.Order())}。");
        }

        await using var objectCommand = connection.CreateCommand();
        objectCommand.CommandText = """
            SELECT COUNT(*) FROM sqlite_master
            WHERE (type = 'table' AND name = 'memory_items')
               OR (type = 'index' AND name IN (
                    'ix_memory_items_status_expiry',
                    'ix_memory_items_project'));
            """;
        if (Convert.ToInt32(
                await objectCommand.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false),
                CultureInfo.InvariantCulture) != 3)
        {
            throw new InvalidOperationException("schema v9 不完整，长期记忆结构缺失。");
        }
    }

    private static async Task ValidateVersion10Async(
        SqliteConnection connection,
        CancellationToken cancellationToken)
    {
        var required = new Dictionary<string, string[]>(StringComparer.Ordinal)
        {
            ["session_turns"] =
            [
                "memory_outbound_state", "memory_consent_id", "memory_origin_destination",
                "memory_prompt_hash", "memory_item_refs_json", "memory_manifest_hash"
            ],
            ["conversation_turns"] =
            [
                "memory_derived", "memory_consent_id", "memory_origin_destination",
                "memory_item_refs_json", "memory_manifest_hash"
            ],
            ["ai_invocations"] =
            [
                "memory_consent_id", "memory_origin_destination",
                "memory_item_refs_json", "memory_manifest_hash"
            ]
        };

        foreach (var (table, expectedColumns) in required)
        {
            var missing = expectedColumns.ToHashSet(StringComparer.OrdinalIgnoreCase);
            await using var command = connection.CreateCommand();
            command.CommandText = $"SELECT name FROM pragma_table_info('{table}');";
            await using var reader = await command.ExecuteReaderAsync(cancellationToken)
                .ConfigureAwait(false);
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                missing.Remove(reader.GetString(0));
            }

            if (missing.Count != 0)
            {
                throw new InvalidOperationException(
                    $"schema v10 不完整，{table} 缺少列：{string.Join(", ", missing.Order())}。");
            }
        }
    }

    private static async Task ValidateVersion11Async(
        SqliteConnection connection,
        CancellationToken cancellationToken)
    {
        await ValidateVersion10Async(connection, cancellationToken).ConfigureAwait(false);
        var missing = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "window_process_id",
            "window_process_started_at_utc"
        };
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT name FROM pragma_table_info('session_turns');";
        await using var reader = await command.ExecuteReaderAsync(cancellationToken)
            .ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            missing.Remove(reader.GetString(0));
        }

        if (missing.Count != 0)
        {
            throw new InvalidOperationException(
                $"schema v11 不完整，session_turns 缺少列：{string.Join(", ", missing.Order())}。");
        }
    }

    private static async Task RecordSchemaVersionAsync(
        SqliteConnection connection,
        SqliteTransaction? transaction,
        int version,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT OR IGNORE INTO schema_info(version, applied_at_utc)
            VALUES ($version, $appliedAtUtc);
            """;
        command.Parameters.AddWithValue("$version", version);
        command.Parameters.AddWithValue("$appliedAtUtc", ToDb(DateTimeOffset.UtcNow));
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async Task<AgentRunRecord?> GetAgentRunInTransactionAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        Guid taskId,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT * FROM agent_runs WHERE task_id = $taskId;";
        Add(command, "$taskId", taskId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        return await reader.ReadAsync(cancellationToken).ConfigureAwait(false)
            ? ReadAgentRun(reader)
            : null;
    }

    private static async Task<AgentRunRecord?> GetAgentRunByIdInTransactionAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        Guid agentRunId,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT * FROM agent_runs WHERE id = $id;";
        Add(command, "$id", agentRunId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        return await reader.ReadAsync(cancellationToken).ConfigureAwait(false)
            ? ReadAgentRun(reader)
            : null;
    }

    private static async Task<AgentAttemptRecord?> GetAgentAttemptInTransactionAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        Guid attemptId,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT * FROM agent_attempts WHERE id = $id;";
        Add(command, "$id", attemptId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        return await reader.ReadAsync(cancellationToken).ConfigureAwait(false)
            ? ReadAgentAttempt(reader)
            : null;
    }

    private static async Task InsertAgentRunAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        AgentRunRecord run,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO agent_runs(
                id, task_id, connector_id, transport, external_run_id,
                connector_version, status, created_at_utc, updated_at_utc,
                last_event_sequence, last_event_type, last_event_at_utc,
                final_summary, final_result_json, failure_code, failure_message)
            VALUES(
                $id, $taskId, $connectorId, $transport, $externalRunId,
                $connectorVersion, $status, $createdAtUtc, $updatedAtUtc,
                $lastEventSequence, $lastEventType, $lastEventAtUtc,
                $finalSummary, $finalResultJson, $failureCode, $failureMessage);
            """;
        Add(command, "$id", run.Id);
        Add(command, "$taskId", run.TaskId);
        command.Parameters.AddWithValue("$connectorId", run.ConnectorId.Trim());
        command.Parameters.AddWithValue("$transport", run.Transport.Trim());
        AddNullable(command, "$externalRunId", run.ExternalRunId);
        AddNullable(command, "$connectorVersion", run.ConnectorVersion);
        command.Parameters.AddWithValue("$status", run.Status.ToString());
        Add(command, "$createdAtUtc", run.CreatedAtUtc);
        Add(command, "$updatedAtUtc", run.UpdatedAtUtc);
        command.Parameters.AddWithValue("$lastEventSequence", run.LastEventSequence);
        AddNullable(command, "$lastEventType", run.LastEventType);
        AddNullable(command, "$lastEventAtUtc", run.LastEventAtUtc);
        AddNullable(command, "$finalSummary", run.FinalSummary);
        AddNullable(command, "$finalResultJson", run.FinalResultJson);
        AddNullable(command, "$failureCode", run.FailureCode);
        AddNullable(command, "$failureMessage", run.FailureMessage);
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async Task InsertAgentAttemptAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        AgentAttemptRecord attempt,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO agent_attempts(
                id, agent_run_id, task_id, command_id, attempt_number,
                operation, status, process_id, process_started_at_utc,
                started_at_utc, terminal_event_at_utc, process_exited_at_utc,
                terminal_event_type, exit_code, cancellation_requested_at_utc,
                cancellation_confirmed_at_utc, last_event_sequence,
                last_event_at_utc, input_hash)
            VALUES(
                $id, $agentRunId, $taskId, $commandId, $attemptNumber,
                $operation, $status, $processId, $processStartedAtUtc,
                $startedAtUtc, $terminalEventAtUtc, $processExitedAtUtc,
                $terminalEventType, $exitCode, $cancellationRequestedAtUtc,
                $cancellationConfirmedAtUtc, $lastEventSequence,
                $lastEventAtUtc, $inputHash);
            """;
        Add(command, "$id", attempt.Id);
        Add(command, "$agentRunId", attempt.AgentRunId);
        Add(command, "$taskId", attempt.TaskId);
        AddNullable(command, "$commandId", attempt.CommandId);
        command.Parameters.AddWithValue("$attemptNumber", attempt.AttemptNumber);
        command.Parameters.AddWithValue("$operation", attempt.Operation.ToString());
        command.Parameters.AddWithValue("$status", attempt.Status.ToString());
        AddNullable(command, "$processId", attempt.ProcessId);
        AddNullable(command, "$processStartedAtUtc", attempt.ProcessStartedAtUtc);
        Add(command, "$startedAtUtc", attempt.StartedAtUtc);
        AddNullable(command, "$terminalEventAtUtc", attempt.TerminalEventAtUtc);
        AddNullable(command, "$processExitedAtUtc", attempt.ProcessExitedAtUtc);
        AddNullable(command, "$terminalEventType", attempt.TerminalEventType);
        AddNullable(command, "$exitCode", attempt.ExitCode);
        AddNullable(command, "$cancellationRequestedAtUtc", attempt.CancellationRequestedAtUtc);
        AddNullable(command, "$cancellationConfirmedAtUtc", attempt.CancellationConfirmedAtUtc);
        command.Parameters.AddWithValue("$lastEventSequence", attempt.LastEventSequence);
        AddNullable(command, "$lastEventAtUtc", attempt.LastEventAtUtc);
        command.Parameters.AddWithValue("$inputHash", attempt.InputHash.Trim());
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async Task UpdateAgentRunForNewAttemptAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        Guid agentRunId,
        DateTimeOffset updatedAtUtc,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            UPDATE agent_runs
            SET status = $status,
                updated_at_utc = $updatedAtUtc,
                final_summary = NULL,
                final_result_json = NULL,
                failure_code = NULL,
                failure_message = NULL
            WHERE id = $id;
            """;
        Add(command, "$id", agentRunId);
        command.Parameters.AddWithValue("$status", AgentRunStatus.Starting.ToString());
        Add(command, "$updatedAtUtc", updatedAtUtc);
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async Task<int> GetNextAttemptNumberAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        Guid agentRunId,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT COALESCE(MAX(attempt_number), 0) + 1
            FROM agent_attempts
            WHERE agent_run_id = $agentRunId;
            """;
        Add(command, "$agentRunId", agentRunId);
        return Convert.ToInt32(
            await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false),
            CultureInfo.InvariantCulture);
    }

    private static async Task MarkPendingDecisionAnsweredAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        Guid taskId,
        Guid? responseCommandId,
        DateTimeOffset respondedAtUtc,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            UPDATE decision_requests
            SET status = $answered,
                responded_at_utc = $respondedAtUtc,
                response_command_id = $responseCommandId
            WHERE task_id = $taskId AND status = $pending;
            """;
        Add(command, "$taskId", taskId);
        command.Parameters.AddWithValue("$answered", DecisionRequestStatus.Answered.ToString());
        Add(command, "$respondedAtUtc", respondedAtUtc);
        AddNullable(command, "$responseCommandId", responseCommandId);
        command.Parameters.AddWithValue("$pending", DecisionRequestStatus.Pending.ToString());
        if (await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) != 1)
        {
            throw new InvalidOperationException("没有可回答的待处理 Decision Request。");
        }
    }

    private static async Task InsertDecisionRequestAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        DecisionRequestRecord decision,
        CancellationToken cancellationToken)
    {
        ValidateJson(decision.OptionsJson, nameof(decision.OptionsJson));
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO decision_requests(
                id, task_id, agent_run_id, attempt_id, question,
                options_json, status, created_at_utc, responded_at_utc,
                response_command_id)
            VALUES(
                $id, $taskId, $agentRunId, $attemptId, $question,
                $optionsJson, $status, $createdAtUtc, $respondedAtUtc,
                $responseCommandId);
            """;
        Add(command, "$id", decision.Id);
        Add(command, "$taskId", decision.TaskId);
        Add(command, "$agentRunId", decision.AgentRunId);
        Add(command, "$attemptId", decision.AttemptId);
        command.Parameters.AddWithValue("$question", decision.Question.Trim());
        command.Parameters.AddWithValue("$optionsJson", decision.OptionsJson);
        command.Parameters.AddWithValue("$status", decision.Status.ToString());
        Add(command, "$createdAtUtc", decision.CreatedAtUtc);
        AddNullable(command, "$respondedAtUtc", decision.RespondedAtUtc);
        AddNullable(command, "$responseCommandId", decision.ResponseCommandId);
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private async Task<CommandRecord?> GetCommandByIdempotencyKeyAsync(
        Guid sourceDeviceId,
        string idempotencyKey,
        CancellationToken cancellationToken)
    {
        await using var connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT * FROM commands
            WHERE source_device_id = $sourceDeviceId AND idempotency_key = $idempotencyKey;
            """;
        Add(command, "$sourceDeviceId", sourceDeviceId);
        command.Parameters.AddWithValue("$idempotencyKey", idempotencyKey);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        return await reader.ReadAsync(cancellationToken).ConfigureAwait(false) ? ReadCommand(reader) : null;
    }

    private async Task RecordDuplicateCommandAsync(
        CommandRecord duplicate,
        CommandRecord existing,
        CancellationToken cancellationToken)
    {
        await using var connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = connection.BeginTransaction();
        await InsertAuditAsync(
            connection,
            transaction,
            DateTimeOffset.UtcNow,
            duplicate.SourceDeviceId,
            "CommandDuplicateIgnored",
            "Command",
            existing.Id.ToString("D"),
            AuditOutcome.Rejected,
            JsonSerializer.Serialize(new
            {
                duplicateCommandId = duplicate.Id,
                duplicate.IdempotencyKey
            }),
            cancellationToken).ConfigureAwait(false);
        transaction.Commit();
    }

    private static async Task<ProjectRecord?> GetProjectInTransactionAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        Guid projectId,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT * FROM projects WHERE id = $id;";
        Add(command, "$id", projectId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        return await reader.ReadAsync(cancellationToken).ConfigureAwait(false) ? ReadProject(reader) : null;
    }

    private static async Task<AgentTask?> GetTaskInTransactionAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        Guid taskId,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT * FROM tasks WHERE id = $id;";
        Add(command, "$id", taskId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        return await reader.ReadAsync(cancellationToken).ConfigureAwait(false) ? ReadTask(reader) : null;
    }

    private static async Task<CommandRecord?> GetCommandInTransactionAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        Guid commandId,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT * FROM commands WHERE id = $id;";
        Add(command, "$id", commandId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        return await reader.ReadAsync(cancellationToken).ConfigureAwait(false) ? ReadCommand(reader) : null;
    }

    private static void ValidateCreateTaskCommand(AgentTask task, CommandRecord sourceCommand)
    {
        if (sourceCommand.CommandType != CommandType.CreateTask || sourceCommand.Status != CommandStatus.Received)
        {
            throw new InvalidOperationException("来源命令不是尚未处理的 CreateTask 命令。");
        }

        if (sourceCommand.SourceDeviceId != task.CreatedByDeviceId || sourceCommand.ProjectId != task.ProjectId)
        {
            throw new InvalidOperationException("来源命令与任务的设备或项目不一致。");
        }

        if (sourceCommand.ExpiresAtUtc is { } expiresAt && expiresAt <= task.CreatedAtUtc)
        {
            throw new InvalidOperationException("来源命令已经过期。");
        }
    }

    private static async Task UpdateTaskAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        long expectedVersion,
        AgentTask task,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            UPDATE tasks SET
                status = $status,
                phase = $phase,
                cancellation_requested_at_utc = $cancellationRequestedAtUtc,
                updated_at_utc = $updatedAtUtc,
                started_at_utc = $startedAtUtc,
                completed_at_utc = $completedAtUtc,
                last_heartbeat_at_utc = $lastHeartbeatAtUtc,
                failure_code = $failureCode,
                failure_message = $failureMessage,
                version = $version
            WHERE id = $id AND version = $expectedVersion;
            """;
        Add(command, "$id", task.Id);
        command.Parameters.AddWithValue("$status", task.Status.ToString());
        command.Parameters.AddWithValue("$phase", task.Phase.ToString());
        AddNullable(command, "$cancellationRequestedAtUtc", task.CancellationRequestedAtUtc);
        Add(command, "$updatedAtUtc", task.UpdatedAtUtc);
        AddNullable(command, "$startedAtUtc", task.StartedAtUtc);
        AddNullable(command, "$completedAtUtc", task.CompletedAtUtc);
        AddNullable(command, "$lastHeartbeatAtUtc", task.LastHeartbeatAtUtc);
        AddNullable(command, "$failureCode", task.FailureCode);
        AddNullable(command, "$failureMessage", task.FailureMessage);
        command.Parameters.AddWithValue("$version", task.Version);
        command.Parameters.AddWithValue("$expectedVersion", expectedVersion);
        if (await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) != 1)
        {
            throw new InvalidOperationException("任务版本已变化，请重新读取后再操作。");
        }
    }

    private static async Task<long> GetNextSequenceAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        Guid taskId,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT COALESCE(MAX(sequence_number), 0) + 1 FROM task_events WHERE task_id = $taskId;";
        Add(command, "$taskId", taskId);
        return Convert.ToInt64(
            await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false),
            CultureInfo.InvariantCulture);
    }

    private static async Task InsertTaskEventAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        TaskEventRecord taskEvent,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO task_events(
                id, task_id, sequence_number, event_type, from_status, to_status,
                from_phase, to_phase,
                source, source_device_id, command_id, occurred_at_utc, message, data_json)
            VALUES(
                $id, $taskId, $sequenceNumber, $eventType, $fromStatus, $toStatus,
                $fromPhase, $toPhase,
                $source, $sourceDeviceId, $commandId, $occurredAtUtc, $message, $dataJson);
            """;
        Add(command, "$id", taskEvent.Id);
        Add(command, "$taskId", taskEvent.TaskId);
        command.Parameters.AddWithValue("$sequenceNumber", taskEvent.SequenceNumber);
        command.Parameters.AddWithValue("$eventType", taskEvent.EventType.ToString());
        AddNullable(command, "$fromStatus", taskEvent.FromStatus?.ToString());
        AddNullable(command, "$toStatus", taskEvent.ToStatus?.ToString());
        AddNullable(command, "$fromPhase", taskEvent.FromPhase?.ToString());
        AddNullable(command, "$toPhase", taskEvent.ToPhase?.ToString());
        command.Parameters.AddWithValue("$source", taskEvent.Source.ToString());
        AddNullable(command, "$sourceDeviceId", taskEvent.SourceDeviceId);
        AddNullable(command, "$commandId", taskEvent.CommandId);
        Add(command, "$occurredAtUtc", taskEvent.OccurredAtUtc);
        command.Parameters.AddWithValue("$message", taskEvent.Message);
        AddNullable(command, "$dataJson", taskEvent.DataJson);
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async Task InsertAuditAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        DateTimeOffset occurredAtUtc,
        Guid? actorDeviceId,
        string action,
        string entityType,
        string entityId,
        AuditOutcome outcome,
        string? detailsJson,
        CancellationToken cancellationToken,
        Guid? entryId = null)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO audit_log(
                id, occurred_at_utc, actor_device_id, action,
                entity_type, entity_id, outcome, details_json)
            VALUES(
                $id, $occurredAtUtc, $actorDeviceId, $action,
                $entityType, $entityId, $outcome, $detailsJson);
            """;
        Add(command, "$id", entryId ?? Guid.NewGuid());
        Add(command, "$occurredAtUtc", occurredAtUtc);
        AddNullable(command, "$actorDeviceId", actorDeviceId);
        command.Parameters.AddWithValue("$action", action);
        command.Parameters.AddWithValue("$entityType", entityType);
        command.Parameters.AddWithValue("$entityId", entityId);
        command.Parameters.AddWithValue("$outcome", outcome.ToString());
        AddNullable(command, "$detailsJson", detailsJson);
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static void BindTask(SqliteCommand command, AgentTask task)
    {
        Add(command, "$id", task.Id);
        Add(command, "$projectId", task.ProjectId);
        Add(command, "$createdByDeviceId", task.CreatedByDeviceId);
        command.Parameters.AddWithValue("$title", task.Title.Trim());
        command.Parameters.AddWithValue("$instruction", task.Instruction.Trim());
        command.Parameters.AddWithValue("$workingDirectoryRelativePath", task.WorkingDirectoryRelativePath);
        command.Parameters.AddWithValue("$executor", task.Executor.Trim());
        command.Parameters.AddWithValue("$status", task.Status.ToString());
        command.Parameters.AddWithValue("$phase", task.Phase.ToString());
        AddNullable(command, "$cancellationRequestedAtUtc", task.CancellationRequestedAtUtc);
        Add(command, "$createdAtUtc", task.CreatedAtUtc);
        Add(command, "$updatedAtUtc", task.UpdatedAtUtc);
        AddNullable(command, "$startedAtUtc", task.StartedAtUtc);
        AddNullable(command, "$completedAtUtc", task.CompletedAtUtc);
        AddNullable(command, "$lastHeartbeatAtUtc", task.LastHeartbeatAtUtc);
        AddNullable(command, "$failureCode", task.FailureCode);
        AddNullable(command, "$failureMessage", task.FailureMessage);
        command.Parameters.AddWithValue("$version", task.Version);
    }

    private static DeviceRecord ReadDevice(SqliteDataReader reader) => new()
    {
        Id = ReadGuid(reader, "id"),
        DisplayName = reader.GetString(reader.GetOrdinal("display_name")),
        DeviceType = ReadEnum<DeviceType>(reader, "device_type"),
        TrustState = ReadEnum<DeviceTrustState>(reader, "trust_state"),
        PublicKeyThumbprint = ReadNullableString(reader, "public_key_thumbprint"),
        CreatedAtUtc = ReadDateTime(reader, "created_at_utc"),
        LastSeenAtUtc = ReadNullableDateTime(reader, "last_seen_at_utc"),
        RevokedAtUtc = ReadNullableDateTime(reader, "revoked_at_utc")
    };

    private static ProjectRecord ReadProject(SqliteDataReader reader) => new()
    {
        Id = ReadGuid(reader, "id"),
        Name = reader.GetString(reader.GetOrdinal("name")),
        RootPath = reader.GetString(reader.GetOrdinal("root_path")),
        AuthorizationState = ReadEnum<ProjectAuthorizationState>(reader, "authorization_state"),
        AuthorizedByDeviceId = ReadGuid(reader, "authorized_by_device_id"),
        AuthorizedAtUtc = ReadDateTime(reader, "authorized_at_utc"),
        RevokedAtUtc = ReadNullableDateTime(reader, "revoked_at_utc"),
        CreatedAtUtc = ReadDateTime(reader, "created_at_utc"),
        UpdatedAtUtc = ReadDateTime(reader, "updated_at_utc")
    };

    private static ProjectAuthorizationRecord ReadProjectAuthorization(SqliteDataReader reader) => new()
    {
        Id = ReadGuid(reader, "id"),
        ProjectId = ReadGuid(reader, "project_id"),
        Scope = ReadEnum<ProjectAuthorizationScope>(reader, "scope"),
        ScopeValue = reader.GetString(reader.GetOrdinal("scope_value")),
        State = ReadEnum<ProjectAuthorizationState>(reader, "state"),
        AuthorizedByDeviceId = ReadGuid(reader, "authorized_by_device_id"),
        AuthorizedAtUtc = ReadDateTime(reader, "authorized_at_utc"),
        RevokedAtUtc = ReadNullableDateTime(reader, "revoked_at_utc"),
        UpdatedAtUtc = ReadDateTime(reader, "updated_at_utc")
    };

    private static ResourceScopeRecord ReadResourceScope(SqliteDataReader reader) => new()
    {
        Id = ReadGuid(reader, "id"),
        TaskId = ReadGuid(reader, "task_id"),
        ScopeType = ReadEnum<ResourceScopeType>(reader, "scope_type"),
        ResourceId = ReadNullableGuid(reader, "resource_id"),
        ScopeValue = reader.GetString(reader.GetOrdinal("scope_value")),
        AccessMode = ReadEnum<ResourceAccessMode>(reader, "access_mode"),
        GrantedByDeviceId = ReadGuid(reader, "granted_by_device_id"),
        GrantedAtUtc = ReadDateTime(reader, "granted_at_utc"),
        ExpiresAtUtc = ReadNullableDateTime(reader, "expires_at_utc"),
        RevokedAtUtc = ReadNullableDateTime(reader, "revoked_at_utc")
    };

    private static SkillInvocationRecord ReadSkillInvocation(SqliteDataReader reader) => new()
    {
        Id = ReadGuid(reader, "id"),
        TaskId = ReadGuid(reader, "task_id"),
        SequenceNumber = reader.GetInt32(reader.GetOrdinal("sequence_number")),
        SkillId = reader.GetString(reader.GetOrdinal("skill_id")),
        SkillVersion = reader.GetString(reader.GetOrdinal("skill_version")),
        Capability = reader.GetString(reader.GetOrdinal("capability")),
        InputJson = reader.GetString(reader.GetOrdinal("input_json")),
        Status = ReadEnum<SkillInvocationStatus>(reader, "status"),
        CreatedAtUtc = ReadDateTime(reader, "created_at_utc"),
        StartedAtUtc = ReadNullableDateTime(reader, "started_at_utc"),
        CompletedAtUtc = ReadNullableDateTime(reader, "completed_at_utc"),
        FailureCode = ReadNullableString(reader, "failure_code"),
        FailureMessage = ReadNullableString(reader, "failure_message")
    };

    private static AgentTask ReadTask(SqliteDataReader reader) => new()
    {
        Id = ReadGuid(reader, "id"),
        ProjectId = ReadGuid(reader, "project_id"),
        CreatedByDeviceId = ReadGuid(reader, "created_by_device_id"),
        Title = reader.GetString(reader.GetOrdinal("title")),
        Instruction = reader.GetString(reader.GetOrdinal("instruction")),
        WorkingDirectoryRelativePath = reader.GetString(reader.GetOrdinal("working_directory_relative_path")),
        Executor = reader.GetString(reader.GetOrdinal("executor")),
        Status = ReadEnum<TaskStatus>(reader, "status"),
        Phase = ReadEnum<TaskPhase>(reader, "phase"),
        CancellationRequestedAtUtc = ReadNullableDateTime(reader, "cancellation_requested_at_utc"),
        CreatedAtUtc = ReadDateTime(reader, "created_at_utc"),
        UpdatedAtUtc = ReadDateTime(reader, "updated_at_utc"),
        StartedAtUtc = ReadNullableDateTime(reader, "started_at_utc"),
        CompletedAtUtc = ReadNullableDateTime(reader, "completed_at_utc"),
        LastHeartbeatAtUtc = ReadNullableDateTime(reader, "last_heartbeat_at_utc"),
        FailureCode = ReadNullableString(reader, "failure_code"),
        FailureMessage = ReadNullableString(reader, "failure_message"),
        Version = reader.GetInt64(reader.GetOrdinal("version"))
    };

    private static CommandRecord ReadCommand(SqliteDataReader reader) => new()
    {
        Id = ReadGuid(reader, "id"),
        SourceDeviceId = ReadGuid(reader, "source_device_id"),
        ProjectId = ReadNullableGuid(reader, "project_id"),
        TaskId = ReadNullableGuid(reader, "task_id"),
        IdempotencyKey = reader.GetString(reader.GetOrdinal("idempotency_key")),
        CommandType = ReadEnum<CommandType>(reader, "command_type"),
        PayloadJson = reader.GetString(reader.GetOrdinal("payload_json")),
        ReceivedAtUtc = ReadDateTime(reader, "received_at_utc"),
        ExpiresAtUtc = ReadNullableDateTime(reader, "expires_at_utc"),
        Status = ReadEnum<CommandStatus>(reader, "status"),
        ProcessedAtUtc = ReadNullableDateTime(reader, "processed_at_utc"),
        RejectionReason = ReadNullableString(reader, "rejection_reason")
    };

    private static TaskEventRecord ReadTaskEvent(SqliteDataReader reader) => new()
    {
        Id = ReadGuid(reader, "id"),
        TaskId = ReadGuid(reader, "task_id"),
        SequenceNumber = reader.GetInt64(reader.GetOrdinal("sequence_number")),
        EventType = ReadEnum<TaskEventType>(reader, "event_type"),
        FromStatus = ReadNullableEnum<TaskStatus>(reader, "from_status"),
        ToStatus = ReadNullableEnum<TaskStatus>(reader, "to_status"),
        FromPhase = ReadNullableEnum<TaskPhase>(reader, "from_phase"),
        ToPhase = ReadNullableEnum<TaskPhase>(reader, "to_phase"),
        Source = ReadEnum<TaskEventSource>(reader, "source"),
        SourceDeviceId = ReadNullableGuid(reader, "source_device_id"),
        CommandId = ReadNullableGuid(reader, "command_id"),
        OccurredAtUtc = ReadDateTime(reader, "occurred_at_utc"),
        Message = reader.GetString(reader.GetOrdinal("message")),
        DataJson = ReadNullableString(reader, "data_json")
    };

    private static AgentRunRecord ReadAgentRun(SqliteDataReader reader) => new()
    {
        Id = ReadGuid(reader, "id"),
        TaskId = ReadGuid(reader, "task_id"),
        ConnectorId = reader.GetString(reader.GetOrdinal("connector_id")),
        Transport = reader.GetString(reader.GetOrdinal("transport")),
        ExternalRunId = ReadNullableString(reader, "external_run_id"),
        ConnectorVersion = ReadNullableString(reader, "connector_version"),
        Status = ReadEnum<AgentRunStatus>(reader, "status"),
        CreatedAtUtc = ReadDateTime(reader, "created_at_utc"),
        UpdatedAtUtc = ReadDateTime(reader, "updated_at_utc"),
        LastEventSequence = reader.GetInt64(reader.GetOrdinal("last_event_sequence")),
        LastEventType = ReadNullableString(reader, "last_event_type"),
        LastEventAtUtc = ReadNullableDateTime(reader, "last_event_at_utc"),
        FinalSummary = ReadNullableString(reader, "final_summary"),
        FinalResultJson = ReadNullableString(reader, "final_result_json"),
        FailureCode = ReadNullableString(reader, "failure_code"),
        FailureMessage = ReadNullableString(reader, "failure_message")
    };

    private static AgentAttemptRecord ReadAgentAttempt(SqliteDataReader reader) => new()
    {
        Id = ReadGuid(reader, "id"),
        AgentRunId = ReadGuid(reader, "agent_run_id"),
        TaskId = ReadGuid(reader, "task_id"),
        CommandId = ReadNullableGuid(reader, "command_id"),
        AttemptNumber = reader.GetInt32(reader.GetOrdinal("attempt_number")),
        Operation = ReadEnum<AgentAttemptOperation>(reader, "operation"),
        Status = ReadEnum<AgentAttemptStatus>(reader, "status"),
        ProcessId = ReadNullableInt32(reader, "process_id"),
        ProcessStartedAtUtc = ReadNullableDateTime(reader, "process_started_at_utc"),
        StartedAtUtc = ReadDateTime(reader, "started_at_utc"),
        TerminalEventAtUtc = ReadNullableDateTime(reader, "terminal_event_at_utc"),
        ProcessExitedAtUtc = ReadNullableDateTime(reader, "process_exited_at_utc"),
        TerminalEventType = ReadNullableString(reader, "terminal_event_type"),
        ExitCode = ReadNullableInt32(reader, "exit_code"),
        CancellationRequestedAtUtc = ReadNullableDateTime(reader, "cancellation_requested_at_utc"),
        CancellationConfirmedAtUtc = ReadNullableDateTime(reader, "cancellation_confirmed_at_utc"),
        LastEventSequence = reader.GetInt64(reader.GetOrdinal("last_event_sequence")),
        LastEventAtUtc = ReadNullableDateTime(reader, "last_event_at_utc"),
        InputHash = reader.GetString(reader.GetOrdinal("input_hash"))
    };

    private static DecisionRequestRecord ReadDecisionRequest(SqliteDataReader reader) => new()
    {
        Id = ReadGuid(reader, "id"),
        TaskId = ReadGuid(reader, "task_id"),
        AgentRunId = ReadGuid(reader, "agent_run_id"),
        AttemptId = ReadGuid(reader, "attempt_id"),
        Question = reader.GetString(reader.GetOrdinal("question")),
        OptionsJson = reader.GetString(reader.GetOrdinal("options_json")),
        Status = ReadEnum<DecisionRequestStatus>(reader, "status"),
        CreatedAtUtc = ReadDateTime(reader, "created_at_utc"),
        RespondedAtUtc = ReadNullableDateTime(reader, "responded_at_utc"),
        ResponseCommandId = ReadNullableGuid(reader, "response_command_id")
    };

    private static AuditLogEntry ReadAudit(SqliteDataReader reader) => new()
    {
        Id = ReadGuid(reader, "id"),
        OccurredAtUtc = ReadDateTime(reader, "occurred_at_utc"),
        ActorDeviceId = ReadNullableGuid(reader, "actor_device_id"),
        Action = reader.GetString(reader.GetOrdinal("action")),
        EntityType = reader.GetString(reader.GetOrdinal("entity_type")),
        EntityId = reader.GetString(reader.GetOrdinal("entity_id")),
        Outcome = ReadEnum<AuditOutcome>(reader, "outcome"),
        DetailsJson = ReadNullableString(reader, "details_json")
    };

    private static void Add(SqliteCommand command, string name, Guid value) =>
        command.Parameters.AddWithValue(name, value.ToString("D"));

    private static void Add(SqliteCommand command, string name, DateTimeOffset value) =>
        command.Parameters.AddWithValue(name, ToDb(value));

    private static void AddNullable(SqliteCommand command, string name, object? value)
    {
        command.Parameters.AddWithValue(
            name,
            value switch
            {
                null => DBNull.Value,
                Guid guid => guid.ToString("D"),
                DateTimeOffset dateTime => ToDb(dateTime),
                _ => value
            });
    }

    private static string ToDb(DateTimeOffset value) =>
        value.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture);

    private static Guid ReadGuid(SqliteDataReader reader, string name) =>
        Guid.Parse(reader.GetString(reader.GetOrdinal(name)));

    private static Guid? ReadNullableGuid(SqliteDataReader reader, string name)
    {
        var ordinal = reader.GetOrdinal(name);
        return reader.IsDBNull(ordinal) ? null : Guid.Parse(reader.GetString(ordinal));
    }

    private static DateTimeOffset ReadDateTime(SqliteDataReader reader, string name) =>
        DateTimeOffset.Parse(
            reader.GetString(reader.GetOrdinal(name)),
            CultureInfo.InvariantCulture,
            DateTimeStyles.RoundtripKind);

    private static DateTimeOffset? ReadNullableDateTime(SqliteDataReader reader, string name)
    {
        var ordinal = reader.GetOrdinal(name);
        return reader.IsDBNull(ordinal)
            ? null
            : DateTimeOffset.Parse(
                reader.GetString(ordinal),
                CultureInfo.InvariantCulture,
                DateTimeStyles.RoundtripKind);
    }

    private static string? ReadNullableString(SqliteDataReader reader, string name)
    {
        var ordinal = reader.GetOrdinal(name);
        return reader.IsDBNull(ordinal) ? null : reader.GetString(ordinal);
    }

    private static int? ReadNullableInt32(SqliteDataReader reader, string name)
    {
        var ordinal = reader.GetOrdinal(name);
        return reader.IsDBNull(ordinal) ? null : reader.GetInt32(ordinal);
    }

    private static T ReadEnum<T>(SqliteDataReader reader, string name)
        where T : struct, Enum =>
        Enum.Parse<T>(reader.GetString(reader.GetOrdinal(name)), ignoreCase: false);

    private static T? ReadNullableEnum<T>(SqliteDataReader reader, string name)
        where T : struct, Enum
    {
        var ordinal = reader.GetOrdinal(name);
        return reader.IsDBNull(ordinal)
            ? null
            : Enum.Parse<T>(reader.GetString(ordinal), ignoreCase: false);
    }

    private static void RequireText(string value, string parameterName)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new ArgumentException("值不能为空。", parameterName);
        }
    }

    private static void ValidateJson(string json, string parameterName)
    {
        try
        {
            using var _ = JsonDocument.Parse(json);
        }
        catch (JsonException exception)
        {
            throw new ArgumentException("值必须是有效 JSON。", parameterName, exception);
        }
    }
}
