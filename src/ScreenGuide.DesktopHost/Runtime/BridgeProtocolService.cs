using System.Collections.Concurrent;
using System.Globalization;
using System.Reflection;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using ScreenGuide.DesktopProtocol;

namespace ScreenGuide.DesktopHost.Runtime;

public sealed class BridgeServerInstance
{
    public string Id { get; } = Guid.NewGuid().ToString("D");
}

public sealed partial class BridgeProtocolService(
    BridgeServerInstance serverInstance,
    BridgeSnapshotProjection snapshotProjection,
    TimeProvider timeProvider,
    ILogger<BridgeProtocolService> logger,
    SessionCoordinator sessions,
    LocalTaskEntryService tasks)
{
    public const int ProtocolVersion = 1;
    public const string SnapshotCapability = "bridge_snapshot_v1";
    public const string CancelCapability = "island_cancel_v1";
    private const int MaximumCollectionItems = 32;
    private const int MaximumAsciiTokenLength = 64;
    private const int MaximumSnapshotBytes = 1024 * 1024;
    private const int MaximumEventBatchBytes = 512 * 1024;
    private const int MaximumEventsPerBatch = 100;
    private const int MaximumEventBytes = 32 * 1024;
    private const int MaximumNegotiatedClients = 256;
    private static readonly string[] ImplementedCapabilities = [SnapshotCapability, CancelCapability];
    private static readonly IReadOnlyDictionary<string, string> DisabledCapabilities =
        new SortedDictionary<string, string>(StringComparer.Ordinal)
        {
            ["bridge_events_v1"] = "not_implemented",
            ["permission_requests_v1"] = "not_implemented",
            ["tool_execution_v1"] = "not_exposed",
            ["turn_cancel_v1"] = "not_implemented",
            ["task_cancel_v1"] = "not_implemented",
            ["turn_retry_v1"] = "not_implemented",
            ["ui_activation_v1"] = "not_exposed",
            ["voice_listening_v1"] = "not_exposed"
        };
    private readonly ConcurrentDictionary<Guid, BridgeClientNegotiation> _clients = new();
    private readonly object _clientsGate = new();

    public BridgeHandshakeResponseDto Handshake(JsonElement payload)
    {
        var request = ParseHandshake(payload);
        if (!request.ProtocolVersions.Contains(ProtocolVersion))
        {
            throw BridgeProtocolException.Incompatible();
        }

        if (!request.RequiredCapabilities.All(request.SupportedCapabilities.Contains))
        {
            throw BridgeProtocolException.InvalidHandshake();
        }

        var enabled = request.SupportedCapabilities
            .Where(capability => ImplementedCapabilities.Contains(capability, StringComparer.Ordinal))
            .Order(StringComparer.Ordinal)
            .ToArray();
        if (!request.RequiredCapabilities.All(enabled.Contains))
        {
            throw BridgeProtocolException.Incompatible();
        }

        var limits = new BridgeLimitsDto(
            DesktopIpcFraming.MaximumMessageBytes,
            Math.Min(request.MaximumSnapshotBytes, MaximumSnapshotBytes),
            Math.Min(request.MaximumEventBatchBytes, MaximumEventBatchBytes),
            Math.Min(request.MaximumEventsPerBatch, MaximumEventsPerBatch),
            Math.Min(request.MaximumEventBytes, MaximumEventBytes));
        var negotiation = new BridgeClientNegotiation(
            limits,
            enabled.ToHashSet(StringComparer.Ordinal));
        bool added;
        lock (_clientsGate)
        {
            if (!_clients.ContainsKey(request.ClientInstanceId)
                && _clients.Count >= MaximumNegotiatedClients)
            {
                var expiredClient = _clients.Keys.First();
                _clients.TryRemove(expiredClient, out _);
            }
            added = _clients.TryAdd(request.ClientInstanceId, negotiation);
            if (!added)
            {
                _clients[request.ClientInstanceId] = negotiation;
            }
        }
        if (added)
        {
            logger.LogInformation(
                "Bridge client {ClientProduct} {ClientVersion} negotiated protocol {ProtocolVersion} and capabilities {Capabilities}.",
                request.ClientProduct,
                request.ClientProductVersion,
                ProtocolVersion,
                string.Join(',', enabled));
        }
        return new BridgeHandshakeResponseDto(
            ProtocolVersion,
            serverInstance.Id,
            "yuanshu-core",
            ProductVersion(),
            DesktopProtocolVersion.Current,
            enabled,
            DisabledCapabilities,
            new Dictionary<string, JsonElement>(StringComparer.Ordinal),
            limits,
            FormatTimestamp(timeProvider.GetUtcNow()));
    }

    public async Task<BridgeSnapshotDto> GetSnapshotAsync(
        JsonElement payload,
        CancellationToken cancellationToken)
    {
        var request = ParseSnapshotRequest(payload);
        if (request.ProtocolVersion != ProtocolVersion)
        {
            throw BridgeProtocolException.Incompatible();
        }
        if (!string.Equals(request.ExpectedServerInstanceId, serverInstance.Id, StringComparison.Ordinal))
        {
            throw new BridgeProtocolException(
                "server_instance_mismatch",
                "Core 已重新启动，请重新连接。");
        }
        if (!_clients.TryGetValue(request.ClientInstanceId, out var negotiation))
        {
            throw new BridgeProtocolException("invalid_request", "请先完成 Bridge Handshake。");
        }
        if (!negotiation.EnabledCapabilities.Contains(SnapshotCapability))
        {
            throw new BridgeProtocolException(
                "capability_not_enabled",
                "当前 Bridge 客户端未协商 Snapshot 能力。");
        }

        var snapshot = await snapshotProjection.BuildAsync(cancellationToken).ConfigureAwait(false);
        if (JsonSerializer.SerializeToUtf8Bytes(snapshot, DesktopProtocolJson.Options).Length
            > negotiation.Limits.SnapshotBytes)
        {
            throw new BridgeProtocolException("invalid_request", "Bridge Snapshot 超过协商限制。");
        }
        return snapshot;
    }

    private static HandshakeRequest ParseHandshake(JsonElement payload)
    {
        RequireObject(
            payload,
            [
                "bridge_protocol_versions",
                "client_instance_id",
                "client_product",
                "client_product_version",
                "supported_capabilities",
                "required_capabilities",
                "client_limits"
            ],
            "invalid_handshake");
        var versions = ReadPositiveIntArray(payload, "bridge_protocol_versions");
        var clientId = ReadGuid(payload, "client_instance_id", "invalid_handshake");
        var clientProduct = ReadAsciiToken(payload, "client_product", "invalid_handshake");
        var clientProductVersion = ReadAsciiToken(
            payload,
            "client_product_version",
            "invalid_handshake");
        var supported = ReadCapabilities(payload, "supported_capabilities");
        var required = ReadCapabilities(payload, "required_capabilities");
        var limits = payload.GetProperty("client_limits");
        RequireObject(
            limits,
            [
                "maximum_snapshot_bytes",
                "maximum_event_batch_bytes",
                "maximum_events_per_batch",
                "maximum_event_bytes"
            ],
            "invalid_handshake");
        return new HandshakeRequest(
            versions,
            clientId,
            clientProduct,
            clientProductVersion,
            supported,
            required,
            ReadPositiveInt(limits, "maximum_snapshot_bytes"),
            ReadPositiveInt(limits, "maximum_event_batch_bytes"),
            ReadPositiveInt(limits, "maximum_events_per_batch"),
            ReadPositiveInt(limits, "maximum_event_bytes"));
    }

    private static SnapshotRequest ParseSnapshotRequest(JsonElement payload)
    {
        RequireObject(
            payload,
            ["bridge_protocol_version", "client_instance_id", "expected_server_instance_id"],
            "invalid_request");
        return new SnapshotRequest(
            ReadPositiveInt(payload, "bridge_protocol_version", "invalid_request"),
            ReadGuid(payload, "client_instance_id", "invalid_request"),
            ReadGuid(payload, "expected_server_instance_id", "invalid_request").ToString("D"));
    }

    private static void RequireObject(JsonElement value, string[] fields, string errorCode)
    {
        if (value.ValueKind != JsonValueKind.Object)
        {
            throw new BridgeProtocolException(errorCode, "Bridge 请求格式无效。");
        }
        var allowed = fields.ToHashSet(StringComparer.Ordinal);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var property in value.EnumerateObject())
        {
            if (!allowed.Contains(property.Name) || !seen.Add(property.Name))
            {
                throw new BridgeProtocolException(errorCode, "Bridge 请求字段无效。");
            }
        }
        if (seen.Count != fields.Length)
        {
            throw new BridgeProtocolException(errorCode, "Bridge 请求缺少必需字段。");
        }
    }

    private static int[] ReadPositiveIntArray(JsonElement value, string name)
    {
        var property = value.GetProperty(name);
        if (property.ValueKind != JsonValueKind.Array)
        {
            throw BridgeProtocolException.InvalidHandshake();
        }
        var result = property.EnumerateArray().Select(item => ReadPositiveIntValue(item)).ToArray();
        if (result.Length is 0 or > MaximumCollectionItems || result.Distinct().Count() != result.Length)
        {
            throw BridgeProtocolException.InvalidHandshake();
        }
        return result;
    }

    private static HashSet<string> ReadCapabilities(JsonElement value, string name)
    {
        var property = value.GetProperty(name);
        if (property.ValueKind != JsonValueKind.Array)
        {
            throw BridgeProtocolException.InvalidHandshake();
        }
        var result = new HashSet<string>(StringComparer.Ordinal);
        var count = 0;
        foreach (var item in property.EnumerateArray())
        {
            count++;
            if (item.ValueKind != JsonValueKind.String || !IsAsciiToken(item.GetString()))
            {
                throw BridgeProtocolException.InvalidHandshake();
            }
            if (!result.Add(item.GetString()!))
            {
                throw BridgeProtocolException.InvalidHandshake();
            }
        }
        if (count > MaximumCollectionItems)
        {
            throw BridgeProtocolException.InvalidHandshake();
        }
        return result;
    }

    private static string ReadAsciiToken(JsonElement value, string name, string errorCode)
    {
        var property = value.GetProperty(name);
        var text = property.ValueKind == JsonValueKind.String ? property.GetString() : null;
        if (!IsAsciiToken(text))
        {
            throw new BridgeProtocolException(errorCode, "Bridge 标识无效。");
        }
        return text!;
    }

    private static bool IsAsciiToken(string? value) =>
        !string.IsNullOrEmpty(value)
        && value.Length <= MaximumAsciiTokenLength
        && value.All(character =>
            character is >= 'A' and <= 'Z'
            or >= 'a' and <= 'z'
            or >= '0' and <= '9'
            or '.' or '_' or ':' or '-');

    private static string ProductVersion()
    {
        var assembly = typeof(BridgeProtocolService).Assembly;
        var productVersion = assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?
            .InformationalVersion;
        return string.IsNullOrWhiteSpace(productVersion)
            ? assembly.GetName().Version?.ToString() ?? "unknown"
            : productVersion;
    }

    private static string FormatTimestamp(DateTimeOffset value) =>
        value.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", CultureInfo.InvariantCulture);

    private static Guid ReadGuid(JsonElement value, string name, string errorCode)
    {
        var property = value.GetProperty(name);
        var text = property.ValueKind == JsonValueKind.String ? property.GetString() : null;
        if (text is null || !Guid.TryParseExact(text, "D", out var guid) || guid == Guid.Empty
            || !string.Equals(text, guid.ToString("D"), StringComparison.Ordinal))
        {
            throw new BridgeProtocolException(errorCode, "Bridge 标识无效。");
        }
        return guid;
    }

    private static int ReadPositiveInt(JsonElement value, string name, string errorCode = "invalid_handshake") =>
        ReadPositiveIntValue(value.GetProperty(name), errorCode);

    private static int ReadPositiveIntValue(JsonElement value, string errorCode = "invalid_handshake")
    {
        if (value.ValueKind != JsonValueKind.Number || !value.TryGetInt32(out var result) || result <= 0)
        {
            throw new BridgeProtocolException(errorCode, "Bridge 限制必须是正整数。");
        }
        return result;
    }

    private sealed record HandshakeRequest(
        int[] ProtocolVersions,
        Guid ClientInstanceId,
        string ClientProduct,
        string ClientProductVersion,
        HashSet<string> SupportedCapabilities,
        HashSet<string> RequiredCapabilities,
        int MaximumSnapshotBytes,
        int MaximumEventBatchBytes,
        int MaximumEventsPerBatch,
        int MaximumEventBytes);

    private sealed record SnapshotRequest(
        int ProtocolVersion,
        Guid ClientInstanceId,
        string ExpectedServerInstanceId);

    private sealed record BridgeClientNegotiation(
        BridgeLimitsDto Limits,
        IReadOnlySet<string> EnabledCapabilities);
}

internal sealed class BridgeProtocolException(string code, string userMessage) : Exception(userMessage)
{
    public string Code { get; } = code;

    public static BridgeProtocolException InvalidHandshake() =>
        new("invalid_handshake", "Bridge Handshake 请求无效。");

    public static BridgeProtocolException Incompatible() =>
        new("bridge_incompatible", "Bridge 协议或必需能力不兼容。");
}
