import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import { createIslandCoordinator } from "../coordinator";
import {
  WINDOWS_VOLUME_EVENT_ID,
  WINDOWS_VOLUME_KEY,
  WINDOWS_VOLUME_SOURCE,
  WINDOWS_VOLUME_TTL_MS,
  windowsVolumeToIslandEvent,
  type WindowsVolumePayload,
} from "./windowsVolume";

function volumePayload(
  volume: number,
  muted = false,
  timestampMs = Date.now(),
): WindowsVolumePayload {
  return {
    volume,
    muted,
    source: WINDOWS_VOLUME_SOURCE,
    key: WINDOWS_VOLUME_KEY,
    eventId: WINDOWS_VOLUME_EVENT_ID,
    timestampMs,
  };
}

function dispatchVolume(
  coordinator: ReturnType<typeof createIslandCoordinator>,
  volume: number,
  muted = false,
) {
  const event = windowsVolumeToIslandEvent(volumePayload(volume, muted));
  expect(event).not.toBeNull();
  coordinator.dispatch(event!);
}

describe("Windows Volume Provider 到 Island Coordinator 的边界", () => {
  beforeEach(() => {
    vi.useFakeTimers();
    vi.setSystemTime(new Date(1_725_000_000_000));
  });

  afterEach(() => {
    vi.useRealTimers();
  });

  it("只接收完整的 0-100 本地音量事件并保留稳定身份与时间信息", () => {
    expect(windowsVolumeToIslandEvent(volumePayload(42, true))).toEqual({
      type: "upsert",
      presentation: expect.objectContaining({
        kind: "SystemEvent",
        source: WINDOWS_VOLUME_SOURCE,
        key: WINDOWS_VOLUME_KEY,
        title: "音量",
        subtitle: "Muted",
        indicator: "Muted",
        category: "volume",
        dismiss: { mode: "auto", durationMs: WINDOWS_VOLUME_TTL_MS },
        metadata: {
          volume: 42,
          muted: true,
          eventId: WINDOWS_VOLUME_EVENT_ID,
          timestampMs: 1_725_000_000_000,
        },
      }),
    });
    expect(windowsVolumeToIslandEvent({ ...volumePayload(42), volume: 101 })).toBeNull();
    expect(windowsVolumeToIslandEvent({ ...volumePayload(42), source: "other" })).toBeNull();
    expect(windowsVolumeToIslandEvent({ ...volumePayload(42), timestampMs: -1 })).toBeNull();
  });

  it("临时覆盖 Media，TTL 到期后恢复 Media", () => {
    const coordinator = createIslandCoordinator();
    coordinator.dispatch({
      type: "upsert",
      presentation: {
        kind: "Media",
        source: "media-test",
        key: "playing",
        title: "正在播放",
      },
    });

    dispatchVolume(coordinator, 64);
    expect(coordinator.getSnapshot()).toMatchObject({
      active: { kind: "SystemEvent", source: WINDOWS_VOLUME_SOURCE, key: WINDOWS_VOLUME_KEY },
      retainedCount: 2,
    });

    vi.advanceTimersByTime(WINDOWS_VOLUME_TTL_MS);
    expect(coordinator.getSnapshot()).toMatchObject({
      active: { kind: "Media", key: "playing" },
      retainedCount: 1,
    });
    coordinator.destroy();
  });

  it("普通 Volume 不能覆盖 WaitingUser", () => {
    const coordinator = createIslandCoordinator();
    coordinator.dispatch({
      type: "upsert",
      presentation: {
        kind: "WaitingUser",
        source: "yuanshu",
        key: "decision",
        title: "需要确认",
      },
    });

    dispatchVolume(coordinator, 80);
    expect(coordinator.getSnapshot()).toMatchObject({
      active: { kind: "WaitingUser", key: "decision" },
      retainedCount: 2,
    });
    vi.advanceTimersByTime(WINDOWS_VOLUME_TTL_MS);
    expect(coordinator.getSnapshot()).toMatchObject({
      active: { kind: "WaitingUser" },
      retainedCount: 1,
    });
    coordinator.destroy();
  });

  it("从 Idle 唤醒并在 TTL 到期后恢复 Idle", () => {
    const coordinator = createIslandCoordinator();
    dispatchVolume(coordinator, 25);
    expect(coordinator.getSnapshot().active.kind).toBe("SystemEvent");

    vi.advanceTimersByTime(WINDOWS_VOLUME_TTL_MS);
    expect(coordinator.getSnapshot()).toMatchObject({
      active: { kind: "Idle" },
      retainedCount: 0,
    });
    coordinator.destroy();
  });

  it("快速连续变化只更新一个固定 Volume 节点", () => {
    const coordinator = createIslandCoordinator();

    for (let volume = 0; volume <= 100; volume += 2) {
      vi.setSystemTime(new Date(1_725_000_000_000 + volume));
      dispatchVolume(coordinator, volume);
    }

    expect(coordinator.getSnapshot()).toMatchObject({
      active: {
        kind: "SystemEvent",
        source: WINDOWS_VOLUME_SOURCE,
        key: WINDOWS_VOLUME_KEY,
        subtitle: "100%",
        metadata: { volume: 100, muted: false },
      },
      retainedCount: 1,
    });
    coordinator.destroy();
  });
});
