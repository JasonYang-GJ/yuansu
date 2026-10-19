import { act, fireEvent, render, screen, within } from "@testing-library/react";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import App from "./App";
import { createIslandCoordinator } from "./coordinator";

const tauriMocks = vi.hoisted(() => {
  const state: {
    focusHandler?: (event: { payload: boolean }) => void;
    resetHandler?: (event: { payload: string }) => void;
    mediaHandler?: (event: { payload: unknown }) => void;
    mediaSnapshot?: unknown;
    volumeHandler?: (event: { payload: unknown }) => void;
    fullscreenHandler?: (event: { payload: unknown }) => void;
    fullscreenSnapshot?: unknown;
    coreBridgeHandler?: (event: { payload: unknown }) => void;
    coreBridgeSnapshot?: unknown;
    startupLowPresence?: unknown;
  } = {};

  return {
    state,
    invoke: vi.fn((..._args: unknown[]) => Promise.resolve<unknown>(undefined)),
    onFocusChanged: vi.fn((handler: (event: { payload: boolean }) => void) => {
      state.focusHandler = handler;
      return Promise.resolve(vi.fn());
    }),
    listen: vi.fn((event: string, handler: (event: { payload: never }) => void) => {
      if (event === "island-mode-reset") {
        state.resetHandler = handler as (event: { payload: string }) => void;
      }
      if (event === "windows-volume-changed") {
        state.volumeHandler = handler as (event: { payload: unknown }) => void;
      }
      if (event === "windows-media-changed") {
        state.mediaHandler = handler as (event: { payload: unknown }) => void;
      }
      if (event === "fullscreen-presence-changed") {
        state.fullscreenHandler = handler as (event: { payload: unknown }) => void;
      }
      if (event === "yuanshu-core-bridge-state") {
        state.coreBridgeHandler = handler as (event: { payload: unknown }) => void;
      }
      return Promise.resolve(vi.fn());
    }),
  };
});

vi.mock("@tauri-apps/api/core", () => ({
  invoke: tauriMocks.invoke,
}));

vi.mock("@tauri-apps/api/window", () => ({
  getCurrentWindow: () => ({
    onFocusChanged: tauriMocks.onFocusChanged,
    listen: tauriMocks.listen,
  }),
}));

function islandRoot() {
  return screen.getByRole("main", { name: "元枢灵动岛" });
}

function expectMode(mode: string) {
  expect(islandRoot()).toHaveAttribute("data-mode", mode);
}

function islandModeCalls() {
  return tauriMocks.invoke.mock.calls.filter(([command]) => command === "set_island_mode");
}

async function advanceTime(milliseconds: number) {
  await act(async () => {
    await Promise.resolve();
    await Promise.resolve();
    vi.advanceTimersByTime(milliseconds);
    await Promise.resolve();
  });
}

async function flushPromises() {
  await act(async () => {
    await Promise.resolve();
    await Promise.resolve();
  });
}

describe("Yuanshu Island 四态待机体验", () => {
  beforeEach(() => {
    vi.useFakeTimers();
    tauriMocks.state.focusHandler = undefined;
    tauriMocks.state.resetHandler = undefined;
    tauriMocks.state.mediaHandler = undefined;
    tauriMocks.state.mediaSnapshot = undefined;
    tauriMocks.state.volumeHandler = undefined;
    tauriMocks.state.fullscreenHandler = undefined;
    tauriMocks.state.fullscreenSnapshot = false;
    tauriMocks.state.coreBridgeHandler = undefined;
    tauriMocks.state.coreBridgeSnapshot = { kind: "clear" };
    tauriMocks.state.startupLowPresence = false;
    tauriMocks.invoke.mockReset();
    tauriMocks.invoke.mockImplementation((...args: unknown[]) => Promise.resolve(
      args[0] === "get_windows_media_state"
        ? tauriMocks.state.mediaSnapshot
        : args[0] === "get_fullscreen_presence"
          ? tauriMocks.state.fullscreenSnapshot
          : args[0] === "get_yuanshu_core_bridge_state"
            ? tauriMocks.state.coreBridgeSnapshot
          : args[0] === "get_startup_low_presence"
            ? tauriMocks.state.startupLowPresence
          : args[0] === "assistant_read" ? null : undefined,
    ));
    tauriMocks.onFocusChanged.mockClear();
    tauriMocks.listen.mockClear();
  });

  afterEach(() => {
    vi.useRealTimers();
  });

  it("启动时显示 compact 小胶囊", () => {
    render(<App />);

    expectMode("compact");
    const compactIsland = screen.getByRole("button", { name: "展开元枢灵动岛" });
    expect(within(compactIsland).getByText("元枢")).toBeVisible();
    expect(within(compactIsland).getByText("Ready")).toBeVisible();
    expect(tauriMocks.listen).toHaveBeenCalledWith("island-mode-reset", expect.any(Function));
  });

  it("登录启动按后端意图同步为 tucked 且不抢占 Coordinator", async () => {
    tauriMocks.state.startupLowPresence = true;
    const coordinator = createIslandCoordinator();
    render(<App coordinator={coordinator} />);

    await flushPromises();

    expectMode("tucked");
    expect(islandRoot()).toHaveAttribute("data-presence", "peek");
    expect(coordinator.getSnapshot().active.kind).toBe("Idle");
    expect(tauriMocks.invoke).not.toHaveBeenCalledWith("set_island_mode", expect.anything());
    coordinator.destroy();
  });

  it("Windows 音量桥只通过 Coordinator 更新固定 SystemEvent 节点", () => {
    const coordinator = createIslandCoordinator();
    render(<App coordinator={coordinator} />);

    act(() => {
      tauriMocks.state.volumeHandler?.({
        payload: {
          volume: 64,
          muted: false,
          source: "windows.volume",
          key: "default-playback",
          eventId: "windows.volume.default-playback",
          timestampMs: 1_725_000_000_000,
        },
      });
      tauriMocks.state.volumeHandler?.({
        payload: {
          volume: 37,
          muted: true,
          source: "windows.volume",
          key: "default-playback",
          eventId: "windows.volume.default-playback",
          timestampMs: 1_725_000_000_010,
        },
      });
    });

    expect(coordinator.getSnapshot()).toMatchObject({
      active: {
        kind: "SystemEvent",
        source: "windows.volume",
        key: "default-playback",
        subtitle: "Muted",
        metadata: { volume: 37, muted: true },
      },
      retainedCount: 1,
    });
    expect(
      within(screen.getByRole("button", { name: "展开元枢灵动岛" })).getByText("Muted"),
    ).toBeVisible();
    coordinator.destroy();
  });

  it("启动先监听事件再回放 Provider 当前媒体状态，且只经 Coordinator 展示", async () => {
    tauriMocks.state.mediaSnapshot = {
      kind: "update",
      title: "启动时正在播放",
      artist: "测试艺术家",
      playbackState: "playing",
      appId: "test.player",
      source: "windows.media",
      key: "current-session",
      eventId: "windows.media.current-session",
      timestampMs: Date.now(),
    };
    const coordinator = createIslandCoordinator();
    render(<App coordinator={coordinator} />);
    await flushPromises();

    expect(tauriMocks.listen).toHaveBeenCalledWith(
      "windows-media-changed",
      expect.any(Function),
    );
    expect(tauriMocks.invoke).toHaveBeenCalledWith("get_windows_media_state");
    expect(coordinator.getSnapshot()).toMatchObject({
      active: {
        kind: "Media",
        source: "windows.media",
        key: "current-session",
        title: "启动时正在播放",
        subtitle: "测试艺术家",
        indicator: "Playing",
      },
      retainedCount: 1,
    });
    coordinator.destroy();
  });

  it("Core Bridge 启动先监听再原子投影，断线只清理 Core 来源", async () => {
    const turnId = "33333333-3333-4333-8333-333333333333";
    tauriMocks.state.coreBridgeSnapshot = {
      kind: "snapshot",
      snapshot: {
        bridge_protocol_version: 1,
        server_instance_id: "aaaaaaaa-aaaa-4aaa-8aaa-aaaaaaaaaaaa",
        snapshot_id: "55555555-5555-4555-8555-555555555555",
        snapshot_sequence: 0,
        generated_at_utc: "2026-08-26T00:00:00.000Z",
        source: "yuanshu.core",
        presentations: [{
          presentation_key: `turn:${turnId}`,
          entity_kind: "turn",
          session_id: "22222222-2222-4222-8222-222222222222",
          turn_id: turnId,
          task_id: null,
          entity_version: 7,
          phase: "Executing",
          island_kind: "Executing",
          title: "正在执行",
          subtitle: "请在元枢主窗口查看详情。",
          indicator: "Executing",
          is_terminal: false,
          requires_user_action: false,
          updated_at_utc: "2026-08-26T00:00:00.000Z",
          actions: [],
        }],
      },
    };
    const coordinator = createIslandCoordinator();
    coordinator.dispatch({
      type: "upsert",
      presentation: {
        kind: "Media",
        source: "windows.media",
        key: "playing",
        title: "本地媒体",
      },
    });
    render(<App coordinator={coordinator} />);
    await flushPromises();

    expect(tauriMocks.listen).toHaveBeenCalledWith(
      "yuanshu-core-bridge-state",
      expect.any(Function),
    );
    expect(tauriMocks.invoke).toHaveBeenCalledWith("get_yuanshu_core_bridge_state");
    expect(coordinator.getSnapshot()).toMatchObject({
      active: { kind: "Executing", source: "yuanshu.core", key: `turn:${turnId}` },
      retainedCount: 2,
    });
    expect(screen.getByLabelText("Core Bridge 连接状态")).toHaveTextContent(
      "Core Connected",
    );

    act(() => {
      tauriMocks.state.coreBridgeHandler?.({ payload: { kind: "clear" } });
    });
    expect(coordinator.getSnapshot()).toMatchObject({
      active: { kind: "Media", source: "windows.media" },
      retainedCount: 1,
    });
    expect(screen.getByLabelText("Core Bridge 连接状态")).toHaveTextContent(
      "Core Disconnected",
    );
    coordinator.destroy();
  });

  it("Fullscreen 仅隐藏表现层，期间 Coordinator 继续接收并在退出后恢复最新状态", async () => {
    const coordinator = createIslandCoordinator();
    render(<App coordinator={coordinator} />);
    await flushPromises();

    act(() => {
      tauriMocks.state.fullscreenHandler?.({ payload: true });
      coordinator.dispatch({
        type: "upsert",
        presentation: {
          kind: "WaitingUser",
          source: "integration-test",
          key: "decision",
          title: "仍需确认",
        },
      });
    });

    expect(islandRoot()).toHaveAttribute("data-presence", "hidden-fullscreen");
    expect(coordinator.getSnapshot().active).toMatchObject({
      kind: "WaitingUser",
      title: "仍需确认",
    });

    act(() => {
      tauriMocks.state.fullscreenHandler?.({ payload: false });
    });
    expect(islandRoot()).toHaveAttribute("data-presence", "active");
    expect(
      within(screen.getByRole("button", { name: "展开元枢灵动岛" })).getByText("仍需确认"),
    ).toBeInTheDocument();
    coordinator.destroy();
  });

  it("Playing 为 Active，Paused 反馈五秒后转 Peek 且同源更新不重复入场", async () => {
    const coordinator = createIslandCoordinator();
    render(<App coordinator={coordinator} />);
    await advanceTime(8_200);
    expectMode("tucked");

    const timestampMs = Date.now();
    act(() => {
      tauriMocks.state.mediaHandler?.({
        payload: {
          kind: "update",
          title: "Presence Track",
          artist: "Presence Artist",
          playbackState: "playing",
          appId: "test.player",
          source: "windows.media",
          key: "current-session",
          eventId: "windows.media.current-session",
          timestampMs,
        },
      });
    });
    expect(islandRoot()).toHaveAttribute("data-presence", "active");
    expectMode("compact");

    tauriMocks.invoke.mockClear();
    act(() => {
      tauriMocks.state.mediaHandler?.({
        payload: {
          kind: "update",
          title: "Presence Track",
          artist: "Presence Artist",
          playbackState: "paused",
          appId: "test.player",
          source: "windows.media",
          key: "current-session",
          eventId: "windows.media.current-session",
          timestampMs: timestampMs + 1,
        },
      });
    });
    expect(islandRoot()).toHaveAttribute("data-presence", "active");
    await advanceTime(4_999);
    expect(islandRoot()).toHaveAttribute("data-presence", "active");
    await advanceTime(1);
    expect(islandRoot()).toHaveAttribute("data-presence", "peek");
    expectMode("orb");
    expect(
      islandModeCalls(),
    ).toEqual([["set_island_mode", { mode: "orb" }]]);

    act(() => {
      tauriMocks.state.mediaHandler?.({
        payload: {
          kind: "clear",
          source: "windows.media",
          key: "current-session",
          eventId: "windows.media.current-session",
          timestampMs: timestampMs + 5_001,
        },
      });
    });
    expect(coordinator.getSnapshot().active.kind).toBe("Idle");
    expect(islandRoot()).toHaveAttribute("data-presence", "peek");
    expectMode("orb");
    coordinator.destroy();
  });

  it("无操作 6 秒进入 orb，再过 2.2 秒进入 tucked", async () => {
    render(<App />);

    await advanceTime(5_999);
    expectMode("compact");
    await advanceTime(1);
    expectMode("orb");
    expect(tauriMocks.invoke).toHaveBeenLastCalledWith("set_island_mode", { mode: "orb" });

    await advanceTime(2_199);
    expectMode("orb");
    await advanceTime(1);
    expectMode("tucked");
    expect(tauriMocks.invoke).toHaveBeenLastCalledWith("set_island_mode", { mode: "tucked" });
  });

  it("compact 的鼠标和键盘活动都会重置 6 秒待机计时", async () => {
    render(<App />);

    await advanceTime(4_000);
    fireEvent.mouseMove(islandRoot());
    await advanceTime(4_000);
    fireEvent.keyDown(window, { key: "ArrowDown" });
    await advanceTime(5_999);
    expectMode("compact");
    await advanceTime(1);
    expectMode("orb");
  });

  it("tucked 悬停唤醒后不自动沉下，离开 1.2 秒才 tucked，重新进入会取消旧计时", async () => {
    render(<App />);
    await advanceTime(6_000);
    await advanceTime(2_200);
    expectMode("tucked");

    fireEvent.mouseEnter(islandRoot());
    expectMode("orb");
    await advanceTime(5_000);
    expectMode("orb");

    fireEvent.mouseLeave(islandRoot(), { clientX: -1, clientY: -1 });
    await advanceTime(600);
    fireEvent.mouseEnter(islandRoot());
    await advanceTime(1_000);
    expectMode("orb");

    fireEvent.mouseLeave(islandRoot(), { clientX: -1, clientY: -1 });
    await advanceTime(1_199);
    expectMode("orb");
    await advanceTime(1);
    expectMode("tucked");
  });

  it("awakened orb 的窗口内 leave 坐标不启动计时，真实越界后才完整等待 1.2 秒", async () => {
    render(<App />);
    await advanceTime(6_000);
    await advanceTime(2_200);
    expectMode("tucked");

    fireEvent.mouseEnter(islandRoot());
    expectMode("orb");
    fireEvent.mouseLeave(islandRoot(), { clientX: 1, clientY: 1 });
    await advanceTime(2_500);
    expectMode("orb");
    expect(tauriMocks.invoke).not.toHaveBeenLastCalledWith("set_island_mode", {
      mode: "tucked",
    });

    fireEvent.mouseLeave(islandRoot(), { clientX: -1, clientY: -1 });
    await advanceTime(1_199);
    expectMode("orb");
    await advanceTime(1);
    expectMode("tucked");
  });

  it("resize 误发 mouseleave 时若待机球实际仍 hover，不会在 1.2 秒后 tucked", async () => {
    render(<App />);
    await advanceTime(6_000);
    await advanceTime(2_200);
    expectMode("tucked");

    fireEvent.mouseEnter(islandRoot());
    expectMode("orb");
    const idleButton = screen.getByRole("button", { name: "展开元枢灵动岛" });
    const matchesSpy = vi.spyOn(idleButton, "matches").mockImplementation((selector) => {
      if (selector === ":hover") {
        return true;
      }
      return Element.prototype.matches.call(idleButton, selector);
    });

    fireEvent.mouseLeave(islandRoot(), { clientX: -1, clientY: -1 });
    await advanceTime(1_200);
    expectMode("orb");
    await advanceTime(3_000);
    expectMode("orb");

    matchesSpy.mockImplementation((selector) => {
      if (selector === ":hover") {
        return false;
      }
      return Element.prototype.matches.call(idleButton, selector);
    });
    fireEvent.mouseLeave(islandRoot(), { clientX: -1, clientY: -1 });
    await advanceTime(1_199);
    expectMode("orb");
    await advanceTime(1);
    expectMode("tucked");
  });

  it.each(["compact", "orb", "tucked"] as const)("点击 %s 会直接展开", async (startMode) => {
    render(<App />);

    if (startMode === "orb" || startMode === "tucked") {
      await advanceTime(6_000);
    }
    if (startMode === "tucked") {
      await advanceTime(2_200);
    }
    expectMode(startMode);

    fireEvent.click(screen.getByRole("button", { name: "展开元枢灵动岛" }));

    expectMode("expanded");
    expect(screen.getByRole("region", { name: "元枢灵动岛详情" })).toBeInTheDocument();
    expect(tauriMocks.invoke).toHaveBeenLastCalledWith("set_island_mode", {
      mode: "expanded",
    });
  });

  it("收起按钮和 Esc 都会回到 compact", () => {
    render(<App />);

    fireEvent.click(screen.getByRole("button", { name: "展开元枢灵动岛" }));
    fireEvent.click(screen.getByRole("button", { name: "收起" }));
    expectMode("compact");
    expect(tauriMocks.invoke).toHaveBeenLastCalledWith("set_island_mode", { mode: "compact" });

    fireEvent.click(screen.getByRole("button", { name: "展开元枢灵动岛" }));
    fireEvent.keyDown(window, { key: "Escape" });
    expectMode("compact");
    expect(tauriMocks.invoke).toHaveBeenLastCalledWith("set_island_mode", { mode: "compact" });
  });

  it("expanded 窗口失焦会回到 compact，非 expanded 失焦不额外切换", async () => {
    render(<App />);

    act(() => {
      tauriMocks.state.focusHandler?.({ payload: false });
    });
    expect(islandModeCalls()).toHaveLength(0);

    fireEvent.click(screen.getByRole("button", { name: "展开元枢灵动岛" }));
    act(() => {
      tauriMocks.state.focusHandler?.({ payload: false });
    });
    expectMode("compact");
    expect(tauriMocks.invoke).toHaveBeenLastCalledWith("set_island_mode", { mode: "compact" });
    await flushPromises();
  });

  it("快速切换时过期请求失败不会覆盖较新的最终模式", async () => {
    let rejectStaleRequest: ((error: Error) => void) | undefined;
    let firstModeRequest = true;
    tauriMocks.invoke.mockImplementation((command: unknown) => {
      if (command === "assistant_read") return Promise.resolve(null);
      if (command === "set_island_mode" && firstModeRequest) {
        firstModeRequest = false;
        return new Promise<void>((_resolve, reject) => {
          rejectStaleRequest = reject;
        });
      }
      if (command === "get_fullscreen_presence" || command === "get_startup_low_presence") {
        return Promise.resolve(false);
      }
      return Promise.resolve(undefined);
    });
    render(<App />);

    fireEvent.click(screen.getByRole("button", { name: "展开元枢灵动岛" }));
    fireEvent.keyDown(window, { key: "Escape" });
    fireEvent.click(screen.getByRole("button", { name: "展开元枢灵动岛" }));
    expectMode("expanded");

    rejectStaleRequest?.(new Error("stale animation failed"));
    await flushPromises();

    expectMode("expanded");
    expect(screen.getByRole("status")).toHaveTextContent("");
    expect(
      islandModeCalls(),
    ).toEqual([
      ["set_island_mode", { mode: "expanded" }],
      ["set_island_mode", { mode: "compact" }],
      ["set_island_mode", { mode: "expanded" }],
    ]);
  });

  it("当前 Tauri 同步失败时回滚到上一个模式", async () => {
    tauriMocks.invoke.mockImplementation((command: unknown) =>
      command === "assistant_read" ? Promise.resolve(null) : command === "set_island_mode"
        ? Promise.reject(new Error("window unavailable"))
        : Promise.resolve(false));
    render(<App />);

    fireEvent.click(screen.getByRole("button", { name: "展开元枢灵动岛" }));
    await flushPromises();

    expectMode("compact");
    expect(screen.getByRole("status")).toHaveTextContent("窗口模式同步失败，请重试");
  });

  it("后端 island-mode-reset 可无 invoke 同步回 compact 并重新开始待机计时", async () => {
    render(<App />);
    fireEvent.click(screen.getByRole("button", { name: "展开元枢灵动岛" }));
    expect(islandModeCalls()).toHaveLength(1);

    act(() => {
      tauriMocks.state.resetHandler?.({ payload: "compact" });
    });

    expectMode("compact");
    expect(islandModeCalls()).toHaveLength(1);
    await advanceTime(5_999);
    expectMode("compact");
    await advanceTime(1);
    expectMode("orb");
  });

  it("非空闲状态从 tucked 唤醒到 compact，并在保持期间禁止再次藏入", async () => {
    const coordinator = createIslandCoordinator();
    render(<App coordinator={coordinator} />);
    await advanceTime(6_000);
    await advanceTime(2_200);
    expectMode("tucked");
    tauriMocks.invoke.mockClear();

    act(() => {
      coordinator.dispatch({
        type: "upsert",
        at: 10,
        presentation: {
          kind: "Executing",
          source: "integration-test",
          key: "task",
          title: "正在执行",
          subtitle: "处理中",
          progress: 25,
        },
      });
    });

    expectMode("compact");
    expect(tauriMocks.invoke).toHaveBeenCalledOnce();
    expect(tauriMocks.invoke).toHaveBeenLastCalledWith("set_island_mode", {
      mode: "compact",
    });
    await advanceTime(12_000);
    expectMode("compact");

    coordinator.destroy();
  });

  it("Coordinator 唤醒 tucked 第一次失败时会有界重试并恢复 compact", async () => {
    const coordinator = createIslandCoordinator();
    render(<App coordinator={coordinator} />);
    await advanceTime(6_000);
    await advanceTime(2_200);
    expectMode("tucked");
    tauriMocks.invoke.mockReset();
    tauriMocks.invoke
      .mockRejectedValueOnce(new Error("transient native failure"))
      .mockResolvedValue(undefined);

    act(() => {
      coordinator.dispatch({
        type: "upsert",
        presentation: {
          kind: "WaitingUser",
          source: "integration-test",
          key: "decision",
          title: "需要你的决定",
        },
      });
    });
    await flushPromises();
    expectMode("tucked");
    expect(screen.getByRole("status")).toHaveTextContent("窗口模式同步失败，请重试");

    await advanceTime(349);
    expect(tauriMocks.invoke).toHaveBeenCalledTimes(1);
    await advanceTime(1);
    expectMode("compact");
    expect(tauriMocks.invoke).toHaveBeenCalledTimes(2);

    coordinator.destroy();
  });

  it("Coordinator 唤醒连续失败三次后停止重试", async () => {
    const coordinator = createIslandCoordinator();
    render(<App coordinator={coordinator} />);
    await advanceTime(6_000);
    await advanceTime(2_200);
    tauriMocks.invoke.mockReset();
    tauriMocks.invoke.mockRejectedValue(new Error("persistent native failure"));

    act(() => {
      coordinator.dispatch({
        type: "upsert",
        presentation: {
          kind: "Error",
          source: "integration-test",
          key: "attention-error",
          title: "需要注意",
        },
      });
    });
    await flushPromises();
    await advanceTime(350);
    await flushPromises();
    await advanceTime(350);
    await flushPromises();
    await advanceTime(2_000);

    expectMode("tucked");
    expect(tauriMocks.invoke).toHaveBeenCalledTimes(3);
    coordinator.destroy();
  });

  it("注意状态结束后会取消尚未执行的唤醒重试", async () => {
    const coordinator = createIslandCoordinator();
    render(<App coordinator={coordinator} />);
    await advanceTime(6_000);
    await advanceTime(2_200);
    tauriMocks.invoke.mockReset();
    tauriMocks.invoke.mockRejectedValueOnce(new Error("transient native failure"));

    act(() => {
      coordinator.dispatch({
        type: "upsert",
        presentation: {
          kind: "SystemEvent",
          source: "integration-test",
          key: "temporary-attention",
          title: "临时事件",
        },
      });
    });
    await flushPromises();
    act(() => {
      coordinator.dispatch({
        type: "remove",
        source: "integration-test",
        key: "temporary-attention",
      });
    });
    await advanceTime(1_000);

    expect(tauriMocks.invoke).toHaveBeenCalledTimes(1);
    expectMode("tucked");
    coordinator.destroy();
  });

  it("状态在待机边界到达时旧 timer 不能把 WaitingUser 藏成 orb", async () => {
    const coordinator = createIslandCoordinator();
    render(<App coordinator={coordinator} />);
    await advanceTime(5_999);

    act(() => {
      coordinator.dispatch({
        type: "upsert",
        presentation: {
          kind: "WaitingUser",
          source: "integration-test",
          key: "boundary-decision",
          title: "边界确认",
        },
      });
    });
    await advanceTime(1);

    expectMode("compact");
    expect(tauriMocks.invoke).not.toHaveBeenCalledWith("set_island_mode", { mode: "orb" });
    coordinator.destroy();
  });

  it("同一 Executing 的进度更新只更新内容，不重复触发原生窗口动画", () => {
    const coordinator = createIslandCoordinator();
    render(<App coordinator={coordinator} />);

    act(() => {
      coordinator.dispatch({
        type: "upsert",
        at: 10,
        presentation: {
          kind: "Executing",
          source: "integration-test",
          key: "task",
          title: "正在执行",
          progress: 25,
        },
      });
    });
    expect(
      within(screen.getByRole("button", { name: "展开元枢灵动岛" })).getByText("25%"),
    ).toBeVisible();
    expect(islandModeCalls()).toHaveLength(0);

    act(() => {
      coordinator.dispatch({
        type: "upsert",
        at: 20,
        presentation: {
          kind: "Executing",
          source: "integration-test",
          key: "task",
          title: "正在执行",
          progress: 75,
        },
      });
    });

    expect(
      within(screen.getByRole("button", { name: "展开元枢灵动岛" })).getByText("75%"),
    ).toBeVisible();
    expect(islandModeCalls()).toHaveLength(0);

    coordinator.destroy();
  });

  it("Esc 只收起窗口，不清除 Coordinator 当前展示", () => {
    const coordinator = createIslandCoordinator();
    coordinator.dispatch({
      type: "upsert",
      presentation: {
        kind: "WaitingUser",
        source: "integration-test",
        key: "decision",
        title: "需要你的决定",
        subtitle: "是否继续？",
      },
    });
    render(<App coordinator={coordinator} />);

    fireEvent.click(screen.getByRole("button", { name: "展开元枢灵动岛" }));
    fireEvent.keyDown(window, { key: "Escape" });

    expectMode("compact");
    expect(coordinator.getSnapshot().active).toMatchObject({
      kind: "WaitingUser",
      key: "decision",
    });
    expect(
      within(screen.getByRole("button", { name: "展开元枢灵动岛" })).getByText(
        "需要你的决定",
      ),
    ).toBeInTheDocument();

    coordinator.destroy();
  });

  it("后端 compact 复位只改变窗口形态并保留当前 Presentation", async () => {
    const coordinator = createIslandCoordinator();
    coordinator.dispatch({
      type: "upsert",
      presentation: {
        kind: "Executing",
        source: "integration-test",
        key: "long-task",
        title: "正在执行",
        progress: 42,
      },
    });
    render(<App coordinator={coordinator} />);
    fireEvent.click(screen.getByRole("button", { name: "展开元枢灵动岛" }));

    act(() => {
      tauriMocks.state.resetHandler?.({ payload: "compact" });
    });

    expectMode("compact");
    expect(coordinator.getSnapshot().active).toMatchObject({
      kind: "Executing",
      key: "long-task",
      progress: 42,
    });
    await advanceTime(12_000);
    expectMode("compact");
    coordinator.destroy();
  });

  it("展示动作原样交给上层，不由 UI 或窗口控制器决定生命周期", () => {
    const coordinator = createIslandCoordinator();
    const onPresentationAction = vi.fn();
    coordinator.dispatch({
      type: "upsert",
      presentation: {
        kind: "WaitingUser",
        source: "integration-test",
        key: "decision",
        title: "需要确认",
        actions: [{ id: "continue", label: "继续", emphasis: "primary" }],
      },
    });
    render(
      <App coordinator={coordinator} onPresentationAction={onPresentationAction} />,
    );
    fireEvent.click(screen.getByRole("button", { name: "展开元枢灵动岛" }));
    fireEvent.click(screen.getByRole("button", { name: "继续" }));

    expect(onPresentationAction).toHaveBeenCalledWith(
      { id: "continue", label: "继续", emphasis: "primary" },
      expect.objectContaining({ kind: "WaitingUser", key: "decision" }),
    );
    expect(coordinator.getSnapshot().active.kind).toBe("WaitingUser");

    coordinator.destroy();
  });

  it("动作处理导致状态切换时把键盘焦点安全移到收起按钮", async () => {
    const coordinator = createIslandCoordinator();
    coordinator.dispatch({
      type: "upsert",
      presentation: {
        kind: "WaitingUser",
        source: "integration-test",
        key: "decision",
        title: "需要确认",
        actions: [{ id: "continue", label: "继续", emphasis: "primary" }],
      },
    });
    render(
      <App
        coordinator={coordinator}
        onPresentationAction={(_action, presentation) => {
          coordinator.dispatch({
            type: "remove",
            source: presentation.source,
            key: presentation.key,
          });
        }}
      />,
    );
    fireEvent.click(screen.getByRole("button", { name: "展开元枢灵动岛" }));
    const actionButton = screen.getByRole("button", { name: "继续" });
    actionButton.focus();
    fireEvent.click(actionButton);
    await flushPromises();

    expect(coordinator.getSnapshot().active.kind).toBe("Idle");
    expect(screen.getByRole("button", { name: "收起" })).toHaveFocus();
    coordinator.destroy();
  });
});
