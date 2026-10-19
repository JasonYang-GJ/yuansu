import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import { createIslandCoordinator } from "../coordinator";
import {
  WINDOWS_MEDIA_APP_ID_LIMIT,
  WINDOWS_MEDIA_ARTIST_LIMIT,
  WINDOWS_MEDIA_EVENT_ID,
  WINDOWS_MEDIA_KEY,
  WINDOWS_MEDIA_SOURCE,
  WINDOWS_MEDIA_TITLE_LIMIT,
  windowsMediaToIslandEvent,
  type WindowsMediaClearPayload,
  type WindowsMediaUpdatePayload,
} from "./windowsMedia";
import {
  WINDOWS_VOLUME_EVENT_ID,
  WINDOWS_VOLUME_KEY,
  WINDOWS_VOLUME_SOURCE,
  WINDOWS_VOLUME_TTL_MS,
  windowsVolumeToIslandEvent,
} from "./windowsVolume";

function mediaPayload(
  overrides: Partial<WindowsMediaUpdatePayload> = {},
): WindowsMediaUpdatePayload {
  return {
    kind: "update",
    title: "测试歌曲",
    artist: "测试艺术家",
    playbackState: "playing",
    appId: "test.player",
    source: WINDOWS_MEDIA_SOURCE,
    key: WINDOWS_MEDIA_KEY,
    eventId: WINDOWS_MEDIA_EVENT_ID,
    timestampMs: Date.now(),
    ...overrides,
  };
}

function clearPayload(timestampMs = Date.now()): WindowsMediaClearPayload {
  return {
    kind: "clear",
    source: WINDOWS_MEDIA_SOURCE,
    key: WINDOWS_MEDIA_KEY,
    eventId: WINDOWS_MEDIA_EVENT_ID,
    timestampMs,
  };
}

function dispatchMedia(
  coordinator: ReturnType<typeof createIslandCoordinator>,
  payload: WindowsMediaUpdatePayload,
) {
  const event = windowsMediaToIslandEvent(payload);
  expect(event).not.toBeNull();
  coordinator.dispatch(event!);
}

describe("Windows Media Provider 到 Island Coordinator 的边界", () => {
  beforeEach(() => {
    vi.useFakeTimers();
    vi.setSystemTime(new Date(1_725_000_000_000));
  });

  afterEach(() => {
    vi.useRealTimers();
  });

  it("只接收固定身份、有限字符串与 Playing/Paused 状态", () => {
    expect(windowsMediaToIslandEvent(mediaPayload())).toEqual({
      type: "upsert",
      at: 1_725_000_000_000,
      presentation: {
        kind: "Media",
        source: WINDOWS_MEDIA_SOURCE,
        key: WINDOWS_MEDIA_KEY,
        title: "测试歌曲",
        subtitle: "测试艺术家",
        indicator: "Playing",
        dismiss: { mode: "persistent" },
        metadata: {
          artist: "测试艺术家",
          playbackState: "playing",
          appId: "test.player",
          eventId: WINDOWS_MEDIA_EVENT_ID,
          timestampMs: 1_725_000_000_000,
        },
      },
    });
    expect(windowsMediaToIslandEvent(mediaPayload({ playbackState: "paused" })))
      .toMatchObject({ presentation: { indicator: "Paused" } });
    expect(windowsMediaToIslandEvent({ ...mediaPayload(), source: "other" })).toBeNull();
    expect(windowsMediaToIslandEvent({ ...mediaPayload(), title: "" })).toBeNull();
    expect(windowsMediaToIslandEvent({ ...mediaPayload(), title: "伪装\u202e标题" })).toBeNull();
    expect(windowsMediaToIslandEvent({ ...mediaPayload(), title: "歌".repeat(WINDOWS_MEDIA_TITLE_LIMIT + 1) })).toBeNull();
    expect(windowsMediaToIslandEvent({ ...mediaPayload(), artist: "人".repeat(WINDOWS_MEDIA_ARTIST_LIMIT + 1) })).toBeNull();
    expect(windowsMediaToIslandEvent({ ...mediaPayload(), appId: "a".repeat(WINDOWS_MEDIA_APP_ID_LIMIT + 1) })).toBeNull();
    expect(windowsMediaToIslandEvent({ ...mediaPayload(), playbackState: "stopped" })).toBeNull();
    expect(windowsMediaToIslandEvent({ ...mediaPayload(), timestampMs: Number.MAX_SAFE_INTEGER + 1 })).toBeNull();
  });

  it("同源固定键切歌、暂停、恢复只原位更新一个 Media 节点", () => {
    const coordinator = createIslandCoordinator();
    for (let index = 0; index < 100; index += 1) {
      vi.setSystemTime(new Date(1_725_000_000_000 + index));
      dispatchMedia(coordinator, mediaPayload({
        title: `歌曲 ${index}`,
        playbackState: index % 2 === 0 ? "playing" : "paused",
        timestampMs: Date.now(),
      }));
    }

    expect(coordinator.getSnapshot()).toMatchObject({
      active: {
        kind: "Media",
        source: WINDOWS_MEDIA_SOURCE,
        key: WINDOWS_MEDIA_KEY,
        title: "歌曲 99",
        indicator: "Paused",
      },
      retainedCount: 1,
    });
    coordinator.destroy();
  });

  it("真实链路语义：Volume 临时覆盖后恢复最新 Media", () => {
    const coordinator = createIslandCoordinator();
    dispatchMedia(coordinator, mediaPayload({ title: "第一首" }));
    dispatchMedia(coordinator, mediaPayload({ title: "第二首", timestampMs: Date.now() + 1 }));
    const volume = windowsVolumeToIslandEvent({
      volume: 63,
      muted: false,
      source: WINDOWS_VOLUME_SOURCE,
      key: WINDOWS_VOLUME_KEY,
      eventId: WINDOWS_VOLUME_EVENT_ID,
      timestampMs: Date.now() + 2,
    });
    coordinator.dispatch(volume!);
    expect(coordinator.getSnapshot().active.kind).toBe("SystemEvent");
    vi.advanceTimersByTime(WINDOWS_VOLUME_TTL_MS);
    expect(coordinator.getSnapshot()).toMatchObject({
      active: { kind: "Media", title: "第二首" },
      retainedCount: 1,
    });
    coordinator.destroy();
  });

  it("WaitingUser 不被普通 Media 抢占，解除后恢复最新 Media", () => {
    const coordinator = createIslandCoordinator();
    coordinator.dispatch({
      type: "upsert",
      presentation: {
        kind: "WaitingUser",
        source: "simulator",
        key: "decision",
        title: "需要确认",
      },
    });
    dispatchMedia(coordinator, mediaPayload());
    expect(coordinator.getSnapshot()).toMatchObject({
      active: { kind: "WaitingUser" },
      retainedCount: 2,
    });
    coordinator.dispatch({ type: "remove", source: "simulator", key: "decision" });
    expect(coordinator.getSnapshot().active.kind).toBe("Media");
    coordinator.destroy();
  });

  it("无 Session 或 Stopped/Closed 清除固定 Media 并恢复 Idle", () => {
    const coordinator = createIslandCoordinator();
    dispatchMedia(coordinator, mediaPayload());
    const clear = windowsMediaToIslandEvent(clearPayload(Date.now() + 1));
    expect(clear).toEqual({
      type: "remove",
      source: WINDOWS_MEDIA_SOURCE,
      key: WINDOWS_MEDIA_KEY,
      at: 1_725_000_000_001,
    });
    coordinator.dispatch(clear!);
    expect(coordinator.getSnapshot()).toMatchObject({
      active: { kind: "Idle" },
      retainedCount: 0,
    });
    coordinator.destroy();
  });
});
