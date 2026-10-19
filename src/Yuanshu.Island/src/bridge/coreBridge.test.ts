import { describe, expect, it } from "vitest";
import { createIslandCoordinator } from "../coordinator";
import {
  coreBridgePayloadToIslandEvent,
  coreBridgeStateToConnectionStatus,
  decodeCoreBridgeState,
} from "./coreBridge";

const serverId = "aaaaaaaa-aaaa-4aaa-8aaa-aaaaaaaaaaaa";
const sessionId = "22222222-2222-4222-8222-222222222222";
const turnId = "33333333-3333-4333-8333-333333333333";
const taskId = "44444444-4444-4444-8444-444444444444";
const snapshotId = "55555555-5555-4555-8555-555555555555";

function snapshotPayload(overrides: Record<string, unknown> = {}) {
  return {
    kind: "snapshot",
    snapshot: {
      bridge_protocol_version: 1,
      server_instance_id: serverId,
      snapshot_id: snapshotId,
      snapshot_sequence: 0,
      generated_at_utc: "2026-08-26T00:00:00.000Z",
      source: "yuanshu.core",
      presentations: [
        {
          presentation_key: `turn:${turnId}`,
          entity_kind: "turn",
          session_id: sessionId,
          turn_id: turnId,
          task_id: taskId,
          entity_version: 7,
          phase: "WaitingForConfirmation",
          island_kind: "WaitingUser",
          title: "需要你的确认",
          subtitle: "请返回元枢主窗口继续。",
          indicator: "WaitingForConfirmation",
          is_terminal: false,
          requires_user_action: true,
          updated_at_utc: "2026-08-26T00:00:00.000Z",
          actions: [],
        },
      ],
      ...overrides,
    },
  };
}

describe("Core Bridge decoder and adapter", () => {
  it("只从现有Snapshot/Clear真源投影已连接和未连接", () => {
    expect(coreBridgeStateToConnectionStatus(decodeCoreBridgeState(snapshotPayload())))
      .toBe("connected");
    expect(coreBridgeStateToConnectionStatus(decodeCoreBridgeState({ kind: "clear" })))
      .toBe("disconnected");
    expect(coreBridgeStateToConnectionStatus(decodeCoreBridgeState({ kind: "reconnecting" })))
      .toBe("disconnected");
  });

  it("将完整最小Snapshot映射成一个Coordinator原子替换事件", () => {
    const decoded = decodeCoreBridgeState(snapshotPayload());
    expect(decoded).toMatchObject({
      kind: "snapshot",
      snapshot: { server_instance_id: serverId, source: "yuanshu.core" },
    });

    expect(coreBridgePayloadToIslandEvent(snapshotPayload(), 1_000)).toEqual({
      type: "replace-source",
      source: "yuanshu.core",
      at: 1_000,
      presentations: [
        {
          kind: "WaitingUser",
          source: "yuanshu.core",
          key: `turn:${turnId}`,
          title: "需要你的确认",
          subtitle: "请返回元枢主窗口继续。",
          indicator: "WaitingForConfirmation",
          dismiss: { mode: "persistent" },
          metadata: {
            serverInstanceId: serverId,
            snapshotId,
            entityKind: "turn",
            sessionId,
            turnId,
            taskId,
            entityVersion: 7,
            phase: "WaitingForConfirmation",
            updatedAtUtc: "2026-08-26T00:00:00.000Z",
          },
        },
      ],
    });
  });

  it("断线和畸形Snapshot只清理Core来源并保留Media", () => {
    const coordinator = createIslandCoordinator({ clock: () => 10_000 });
    coordinator.dispatch({
      type: "upsert",
      at: 100,
      presentation: {
        kind: "Media",
        source: "windows.media",
        key: "playing",
        title: "本地媒体",
      },
    });
    coordinator.dispatch(coreBridgePayloadToIslandEvent(snapshotPayload(), 200));
    expect(coordinator.getSnapshot().active.kind).toBe("WaitingUser");

    const malformed = snapshotPayload({ source: "forged.source" });
    coordinator.dispatch(coreBridgePayloadToIslandEvent(malformed, 300));
    expect(coordinator.getSnapshot()).toMatchObject({
      active: { kind: "Media", source: "windows.media" },
      retainedCount: 1,
    });

    coordinator.dispatch(coreBridgePayloadToIslandEvent({ kind: "clear" }, 400));
    expect(coordinator.getSnapshot()).toMatchObject({
      active: { kind: "Media", source: "windows.media" },
      retainedCount: 1,
    });
    coordinator.destroy();
  });

  it("拒绝额外敏感字段、非规范ID、控制字符、非NFC、进度和非空动作", () => {
    const base = snapshotPayload();
    const presentation = (base.snapshot.presentations as Array<Record<string, unknown>>)[0];
    const invalidPayloads = [
      snapshotPayload({ input_text: "secret" }),
      snapshotPayload({ server_instance_id: serverId.toUpperCase() }),
      snapshotPayload({ presentations: [{ ...presentation, title: "伪装\u202e标题" }] }),
      snapshotPayload({ presentations: [{ ...presentation, title: "e\u0301" }] }),
      snapshotPayload({ presentations: [{ ...presentation, progress: 35 }] }),
      snapshotPayload({
        presentations: [{ ...presentation, actions: [{ id: "approve", label: "批准" }] }],
      }),
      snapshotPayload({ presentations: Array.from({ length: 33 }, () => presentation) }),
    ];

    for (const payload of invalidPayloads) {
      expect(decodeCoreBridgeState(payload)).toBeNull();
      expect(coreBridgePayloadToIslandEvent(payload, 500)).toEqual({
        type: "replace-source",
        source: "yuanshu.core",
        at: 500,
        presentations: [],
      });
    }
  });
});
