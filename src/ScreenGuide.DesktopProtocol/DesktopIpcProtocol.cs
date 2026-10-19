using System.Buffers.Binary;
using System.IO.Pipes;
using System.Security.Principal;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace ScreenGuide.DesktopProtocol;

public static class DesktopProtocolVersion
{
    public const int Current = 12;

    public const int MinimumSupported = 12;
}

public static class DesktopApiMethods
{
    public const string Ping = "system.ping";
    public const string GetSystemStatus = "system.status";
    public const string GetDashboard = "dashboard.get";
    public const string ListProjects = "projects.list";
    public const string AddProject = "projects.add";
    public const string RevokeProject = "projects.revoke";
    public const string ListTasks = "tasks.list";
    public const string GetTask = "tasks.get";
    public const string CreateTask = "tasks.create";
    public const string CancelTask = "tasks.cancel";
    public const string ContinueTask = "tasks.continue";
    public const string ClearHistory = "tasks.clear-history";
    public const string ListDesktopApplications = "desktop.applications.list";
    public const string ExecuteDesktopAction = "desktop.action.execute";
    public const string PlanAssistantCommand = "assistant.command.plan";
    public const string ExecuteAssistantCommand = "assistant.command.execute";
    public const string CancelWindowObservation = "assistant.window-observation.cancel";
    public const string PreparePointerRegion = "assistant.pointer-region.prepare";
    public const string ReadPointerRegion = "assistant.pointer-region.read";
    public const string CancelPointerRegion = "assistant.pointer-region.cancel";
    public const string ListConversations = "conversations.list";
    public const string GetConversation = "conversations.get";
    public const string CreateConversation = "conversations.create";
    public const string SendConversationMessage = "conversations.send";
    public const string CancelConversationTurn = "conversations.cancel";
    public const string GetCurrentSession = "sessions.current";
    public const string StartNewSession = "sessions.new";
    public const string SetCurrentSession = "sessions.select";
    public const string SubmitSessionInput = "sessions.submit";
    public const string ConfirmMemoryOutbound = "sessions.memory-outbound.confirm";
    public const string ConfirmPointerAnswer = "sessions.pointer-answer.confirm";
    public const string ProvideSessionProject = "sessions.context.project";
    public const string ProvideSessionFile = "sessions.context.file";
    public const string RespondSessionWindowConsent = "sessions.context.window-consent";
    public const string RetrySessionTurn = "sessions.context.retry";
    public const string ConfirmSessionTurn = "sessions.turn.confirm";
    public const string CancelSessionTurn = "sessions.turn.cancel";
    public const string WaitForSessionUpdate = "sessions.wait";
    public const string GetSessionMessagesPage = "sessions.messages.page";
    public const string GetSessionTurn = "sessions.turn.get";
    public const string GetAiSettings = "ai.settings.get";
    public const string SetChatRoute = "ai.chat-route.set";
    public const string SetProviderCredential = "ai.credentials.set";
    public const string DeleteProviderCredential = "ai.credentials.delete";
    public const string CheckAiProviderHealth = "ai.provider.health";
    public const string ListMemories = "memory.list";
    public const string PreviewMemories = "memory.preview";
    public const string GetMemory = "memory.get";
    public const string CreateMemory = "memory.create";
    public const string UpdateMemory = "memory.update";
    public const string SetMemoryEnabled = "memory.set-enabled";
    public const string DeleteMemory = "memory.delete";
    public const string BridgeHandshake = "bridge.handshake";
    public const string BridgeSnapshotGet = "bridge.snapshot.get";
    public const string BridgeCancel = "bridge.task.cancel";
    public const string Shutdown = "system.shutdown";
}

public static class DesktopIpcEndpoint
{
    public static string CurrentUserPipeName()
    {
        var sid = WindowsIdentity.GetCurrent().User?.Value
            ?? throw new InvalidOperationException("无法识别当前 Windows 用户。");
        return $"ScreenGuide.DesktopHost.V01.{sid.Replace('-', '_')}";
    }

    public static string CurrentUserMutexName(string component)
    {
        var sid = WindowsIdentity.GetCurrent().User?.Value
            ?? throw new InvalidOperationException("无法识别当前 Windows 用户。");
        return $"Local\\ScreenGuide.{component}.V01.{sid.Replace('-', '_')}";
    }
}

public static class DesktopProtocolJson
{
    public static JsonSerializerOptions Options { get; } = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() }
    };

    public static JsonElement ToElement<T>(T value) =>
        JsonSerializer.SerializeToElement(value, Options);
}

public sealed record DesktopApiRequest(
    string RequestId,
    string Method,
    JsonElement Payload,
    int ProtocolVersion = DesktopProtocolVersion.Current);

public sealed record DesktopApiError(string Code, string UserMessage, string? TechnicalDetail = null);

public sealed record DesktopApiResponse(
    string RequestId,
    bool Success,
    JsonElement? Payload = null,
    DesktopApiError? Error = null,
    int ProtocolVersion = DesktopProtocolVersion.Current);

public sealed class DesktopApiException(DesktopApiError error) : Exception(error.UserMessage)
{
    public DesktopApiError Error { get; } = error;
}

public static class DesktopIpcFraming
{
    public const int MaximumMessageBytes = 4 * 1024 * 1024;

    public static async Task WriteAsync<T>(
        PipeStream stream,
        T message,
        CancellationToken cancellationToken = default)
    {
        var payload = JsonSerializer.SerializeToUtf8Bytes(message, DesktopProtocolJson.Options);
        if (payload.Length > MaximumMessageBytes)
        {
            throw new InvalidDataException("IPC 消息超过大小限制。");
        }

        var header = new byte[sizeof(int)];
        BinaryPrimitives.WriteInt32LittleEndian(header, payload.Length);
        await stream.WriteAsync(header, cancellationToken).ConfigureAwait(false);
        await stream.WriteAsync(payload, cancellationToken).ConfigureAwait(false);
        await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
    }

    public static async Task<T> ReadAsync<T>(
        PipeStream stream,
        CancellationToken cancellationToken = default)
    {
        var header = new byte[sizeof(int)];
        await stream.ReadExactlyAsync(header, cancellationToken).ConfigureAwait(false);
        var length = BinaryPrimitives.ReadInt32LittleEndian(header);
        if (length <= 0 || length > MaximumMessageBytes)
        {
            throw new InvalidDataException("IPC 消息长度无效。");
        }

        var payload = new byte[length];
        await stream.ReadExactlyAsync(payload, cancellationToken).ConfigureAwait(false);
        return JsonSerializer.Deserialize<T>(payload, DesktopProtocolJson.Options)
            ?? throw new InvalidDataException("IPC 消息内容无效。");
    }
}
