using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using ScreenGuide.Core.Conversations;
using ScreenGuide.Core.Sessions;
using ScreenGuide.Core.Tasking;
using ScreenGuide.DesktopHost.Runtime;
using ScreenGuide.DesktopProtocol;

namespace ScreenGuide.DesktopHost.Tests;

public sealed partial class BridgeIpcIntegrationTests
{
    [Theory]
    [InlineData(CommandStatus.Received, false)]
    [InlineData(CommandStatus.Rejected, false)]
    [InlineData(CommandStatus.Failed, false)]
    [InlineData(CommandStatus.Processed, true)]
    public async Task PersistedCancellationDuplicatePreservesItsOriginalAcceptance(CommandStatus status, bool accepted)
    {
        await using var environment = DesktopHostTestEnvironment.Create();
        using var host = environment.BuildHost();
        await host.StartAsync();
        var (_, _, task) = await environment.SeedTaskAsync("SYNTHETIC_CANCEL_RECEIPT");
        await using var store = await environment.OpenStoreAsync();
        var command = new CommandRecord
        {
            Id = Guid.NewGuid(),
            SourceDeviceId = host.Services.GetRequiredService<DesktopHostState>().Snapshot.LocalDevice!.Id,
            ProjectId = task.ProjectId, TaskId = task.Id, IdempotencyKey = "synthetic-cancel-receipt",
            CommandType = CommandType.CancelTask,
            PayloadJson = JsonSerializer.Serialize(new { expectedVersion = task.Version }),
            ReceivedAtUtc = environment.TimeProvider.GetUtcNow(), Status = status
        };
        Assert.True((await store.RegisterCommandAsync(command)).Accepted);

        var result = await host.Services.GetRequiredService<LocalTaskEntryService>().CancelTaskAsync(
            task.Id, command.IdempotencyKey, expectedVersion: task.Version);

        Assert.True(result.WasDuplicate);
        Assert.Equal(accepted, result.Accepted);
        Assert.Equal(task.Version, (await store.GetTaskAsync(task.Id))!.Version);
        Assert.Equal(status, (await store.GetCommandAsync(command.Id))!.Status);
        await host.StopAsync();
    }

    [Theory]
    [InlineData("target")]
    [InlineData("type")]
    [InlineData("version")]
    public async Task PersistedCancellationKeyCannotBeReusedForAnotherRequest(string mismatch)
    {
        await using var environment = DesktopHostTestEnvironment.Create();
        using var host = environment.BuildHost();
        await host.StartAsync();
        var (_, _, task) = await environment.SeedTaskAsync("SYNTHETIC_CANCEL_CONFLICT");
        await using var store = await environment.OpenStoreAsync();
        var otherTask = task with { Id = Guid.NewGuid(), Instruction = "SYNTHETIC_OTHER_TARGET" };
        var creation = new CommandRecord
        {
            Id = Guid.NewGuid(), SourceDeviceId = task.CreatedByDeviceId, ProjectId = task.ProjectId,
            IdempotencyKey = "synthetic-create-other-target", CommandType = CommandType.CreateTask,
            PayloadJson = "{}", ReceivedAtUtc = environment.TimeProvider.GetUtcNow()
        };
        Assert.True((await store.RegisterCommandAsync(creation)).Accepted);
        await store.CreateTaskAsync(otherTask, creation.Id);
        var command = new CommandRecord
        {
            Id = Guid.NewGuid(),
            SourceDeviceId = host.Services.GetRequiredService<DesktopHostState>().Snapshot.LocalDevice!.Id,
            ProjectId = task.ProjectId, TaskId = mismatch == "target" ? otherTask.Id : task.Id,
            IdempotencyKey = "synthetic-cancel-conflict",
            CommandType = mismatch == "type" ? CommandType.ResumeTask : CommandType.CancelTask,
            PayloadJson = JsonSerializer.Serialize(new { expectedVersion = task.Version + (mismatch == "version" ? 1 : 0) }),
            ReceivedAtUtc = environment.TimeProvider.GetUtcNow(), Status = CommandStatus.Processed
        };
        Assert.True((await store.RegisterCommandAsync(command)).Accepted);

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            host.Services.GetRequiredService<LocalTaskEntryService>().CancelTaskAsync(
                task.Id, command.IdempotencyKey, expectedVersion: task.Version));

        Assert.Equal("request_conflict", exception.Message);
        Assert.Equal(task.Version, (await store.GetTaskAsync(task.Id))!.Version);
        Assert.Equal(otherTask.Version, (await store.GetTaskAsync(otherTask.Id))!.Version);
        await host.StopAsync();
    }

    [Fact]
    public async Task IslandTaskCancellationStopsTheFakeProcessTreeAndScopesPersistentIdentity()
    {
        await using var environment = DesktopHostTestEnvironment.Create();
        var (_, project, _) = await environment.SeedProjectsAsync();
        using var processes = new ReadyProcessTree(environment.RootDirectory);
        using var host = environment.BuildHost();
        await host.StartAsync();
        var entry = host.Services.GetRequiredService<LocalTaskEntryService>();
        var execution = host.Services.GetRequiredService<AgentTaskExecutionService>();
        var created = await entry.CreateTaskAsync(new CreateLocalTaskRequest(project.Id, processes.Prompt, "Bridge cancel fixture"));
        await processes.WaitUntilReadyAsync();
        var client = Guid.NewGuid();
        var handshakeResponse = await SendAsync(environment.Options.PipeName, DesktopApiMethods.BridgeHandshake,
            HandshakePayload(clientInstanceId: client.ToString("D"), supported: ["bridge_snapshot_v1", "island_cancel_v1"]));
        Assert.True(handshakeResponse.Success);
        var handshake = handshakeResponse.Payload!.Value.Deserialize<BridgeHandshakeResponseDto>(DesktopProtocolJson.Options)!;
        var snapshotResponse = await SendAsync(environment.Options.PipeName, DesktopApiMethods.BridgeSnapshotGet,
            SnapshotPayload(client, handshake.ServerInstanceId));
        var target = Assert.Single(snapshotResponse.Payload!.Value.Deserialize<BridgeSnapshotDto>(DesktopProtocolJson.Options)!.Presentations);
        Assert.Equal("task", target.EntityKind);
        var requestId = Guid.NewGuid();
        await using var store = await environment.OpenStoreAsync();
        var legacy = new CommandRecord
        {
            Id = Guid.NewGuid(),
            SourceDeviceId = host.Services.GetRequiredService<DesktopHostState>().Snapshot.LocalDevice!.Id,
            ProjectId = project.Id, TaskId = created.TaskId,
            IdempotencyKey = $"island-{requestId:D}", CommandType = CommandType.CancelTask,
            PayloadJson = "{}", ReceivedAtUtc = environment.TimeProvider.GetUtcNow(), Status = CommandStatus.Failed
        };
        Assert.True((await store.RegisterCommandAsync(legacy)).Accepted);
        var previousServer = legacy with { Id = Guid.NewGuid(), IdempotencyKey = $"island-{Guid.NewGuid():D}-{client:D}-{requestId:D}" };
        Assert.True((await store.RegisterCommandAsync(previousServer)).Accepted);
        var request = new
        {
            bridge_protocol_version = 1, client_instance_id = client.ToString("D"),
            expected_server_instance_id = handshake.ServerInstanceId,
            presentation_key = target.PresentationKey, expected_version = target.EntityVersion,
            request_id = requestId.ToString("D")
        };

        var outcomes = await Task.WhenAll(
            SendAsync(environment.Options.PipeName, DesktopApiMethods.BridgeCancel, request),
            SendAsync(environment.Options.PipeName, DesktopApiMethods.BridgeCancel, request));

        Assert.All(outcomes, outcome => Assert.True(outcome.Success, outcome.Error?.Code));
        Assert.All(outcomes, outcome => Assert.True(outcome.Payload!.Value.GetProperty("accepted").GetBoolean()));
        Assert.Single(outcomes, outcome => !outcome.Payload!.Value.GetProperty("duplicate").GetBoolean());
        Assert.Single(outcomes, outcome => outcome.Payload!.Value.GetProperty("duplicate").GetBoolean());
        await execution.WaitForTaskAsync(created.TaskId);
        await processes.AssertStoppedAsync();
        Assert.Equal(ScreenGuide.Core.Tasking.TaskStatus.Cancelled, (await store.GetTaskAsync(created.TaskId))!.Status);
        var commands = await store.GetTaskCommandsAsync(created.TaskId);
        var actual = Assert.Single(commands, command => command.IdempotencyKey == $"island-{handshake.ServerInstanceId}-{client:D}-{requestId:D}");
        Assert.Equal(CommandStatus.Processed, actual.Status);
        Assert.Equal(CommandStatus.Failed, (await store.GetCommandAsync(legacy.Id))!.Status);
        Assert.Equal(CommandStatus.Failed, (await store.GetCommandAsync(previousServer.Id))!.Status);
        Assert.Single(await store.GetTaskEventsAsync(created.TaskId), item => item.EventType == TaskEventType.CancellationRequested);
        await host.StopAsync();
    }

    [Fact]
    public async Task RejectedPersistentCancellationRemainsRejectedOnBridgeRetry()
    {
        await using var environment = DesktopHostTestEnvironment.Create();
        using var host = environment.BuildHost();
        await host.StartAsync();
        var (_, _, task) = await environment.SeedTaskAsync("SYNTHETIC_REJECTED_RECEIPT");
        var client = Guid.NewGuid();
        var handshakeResponse = await SendAsync(environment.Options.PipeName, DesktopApiMethods.BridgeHandshake,
            HandshakePayload(clientInstanceId: client.ToString("D"), supported: ["bridge_snapshot_v1", "island_cancel_v1"]));
        var handshake = handshakeResponse.Payload!.Value.Deserialize<BridgeHandshakeResponseDto>(DesktopProtocolJson.Options)!;
        var requestId = Guid.NewGuid();
        await using var store = await environment.OpenStoreAsync();
        var command = new CommandRecord
        {
            Id = Guid.NewGuid(),
            SourceDeviceId = host.Services.GetRequiredService<DesktopHostState>().Snapshot.LocalDevice!.Id,
            ProjectId = task.ProjectId, TaskId = task.Id,
            IdempotencyKey = $"island-{handshake.ServerInstanceId}-{client:D}-{requestId:D}",
            CommandType = CommandType.CancelTask,
            PayloadJson = JsonSerializer.Serialize(new { expectedVersion = task.Version }),
            ReceivedAtUtc = environment.TimeProvider.GetUtcNow(), Status = CommandStatus.Failed
        };
        Assert.True((await store.RegisterCommandAsync(command)).Accepted);
        var request = new
        {
            bridge_protocol_version = 1, client_instance_id = client.ToString("D"),
            expected_server_instance_id = handshake.ServerInstanceId, presentation_key = $"task:{task.Id:D}",
            expected_version = task.Version, request_id = requestId.ToString("D")
        };

        AssertBridgeError(await SendAsync(environment.Options.PipeName, DesktopApiMethods.BridgeCancel, request),
            "task_not_cancellable", "SYNTHETIC_REJECTED_RECEIPT");
        AssertBridgeError(await SendAsync(environment.Options.PipeName, DesktopApiMethods.BridgeCancel, request),
            "task_not_cancellable", "SYNTHETIC_REJECTED_RECEIPT");
        Assert.Equal(task.Version, (await store.GetTaskAsync(task.Id))!.Version);
        Assert.Equal(CommandStatus.Failed, (await store.GetCommandAsync(command.Id))!.Status);
        await host.StopAsync();
    }

    [Fact]
    public async Task IslandCancellationUsesNegotiatedCapabilityAndExactVersionAndIsIdempotent()
    {
        await using var environment = DesktopHostTestEnvironment.Create();
        using var host = environment.BuildHost();
        await host.StartAsync();
        var now = environment.TimeProvider.GetUtcNow();
        var store = host.Services.GetRequiredService<ISessionStore>();
        var conversation = await host.Services.GetRequiredService<ConversationService>().CreateAsync("PRIVATE_CANCEL_CANARY");
        var session = new SessionRecord {
            Id = Guid.NewGuid(), ConversationId = conversation.Id,
            CreatedByDeviceId = host.Services.GetRequiredService<DesktopHostState>().Snapshot.LocalDevice!.Id,
            Title = "PRIVATE_CANCEL_CANARY", IsCurrent = true, CreatedAtUtc = now, UpdatedAtUtc = now, LastActiveAtUtc = now,
        };
        await store.CreateSessionAsync(session);
        var registration = await store.StartTurnAsync(session.Id, "PRIVATE_CANCEL_CANARY", "Text", Guid.NewGuid().ToString("N"),
            new SessionTurnFrozenRoute { Status = SessionTurnRouteStatus.Ready, ProviderId = "synthetic", ModelId = "synthetic", DataDestination = "local synthetic test", SendsDataOffDevice = false, FrozenAtUtc = now }, now);
        await host.Services.GetRequiredService<SessionCoordinator>().SetCurrentAsync(session.Id);
        var client = Guid.NewGuid();
        var handshakeResponse = await SendAsync(environment.Options.PipeName, DesktopApiMethods.BridgeHandshake,
            HandshakePayload(clientInstanceId: client.ToString("D"), supported: ["bridge_snapshot_v1", "island_cancel_v1"]));
        Assert.True(handshakeResponse.Success);
        var handshake = handshakeResponse.Payload!.Value.Deserialize<BridgeHandshakeResponseDto>(DesktopProtocolJson.Options)!;
        Assert.Contains("island_cancel_v1", handshake.EnabledCapabilities);
        var read = await SendAsync(environment.Options.PipeName, DesktopApiMethods.BridgeSnapshotGet, SnapshotPayload(client, handshake.ServerInstanceId));
        var target = Assert.Single(read.Payload!.Value.Deserialize<BridgeSnapshotDto>(DesktopProtocolJson.Options)!.Presentations);
        var commandId = Guid.NewGuid();
        Dictionary<string, object?> Request(long version, string? server = null, Guid? id = null) => new() {
            ["bridge_protocol_version"] = 1, ["client_instance_id"] = client.ToString("D"),
            ["expected_server_instance_id"] = server ?? handshake.ServerInstanceId,
            ["presentation_key"] = target.PresentationKey, ["expected_version"] = version, ["request_id"] = (id ?? commandId).ToString("D"),
        };
        var stale = await SendAsync(environment.Options.PipeName, DesktopApiMethods.BridgeCancel, Request(target.EntityVersion + 1));
        AssertBridgeError(stale, "entity_version_mismatch", "PRIVATE_CANCEL_CANARY");
        var restarted = await SendAsync(environment.Options.PipeName, DesktopApiMethods.BridgeCancel, Request(target.EntityVersion, Guid.NewGuid().ToString("D")));
        AssertBridgeError(restarted, "server_instance_mismatch", "PRIVATE_CANCEL_CANARY");
        var malformed = Request(target.EntityVersion); malformed["expected_version"] = "invalid";
        AssertBridgeError(await SendAsync(environment.Options.PipeName, DesktopApiMethods.BridgeCancel, malformed), "invalid_request", "PRIVATE_CANCEL_CANARY");
        var before = await store.GetTurnAsync(registration.Turn.Id);
        Assert.NotEqual(SessionTurnPhase.Cancelled, before!.Phase);

        var cancelled = await SendAsync(environment.Options.PipeName, DesktopApiMethods.BridgeCancel, Request(target.EntityVersion));
        Assert.True(cancelled.Success, cancelled.Error?.Code);
        Assert.True(cancelled.Payload!.Value.GetProperty("accepted").GetBoolean());
        Assert.Equal(SessionTurnPhase.Cancelled, (await store.GetTurnAsync(registration.Turn.Id))!.Phase);
        var duplicate = await SendAsync(environment.Options.PipeName, DesktopApiMethods.BridgeCancel, Request(target.EntityVersion));
        Assert.True(duplicate.Success, duplicate.Error?.Code);
        Assert.True(duplicate.Payload!.Value.GetProperty("duplicate").GetBoolean());
        var conflict = await SendAsync(environment.Options.PipeName, DesktopApiMethods.BridgeCancel, Request(target.EntityVersion + 1));
        AssertBridgeError(conflict, "request_conflict", "PRIVATE_CANCEL_CANARY");
        Assert.DoesNotContain("PRIVATE_CANCEL_CANARY", cancelled.Payload.Value.GetRawText());
        await host.StopAsync();
    }

    [Fact]
    public async Task ReadOnlyBridgeClientCannotCancelEvenWithAValidServerIdentity()
    {
        await using var environment = DesktopHostTestEnvironment.Create();
        using var host = environment.BuildHost();
        await host.StartAsync();
        var client = Guid.NewGuid();
        var handshake = await HandshakeAsync(environment.Options.PipeName, client);
        var response = await SendAsync(environment.Options.PipeName, DesktopApiMethods.BridgeCancel, new {
            bridge_protocol_version = 1, client_instance_id = client.ToString("D"), expected_server_instance_id = handshake.ServerInstanceId,
            presentation_key = $"turn:{Guid.NewGuid():D}", expected_version = 1, request_id = Guid.NewGuid().ToString("D"),
        });
        AssertBridgeError(response, "capability_not_enabled", "synthetic");
        await host.StopAsync();
    }

    [Fact]
    public async Task TaskCancellationVersionCheckIsInsideTheStorageTransaction()
    {
        await using var environment = DesktopHostTestEnvironment.Create();
        using var host = environment.BuildHost();
        await host.StartAsync();
        var (device, _, task) = await environment.SeedTaskAsync("SYNTHETIC_CAS");
        await using var store = await environment.OpenStoreAsync();
        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() => store.RequestCancellationAsync(
            task.Id, device.Id, expectedVersion: task.Version + 1));
        Assert.Equal("entity_version_mismatch", exception.Message);
        Assert.Equal(task.Version, (await store.GetTaskAsync(task.Id))!.Version);
        Assert.True(await store.RequestCancellationAsync(task.Id, device.Id, expectedVersion: task.Version));
        await host.StopAsync();
    }

    [Fact]
    public async Task AlreadyCancellingTaskIsNotReportedAsANewAcceptedCancellation()
    {
        await using var environment = DesktopHostTestEnvironment.Create();
        using var host = environment.BuildHost();
        await host.StartAsync();
        var (device, _, task) = await environment.SeedTaskAsync("SYNTHETIC_CANCELLING");
        await using var store = await environment.OpenStoreAsync();
        Assert.True(await store.RequestCancellationAsync(task.Id, device.Id));
        var client = Guid.NewGuid();
        var response = await SendAsync(environment.Options.PipeName, DesktopApiMethods.BridgeHandshake,
            HandshakePayload(clientInstanceId: client.ToString("D"), supported: ["bridge_snapshot_v1", "island_cancel_v1"]));
        var handshake = response.Payload!.Value.Deserialize<BridgeHandshakeResponseDto>(DesktopProtocolJson.Options)!;
        var snapshotResponse = await SendAsync(environment.Options.PipeName, DesktopApiMethods.BridgeSnapshotGet, SnapshotPayload(client, handshake.ServerInstanceId));
        var target = Assert.Single(snapshotResponse.Payload!.Value.Deserialize<BridgeSnapshotDto>(DesktopProtocolJson.Options)!.Presentations);
        Assert.Equal("CancellationRequested", target.Phase);
        var result = await SendAsync(environment.Options.PipeName, DesktopApiMethods.BridgeCancel, new {
            bridge_protocol_version = 1, client_instance_id = client.ToString("D"), expected_server_instance_id = handshake.ServerInstanceId,
            presentation_key = target.PresentationKey, expected_version = target.EntityVersion, request_id = Guid.NewGuid().ToString("D"),
        });
        AssertBridgeError(result, "task_not_cancellable", "SYNTHETIC_CANCELLING");
        await host.StopAsync();
    }
}
