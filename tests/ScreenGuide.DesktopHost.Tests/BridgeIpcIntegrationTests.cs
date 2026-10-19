using System.IO.Pipes;
using System.Security.Principal;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using ScreenGuide.Core.Conversations;
using ScreenGuide.Core.Memories;
using ScreenGuide.Core.Sessions;
using ScreenGuide.Core.Tasking;
using ScreenGuide.DesktopHost.Runtime;
using ScreenGuide.DesktopProtocol;
using AgentTaskStatus = ScreenGuide.Core.Tasking.TaskStatus;

namespace ScreenGuide.DesktopHost.Tests;

public sealed partial class BridgeIpcIntegrationTests
{
    [Fact]
    public async Task HandshakeNegotiatesSnapshotOnlyAtDesktopProtocolV12()
    {
        await using var environment = DesktopHostTestEnvironment.Create();
        using var host = environment.BuildHost();
        await host.StartAsync();

        var response = await SendAsync(
            environment.Options.PipeName,
            DesktopApiMethods.BridgeHandshake,
            HandshakePayload(
                supported: ["bridge_snapshot_v1", "future_optional_v1"],
                required: ["bridge_snapshot_v1"],
                snapshotBytes: 65_536,
                eventBatchBytes: 262_144,
                eventsPerBatch: 20,
                eventBytes: 16_384));
        await host.StopAsync();

        Assert.True(response.Success, response.Error?.Code);
        Assert.Equal(12, response.ProtocolVersion);
        var handshake = response.Payload!.Value.Deserialize<BridgeHandshakeResponseDto>(
            DesktopProtocolJson.Options)!;
        Assert.Equal(1, handshake.BridgeProtocolVersion);
        Assert.Equal(12, handshake.DesktopIpcProtocolVersion);
        Assert.NotEqual(Guid.Empty, Guid.Parse(handshake.ServerInstanceId));
        Assert.Equal("yuanshu-core", handshake.ServerProduct);
        Assert.False(string.IsNullOrWhiteSpace(handshake.ServerProductVersion));
        Assert.DoesNotContain("0.3.0", handshake.ServerProductVersion, StringComparison.Ordinal);
        Assert.Equal(["bridge_snapshot_v1"], handshake.EnabledCapabilities);
        Assert.Equal(65_536, handshake.Limits.SnapshotBytes);
        Assert.Equal(262_144, handshake.Limits.EventBatchBytes);
        Assert.Equal(20, handshake.Limits.EventsPerBatch);
        Assert.Equal(16_384, handshake.Limits.EventBytes);
        Assert.Equal(DesktopIpcFraming.MaximumMessageBytes, handshake.Limits.OuterFrameBytes);
        Assert.EndsWith("Z", handshake.IssuedAtUtc, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(8)]
    [InlineData(10)]
    [InlineData(11)]
    [InlineData(13)]
    public async Task DesktopProtocolRejectsEveryVersionExceptV12(int protocolVersion)
    {
        await using var environment = DesktopHostTestEnvironment.Create();
        using var host = environment.BuildHost();
        await host.StartAsync();
        var response = await SendAsync(
            environment.Options.PipeName,
            DesktopApiMethods.BridgeHandshake,
            HandshakePayload(),
            protocolVersion);
        await host.StopAsync();

        Assert.False(response.Success);
        Assert.Equal("protocol_version_unsupported", response.Error?.Code);
    }

    [Fact]
    public async Task SnapshotProjectsAuthorityWithoutSensitiveSessionOrTaskFields()
    {
        const string Canary = "SENSITIVE_BRIDGE_CANARY";
        await using var environment = DesktopHostTestEnvironment.Create();
        using var host = environment.BuildHost();
        await host.StartAsync();
        var (_, project, task) = await environment.SeedTaskAsync(Canary);
        await using (var taskStore = await environment.OpenStoreAsync())
        {
            await taskStore.TransitionTaskAsync(
                task.Id,
                AgentTaskStatus.Running,
                TaskEventSource.System,
                Canary);
        }

        var now = environment.TimeProvider.GetUtcNow();
        var sessionStore = host.Services.GetRequiredService<ISessionStore>();
        var conversation = await host.Services.GetRequiredService<ConversationService>()
            .CreateAsync(Canary);
        var session = new SessionRecord
        {
            Id = Guid.NewGuid(),
            ConversationId = conversation.Id,
            CreatedByDeviceId = task.CreatedByDeviceId,
            Title = Canary,
            IsCurrent = true,
            CreatedAtUtc = now,
            UpdatedAtUtc = now,
            LastActiveAtUtc = now
        };
        await sessionStore.CreateSessionAsync(session);
        var registration = await sessionStore.StartTurnAsync(
            session.Id,
            Canary,
            "Text",
            $"bridge-{Guid.NewGuid():N}",
            new SessionTurnFrozenRoute
            {
                Status = SessionTurnRouteStatus.Ready,
                ProviderId = Canary,
                ModelId = Canary,
                DataDestination = Canary,
                SendsDataOffDevice = true,
                FrozenAtUtc = now
            },
            now);
        var updated = await sessionStore.UpdateTurnAsync(
            registration.Turn with
            {
                WorkKind = SessionWorkKind.CodingTask,
                Phase = SessionTurnPhase.WaitingForConfirmation,
                MissingContext = SessionMissingContext.Confirmation,
                TaskId = task.Id,
                ProjectId = project.Id,
                FilePath = $"C:\\{Canary}\\secret.txt",
                WindowHandle = 1234,
                WindowTitle = Canary,
                WindowProcessName = Canary,
                RequiresConfirmation = true,
                ResultSummary = Canary,
                FailureMessage = Canary
            },
            registration.Turn.Version,
            now.AddSeconds(1));
        await host.Services.GetRequiredService<SessionCoordinator>().SetCurrentAsync(session.Id);

        var response = await GetSnapshotAsync(environment.Options.PipeName);
        await host.StopAsync();

        Assert.True(response.Success, response.Error?.Code);
        var raw = response.Payload!.Value.GetRawText();
        var snapshot = response.Payload.Value.Deserialize<BridgeSnapshotDto>(DesktopProtocolJson.Options)!;
        var presentation = Assert.Single(snapshot.Presentations);
        Assert.Equal("yuanshu.core", snapshot.Source);
        Assert.Equal($"turn:{updated.Id:D}", presentation.PresentationKey);
        Assert.Equal(session.Id.ToString("D"), presentation.SessionId);
        Assert.Equal(updated.Id.ToString("D"), presentation.TurnId);
        Assert.Equal(task.Id.ToString("D"), presentation.TaskId);
        Assert.Equal(updated.Version, presentation.EntityVersion);
        Assert.Equal("WaitingForConfirmation", presentation.Phase);
        Assert.Equal("WaitingUser", presentation.IslandKind);
        Assert.Equal("需要你的确认", presentation.Title);
        Assert.True(presentation.RequiresUserAction);
        Assert.Empty(presentation.Actions);
        Assert.EndsWith("Z", snapshot.GeneratedAtUtc, StringComparison.Ordinal);
        AssertNoSensitivePayload(raw, Canary);
    }

    [Fact]
    public async Task MemoryOutboundConsentUsesFixedSafeWaitingProjection()
    {
        const string Canary = "MEMORY_CONSENT_PRIVATE_CANARY";
        await using var environment = DesktopHostTestEnvironment.Create();
        using var host = environment.BuildHost();
        await host.StartAsync();
        var (_, _, task) = await environment.SeedTaskAsync(Canary);
        var now = environment.TimeProvider.GetUtcNow();
        var store = host.Services.GetRequiredService<ISessionStore>();
        var conversation = await host.Services.GetRequiredService<ConversationService>().CreateAsync(Canary);
        var session = new SessionRecord
        {
            Id = Guid.NewGuid(),
            ConversationId = conversation.Id,
            CreatedByDeviceId = task.CreatedByDeviceId,
            Title = Canary,
            IsCurrent = true,
            CreatedAtUtc = now,
            UpdatedAtUtc = now,
            LastActiveAtUtc = now
        };
        await store.CreateSessionAsync(session);
        var registration = await store.StartTurnAsync(
            session.Id,
            Canary,
            "Text",
            $"memory-{Guid.NewGuid():N}",
            new SessionTurnFrozenRoute
            {
                Status = SessionTurnRouteStatus.Ready,
                ProviderId = Canary,
                ModelId = Canary,
                DataDestination = Canary,
                SendsDataOffDevice = true,
                FrozenAtUtc = now
            },
            now);
        await store.UpdateTurnAsync(
            registration.Turn with
            {
                Phase = SessionTurnPhase.WaitingForMemoryOutboundConsent,
                TaskId = task.Id,
                MemoryOutboundState = MemoryOutboundConsentState.WaitingForMemoryOutboundConsent,
                ResultSummary = Canary,
                FailureMessage = Canary
            },
            registration.Turn.Version,
            now.AddSeconds(1));
        await host.Services.GetRequiredService<SessionCoordinator>().SetCurrentAsync(session.Id);

        var response = await GetSnapshotAsync(environment.Options.PipeName);
        await host.StopAsync();
        Assert.True(response.Success, response.Error?.Code);
        var raw = response.Payload!.Value.GetRawText();
        var presentation = Assert.Single(response.Payload.Value
            .Deserialize<BridgeSnapshotDto>(DesktopProtocolJson.Options)!.Presentations);
        Assert.Equal("WaitingUser", presentation.Phase);
        Assert.Equal("WaitingUser", presentation.IslandKind);
        Assert.Equal("需要你的确认", presentation.Title);
        Assert.Equal("请返回元枢主窗口继续。", presentation.Subtitle);
        Assert.True(presentation.RequiresUserAction);
        AssertNoSensitivePayload(raw, Canary);
        Assert.DoesNotContain("consent", raw, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("memory_outbound", raw, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("options", raw, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task WaitingTaskDoesNotExposeQuestionOptionsOrDecisionIdentifiers()
    {
        const string Canary = "PRIVATE_PERMISSION_QUESTION_OPTION_ID";
        await using var environment = DesktopHostTestEnvironment.Create();
        using var host = environment.BuildHost();
        await host.StartAsync();
        var (_, _, task) = await environment.SeedTaskAsync(Canary);
        await using (var store = await environment.OpenStoreAsync())
        {
            await store.TransitionTaskPhaseAsync(task.Id, TaskPhase.Routing, TaskEventSource.System, Canary);
            await store.TransitionTaskPhaseAsync(task.Id, TaskPhase.AwaitingPermission, TaskEventSource.System, Canary);
            await store.TransitionTaskAsync(task.Id, AgentTaskStatus.Running, TaskEventSource.System, Canary);
            await store.TransitionTaskAsync(task.Id, AgentTaskStatus.WaitingForUser, TaskEventSource.System, Canary);
        }
        var response = await GetSnapshotAsync(environment.Options.PipeName);
        await host.StopAsync();

        Assert.True(response.Success, response.Error?.Code);
        var raw = response.Payload!.Value.GetRawText();
        var presentation = Assert.Single(response.Payload.Value
            .Deserialize<BridgeSnapshotDto>(DesktopProtocolJson.Options)!.Presentations);
        Assert.Equal("WaitingUser", presentation.IslandKind);
        Assert.True(presentation.RequiresUserAction);
        Assert.Equal("AwaitingPermission", presentation.Indicator);
        AssertNoSensitivePayload(raw, Canary);
        Assert.DoesNotContain("question", raw, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("option", raw, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("decision_request", raw, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task SnapshotCapsPresentationsAt32AndKeepsLinkedTaskDeduplicated()
    {
        await using var environment = DesktopHostTestEnvironment.Create();
        var (device, project, _) = await environment.SeedTaskAsync("PRIVATE_TASK_0");
        await using (var store = await environment.OpenStoreAsync())
        {
            for (var index = 1; index < 33; index++)
            {
                var now = environment.TimeProvider.GetUtcNow();
                var command = new CommandRecord
                {
                    Id = Guid.NewGuid(),
                    SourceDeviceId = device.Id,
                    ProjectId = project.Id,
                    IdempotencyKey = $"bridge-cap-{Guid.NewGuid():N}",
                    CommandType = CommandType.CreateTask,
                    PayloadJson = "{}",
                    ReceivedAtUtc = now,
                    ExpiresAtUtc = now.AddMinutes(5),
                    Status = CommandStatus.Received
                };
                await store.RegisterCommandAsync(command);
                await store.CreateTaskAsync(new AgentTask
                {
                    Id = Guid.NewGuid(),
                    ProjectId = project.Id,
                    CreatedByDeviceId = device.Id,
                    Title = $"PRIVATE_TASK_{index}",
                    Instruction = $"PRIVATE_TASK_{index}",
                    WorkingDirectoryRelativePath = ".",
                    Executor = "codex",
                    Status = AgentTaskStatus.Pending,
                    CreatedAtUtc = now,
                    UpdatedAtUtc = now,
                    Version = 0
                }, command.Id);
            }
        }
        using var host = environment.BuildHost();
        await host.StartAsync();
        var response = await GetSnapshotAsync(environment.Options.PipeName);
        await host.StopAsync();

        Assert.True(response.Success, response.Error?.Code);
        var snapshot = response.Payload!.Value.Deserialize<BridgeSnapshotDto>(DesktopProtocolJson.Options)!;
        Assert.Equal(32, snapshot.Presentations.Count);
        Assert.Equal(32, snapshot.Presentations.Select(item => item.PresentationKey).Distinct().Count());
    }

    [Fact]
    public async Task EmptyAuthorityProducesEmptySnapshot()
    {
        await using var environment = DesktopHostTestEnvironment.Create();
        using var host = environment.BuildHost();
        await host.StartAsync();
        var response = await GetSnapshotAsync(environment.Options.PipeName);
        await host.StopAsync();
        Assert.True(response.Success, response.Error?.Code);
        Assert.Empty(response.Payload!.Value
            .Deserialize<BridgeSnapshotDto>(DesktopProtocolJson.Options)!.Presentations);
    }

    [Fact]
    public async Task HandshakeRejectsMalformedAndIncompatibleRequestsWithRedactedErrors()
    {
        const string Canary = "SENSITIVE_HANDSHAKE_CANARY";
        await using var environment = DesktopHostTestEnvironment.Create();
        using var host = environment.BuildHost();
        await host.StartAsync();

        var cases = new List<(DesktopApiResponse Response, string Code)>();
        cases.Add((await SendAsync(environment.Options.PipeName, DesktopApiMethods.BridgeHandshake,
            HandshakePayload(supported: ["bridge_snapshot_v1"], required: ["future_required_v1"])), "invalid_handshake"));
        cases.Add((await SendAsync(environment.Options.PipeName, DesktopApiMethods.BridgeHandshake,
            HandshakePayload(versions: [2])), "bridge_incompatible"));
        cases.Add((await SendAsync(environment.Options.PipeName, DesktopApiMethods.BridgeHandshake,
            HandshakePayload(supported: ["bridge_snapshot_v1", "future_required_v1"], required: ["future_required_v1"])), "bridge_incompatible"));
        cases.Add((await SendAsync(environment.Options.PipeName, DesktopApiMethods.BridgeHandshake,
            HandshakePayload(snapshotBytes: 0)), "invalid_handshake"));
        cases.Add((await SendAsync(environment.Options.PipeName, DesktopApiMethods.BridgeHandshake,
            HandshakePayload(clientInstanceId: Canary)), "invalid_handshake"));
        var unknown = HandshakePayload();
        unknown["unexpected_sensitive_field"] = Canary;
        cases.Add((await SendAsync(environment.Options.PipeName, DesktopApiMethods.BridgeHandshake, unknown), "invalid_handshake"));
        var unicode = HandshakePayload();
        unicode["client_product"] = "元枢岛";
        cases.Add((await SendAsync(environment.Options.PipeName, DesktopApiMethods.BridgeHandshake, unicode), "invalid_handshake"));
        var control = HandshakePayload();
        control["supported_capabilities"] = new[] { "bridge_snapshot_v1\0" };
        cases.Add((await SendAsync(environment.Options.PipeName, DesktopApiMethods.BridgeHandshake, control), "invalid_handshake"));
        var tooMany = HandshakePayload();
        tooMany["supported_capabilities"] = Enumerable.Range(0, 33).Select(index => $"optional_{index}").ToArray();
        cases.Add((await SendAsync(environment.Options.PipeName, DesktopApiMethods.BridgeHandshake, tooMany), "invalid_handshake"));
        await host.StopAsync();

        foreach (var (response, code) in cases)
        {
            AssertBridgeError(response, code, Canary);
        }
    }

    [Fact]
    public async Task SnapshotRequiresNegotiatedClientCapabilityAndExactServerInstance()
    {
        await using var environment = DesktopHostTestEnvironment.Create();
        using var host = environment.BuildHost();
        await host.StartAsync();
        var client = Guid.NewGuid();
        var handshake = await HandshakeAsync(environment.Options.PipeName, client);
        var unknownClient = await SendAsync(environment.Options.PipeName, DesktopApiMethods.BridgeSnapshotGet,
            SnapshotPayload(Guid.NewGuid(), handshake.ServerInstanceId));
        var wrongServer = await SendAsync(environment.Options.PipeName, DesktopApiMethods.BridgeSnapshotGet,
            SnapshotPayload(client, Guid.NewGuid().ToString("D")));

        var noCapabilityClient = Guid.NewGuid();
        var noCapabilityHandshake = await SendAsync(environment.Options.PipeName, DesktopApiMethods.BridgeHandshake,
            HandshakePayload(clientInstanceId: noCapabilityClient.ToString("D"), supported: [], required: []));
        var noCapabilityServer = noCapabilityHandshake.Payload!.Value
            .Deserialize<BridgeHandshakeResponseDto>(DesktopProtocolJson.Options)!;
        var bypass = await SendAsync(environment.Options.PipeName, DesktopApiMethods.BridgeSnapshotGet,
            SnapshotPayload(noCapabilityClient, noCapabilityServer.ServerInstanceId));
        await host.StopAsync();

        AssertBridgeError(unknownClient, "invalid_request", "canary");
        AssertBridgeError(wrongServer, "server_instance_mismatch", "canary");
        AssertBridgeError(bypass, "capability_not_enabled", "canary");
    }

    [Fact]
    public async Task SnapshotHonorsNegotiatedSizeAndServerInstanceChangesAfterRestart()
    {
        await using var environment = DesktopHostTestEnvironment.Create();
        string firstServer;
        using (var firstHost = environment.BuildHost())
        {
            await firstHost.StartAsync();
            var tinyClient = Guid.NewGuid();
            var tiny = await SendAsync(environment.Options.PipeName, DesktopApiMethods.BridgeHandshake,
                HandshakePayload(clientInstanceId: tinyClient.ToString("D"), snapshotBytes: 16));
            var tinyHandshake = tiny.Payload!.Value.Deserialize<BridgeHandshakeResponseDto>(DesktopProtocolJson.Options)!;
            var oversized = await SendAsync(environment.Options.PipeName, DesktopApiMethods.BridgeSnapshotGet,
                SnapshotPayload(tinyClient, tinyHandshake.ServerInstanceId));
            AssertBridgeError(oversized, "invalid_request", "canary");
            firstServer = (await HandshakeAsync(environment.Options.PipeName, Guid.NewGuid())).ServerInstanceId;
            Assert.Equal(firstServer, (await HandshakeAsync(environment.Options.PipeName, Guid.NewGuid())).ServerInstanceId);
            await firstHost.StopAsync();
        }
        using var secondHost = environment.BuildHost();
        await secondHost.StartAsync();
        var restarted = await HandshakeAsync(environment.Options.PipeName, Guid.NewGuid());
        await secondHost.StopAsync();
        Assert.NotEqual(firstServer, restarted.ServerInstanceId);
    }

    private static Dictionary<string, object?> HandshakePayload(
        int[]? versions = null,
        string? clientInstanceId = null,
        string[]? supported = null,
        string[]? required = null,
        int snapshotBytes = 1_048_576,
        int eventBatchBytes = 524_288,
        int eventsPerBatch = 100,
        int eventBytes = 32_768) => new(StringComparer.Ordinal)
        {
            ["bridge_protocol_versions"] = versions ?? [1],
            ["client_instance_id"] = clientInstanceId ?? Guid.NewGuid().ToString("D"),
            ["client_product"] = "yuanshu-island",
            ["client_product_version"] = "0.2.0",
            ["supported_capabilities"] = supported ?? ["bridge_snapshot_v1"],
            ["required_capabilities"] = required ?? ["bridge_snapshot_v1"],
            ["client_limits"] = new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["maximum_snapshot_bytes"] = snapshotBytes,
                ["maximum_event_batch_bytes"] = eventBatchBytes,
                ["maximum_events_per_batch"] = eventsPerBatch,
                ["maximum_event_bytes"] = eventBytes
            }
        };

    private static object SnapshotPayload(Guid clientInstanceId, string serverInstanceId) => new
    {
        bridge_protocol_version = 1,
        client_instance_id = clientInstanceId.ToString("D"),
        expected_server_instance_id = serverInstanceId
    };

    private static async Task<DesktopApiResponse> GetSnapshotAsync(string pipeName)
    {
        var client = Guid.NewGuid();
        var handshake = await HandshakeAsync(pipeName, client);
        return await SendAsync(pipeName, DesktopApiMethods.BridgeSnapshotGet,
            SnapshotPayload(client, handshake.ServerInstanceId));
    }

    private static void AssertNoSensitivePayload(string raw, string canary)
    {
        Assert.DoesNotContain(canary, raw, StringComparison.Ordinal);
        foreach (var field in new[]
                 {
                     "input_text", "file_path", "window_", "provider", "model", "evidence", "prompt",
                     "authorization", "conversation", "token", "api_key", "progress", "exception"
                 })
        {
            Assert.DoesNotContain(field, raw, StringComparison.OrdinalIgnoreCase);
        }
    }

    private static void AssertBridgeError(DesktopApiResponse response, string expectedCode, string canary)
    {
        Assert.False(response.Success);
        Assert.Equal(expectedCode, response.Error?.Code);
        Assert.DoesNotContain(canary,
            JsonSerializer.Serialize(response, DesktopProtocolJson.Options), StringComparison.Ordinal);
    }

    private static async Task<BridgeHandshakeResponseDto> HandshakeAsync(string pipeName, Guid client)
    {
        var response = await SendAsync(pipeName, DesktopApiMethods.BridgeHandshake,
            HandshakePayload(clientInstanceId: client.ToString("D")));
        Assert.True(response.Success, response.Error?.Code);
        return response.Payload!.Value.Deserialize<BridgeHandshakeResponseDto>(DesktopProtocolJson.Options)!;
    }

    private static async Task<DesktopApiResponse> SendAsync(
        string pipeName,
        string method,
        object payload,
        int protocolVersion = DesktopProtocolVersion.Current)
    {
        await using var pipe = new NamedPipeClientStream(
            ".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous,
            TokenImpersonationLevel.Identification);
        await pipe.ConnectAsync(3000);
        var requestId = Guid.NewGuid().ToString("N");
        await DesktopIpcFraming.WriteAsync(pipe,
            new DesktopApiRequest(requestId, method, DesktopProtocolJson.ToElement(payload), protocolVersion));
        var response = await DesktopIpcFraming.ReadAsync<DesktopApiResponse>(pipe);
        Assert.Equal(requestId, response.RequestId);
        return response;
    }
}
