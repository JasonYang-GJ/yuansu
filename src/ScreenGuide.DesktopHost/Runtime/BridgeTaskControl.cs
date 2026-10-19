using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;

namespace ScreenGuide.DesktopHost.Runtime;

public sealed partial class BridgeProtocolService
{
    private readonly SemaphoreSlim _cancelGate = new(1, 1);
    private readonly Dictionary<Guid, CancelReceipt> _cancelReceipts = new();
    private const int MaximumCancelReceipts = 4096;

    public async Task<BridgeCancelResult> CancelAsync(JsonElement payload, CancellationToken cancellationToken)
    {
        RequireObject(payload, ["bridge_protocol_version", "client_instance_id", "expected_server_instance_id",
            "presentation_key", "expected_version", "request_id"], "invalid_request");
        var version = ReadPositiveInt(payload, "bridge_protocol_version", "invalid_request");
        var clientId = ReadGuid(payload, "client_instance_id", "invalid_request");
        var serverId = ReadGuid(payload, "expected_server_instance_id", "invalid_request").ToString("D");
        var requestId = ReadGuid(payload, "request_id", "invalid_request");
        var key = ReadAsciiToken(payload, "presentation_key", "invalid_request");
        if (payload.GetProperty("expected_version").ValueKind != JsonValueKind.Number
            || !payload.GetProperty("expected_version").TryGetInt64(out var expectedVersion) || expectedVersion < 0)
            throw new BridgeProtocolException("invalid_request", "任务版本无效。");
        if (version != ProtocolVersion) throw BridgeProtocolException.Incompatible();
        if (serverId != serverInstance.Id) throw new BridgeProtocolException("server_instance_mismatch", "元枢已重启，请刷新后重试。");
        if (!_clients.TryGetValue(clientId, out var negotiation) || !negotiation.EnabledCapabilities.Contains(CancelCapability))
            throw new BridgeProtocolException("capability_not_enabled", "当前连接未启用任务取消。");

        await _cancelGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_cancelReceipts.TryGetValue(requestId, out var receipt))
            {
                if (receipt.ClientId != clientId || receipt.Key != key || receipt.Version != expectedVersion)
                    throw new BridgeProtocolException("request_conflict", "请求标识已用于其他任务。");
                if (receipt.ErrorCode is not null)
                    throw new BridgeProtocolException(receipt.ErrorCode, "上次取消未确认，请刷新任务状态。");
                return new BridgeCancelResult(true, true);
            }
            if (_cancelReceipts.Count >= MaximumCancelReceipts)
                throw new BridgeProtocolException("control_capacity_reached", "任务操作记录已满，请重新启动元枢后继续。");
            var snapshot = await snapshotProjection.BuildAsync(cancellationToken).ConfigureAwait(false);
            var target = snapshot.Presentations.SingleOrDefault(item => item.PresentationKey == key);
            if (target is null || target.IsTerminal)
                throw new BridgeProtocolException("target_not_active", "该任务已经结束或不在当前任务列表中。");
            if (target.Phase == "CancellationRequested")
                throw new BridgeProtocolException("task_not_cancellable", "任务正在取消中，请等待状态更新。");
            if (target.EntityVersion != expectedVersion)
                throw new BridgeProtocolException("entity_version_mismatch", "任务状态已变化，请查看最新状态后再操作。");

            // Record intent before executing. Never replay an ambiguous request after an exception.
            _cancelReceipts[requestId] = new CancelReceipt(clientId, key, expectedVersion, "cancel_unconfirmed");
            try
            {
                if (target.EntityKind == "turn")
                    await sessions.CancelTurnAsync(Guid.Parse(target.SessionId!), Guid.Parse(target.TurnId!),
                        cancellationToken, expectedVersion).ConfigureAwait(false);
                else
                {
                    var result = await tasks.CancelTaskAsync(Guid.Parse(target.TaskId!),
                        $"island-{serverId}-{clientId:D}-{requestId:D}",
                        cancellationToken, expectedVersion).ConfigureAwait(false);
                    if (!result.Accepted)
                    {
                        _cancelReceipts[requestId] = new CancelReceipt(clientId, key, expectedVersion, "task_not_cancellable");
                        throw new BridgeProtocolException("task_not_cancellable", "当前任务不能再次取消，请查看最新状态。");
                    }
                }
            }
            catch (InvalidOperationException exception) when (exception.Message is "entity_version_mismatch" or "request_conflict")
            {
                _cancelReceipts[requestId] = new CancelReceipt(clientId, key, expectedVersion, exception.Message);
                throw new BridgeProtocolException(exception.Message, "取消请求与当前任务状态不一致，请刷新后重试。");
            }
            _cancelReceipts[requestId] = new CancelReceipt(clientId, key, expectedVersion, null);
            logger.LogInformation("Island cancellation accepted for {EntityKind}; request {RequestId}.", target.EntityKind, requestId);
            return new BridgeCancelResult(true, false);
        }
        finally { _cancelGate.Release(); }
    }

    private sealed record CancelReceipt(Guid ClientId, string Key, long Version, string? ErrorCode);
    public sealed record BridgeCancelResult(
        [property: JsonPropertyName("accepted")] bool Accepted,
        [property: JsonPropertyName("duplicate")] bool Duplicate);
}
