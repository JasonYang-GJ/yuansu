import { describe, expect, it } from "vitest";
import {
  createIslandCoordinator,
  type IslandCoordinator,
  type IslandEvent,
  type IslandPresentationInput,
  type IslandScheduler,
} from "./index";

class ManualClock implements IslandScheduler {
  now = 0;
  readonly clearedCallbacks: Array<() => void> = [];
  private nextHandle = 1;
  private tasks = new Map<number, { callback: () => void; dueAt: number }>();

  setTimeout(callback: () => void, delayMs: number) {
    const handle = this.nextHandle++;
    this.tasks.set(handle, { callback, dueAt: this.now + delayMs });
    return handle;
  }

  clearTimeout(handle: unknown) {
    if (typeof handle === "number") {
      const task = this.tasks.get(handle);
      if (task !== undefined) {
        this.clearedCallbacks.push(task.callback);
      }
      this.tasks.delete(handle);
    }
  }

  advanceBy(milliseconds: number) {
    const target = this.now + milliseconds;
    while (true) {
      const next = [...this.tasks.entries()]
        .filter(([, task]) => task.dueAt <= target)
        .sort((left, right) => left[1].dueAt - right[1].dueAt)[0];
      if (next === undefined) {
        break;
      }

      const [handle, task] = next;
      this.tasks.delete(handle);
      this.now = task.dueAt;
      task.callback();
    }
    this.now = target;
  }

  fireClearedCallbacks() {
    const callbacks = this.clearedCallbacks.splice(0);
    callbacks.forEach((callback) => callback());
  }
}

function upsert(
  coordinator: IslandCoordinator,
  presentation: IslandPresentationInput,
  at: number,
) {
  return coordinator.dispatch({ type: "upsert", presentation, at });
}

describe("Island Coordinator", () => {
  it("没有事件时提供稳定的 Idle 展示", () => {
    const coordinator = createIslandCoordinator();

    expect(coordinator.getSnapshot()).toMatchObject({
      active: {
        kind: "Idle",
        source: "coordinator",
        key: "idle",
        title: "元枢",
        subtitle: "Ready",
        visibility: { wake: "none", allowIdleCollapse: true },
      },
      retainedCount: 0,
    });

    coordinator.destroy();
  });

  it("upsert 接收一个结构化状态并把它作为当前展示", () => {
    const coordinator = createIslandCoordinator();

    const result = coordinator.dispatch({
      type: "upsert",
      at: 1_000,
      presentation: {
        kind: "Media",
        source: "media-simulator",
        key: "now-playing",
        title: "正在播放",
        subtitle: "元枢测试曲目",
        indicator: "播放中",
        metadata: { album: "Stage 1" },
      },
    });

    expect(result.accepted).toBe(true);
    expect(result.snapshot).toMatchObject({
      active: {
        kind: "Media",
        source: "media-simulator",
        key: "now-playing",
        title: "正在播放",
        subtitle: "元枢测试曲目",
        indicator: "播放中",
        dismiss: { mode: "persistent" },
        requiresUserAction: false,
        visibility: { wake: "none", allowIdleCollapse: true },
        createdAt: 1_000,
        updatedAt: 1_000,
      },
      retainedCount: 1,
      revision: 1,
    });

    coordinator.destroy();
  });

  it("SystemEvent 临时抢占 Media，结束后恢复被保留的 Media", () => {
    const coordinator = createIslandCoordinator();
    coordinator.dispatch({
      type: "upsert",
      at: 100,
      presentation: {
        kind: "Media",
        source: "media",
        key: "playing",
        title: "正在播放",
      },
    });

    coordinator.dispatch({
      type: "upsert",
      at: 200,
      presentation: {
        kind: "SystemEvent",
        source: "windows",
        key: "volume",
        title: "音量 70%",
      },
    });

    expect(coordinator.getSnapshot()).toMatchObject({
      active: { kind: "SystemEvent", key: "volume" },
      retainedCount: 2,
    });

    const result = coordinator.dispatch({
      type: "remove",
      source: "windows",
      key: "volume",
      at: 300,
    });

    expect(result).toMatchObject({
      accepted: true,
      snapshot: {
        active: { kind: "Media", key: "playing" },
        retainedCount: 1,
      },
    });

    coordinator.destroy();
  });

  it("按冻结的语义顺序选择状态，来源数字不能跨语义越级", () => {
    const coordinator = createIslandCoordinator();
    const ordered: IslandPresentationInput[] = [
      { kind: "Idle", source: "sim", key: "idle", title: "Idle" },
      { kind: "Media", source: "sim", key: "media", title: "Media" },
      {
        kind: "SystemEvent",
        source: "sim",
        key: "system",
        title: "SystemEvent",
        sourcePriority: 99,
      },
      { kind: "Success", source: "sim", key: "success", title: "Success" },
      { kind: "Thinking", source: "sim", key: "thinking", title: "Thinking" },
      {
        kind: "Executing",
        source: "sim",
        key: "executing",
        title: "Executing",
        progress: 25,
      },
      { kind: "Listening", source: "sim", key: "listening", title: "Listening" },
      {
        kind: "Error",
        source: "sim",
        key: "normal-error",
        title: "Error",
        severity: "error",
      },
      {
        kind: "WaitingUser",
        source: "sim",
        key: "waiting",
        title: "WaitingUser",
        sourcePriority: -99,
        actions: [{ id: "continue", label: "继续", emphasis: "primary" }],
      },
      {
        kind: "Error",
        source: "sim",
        key: "critical-error",
        title: "Critical Error",
        severity: "critical",
      },
    ];

    ordered.forEach((presentation, index) => upsert(coordinator, presentation, index + 1));
    expect(coordinator.getSnapshot().active.key).toBe("critical-error");

    const expectedRestoreOrder = [
      "waiting",
      "normal-error",
      "listening",
      "executing",
      "thinking",
      "success",
      "system",
      "media",
      "idle",
    ];
    for (const key of expectedRestoreOrder) {
      const current = coordinator.getSnapshot().active;
      coordinator.dispatch({ type: "remove", source: "sim", key: current.key });
      expect(coordinator.getSnapshot().active.key).toBe(key);
    }

    coordinator.destroy();
  });

  it("临时 SystemEvent 到期后自动恢复 Media", () => {
    const time = new ManualClock();
    const coordinator = createIslandCoordinator({
      clock: () => time.now,
      scheduler: time,
    });
    upsert(
      coordinator,
      { kind: "Media", source: "media", key: "playing", title: "正在播放" },
      time.now,
    );
    upsert(
      coordinator,
      { kind: "SystemEvent", source: "windows", key: "volume", title: "音量 80%" },
      time.now,
    );

    expect(coordinator.getSnapshot().active).toMatchObject({
      kind: "SystemEvent",
      expiresAt: 2_000,
      visibility: { wake: "compact", allowIdleCollapse: false },
    });

    time.advanceBy(1_999);
    expect(coordinator.getSnapshot().active.kind).toBe("SystemEvent");
    time.advanceBy(1);
    expect(coordinator.getSnapshot()).toMatchObject({
      active: { kind: "Media", key: "playing" },
      retainedCount: 1,
    });

    coordinator.destroy();
  });

  it("WaitingUser 强制保持，不能被调用方设置为自动消失", () => {
    const time = new ManualClock();
    const coordinator = createIslandCoordinator({
      clock: () => time.now,
      scheduler: time,
    });
    upsert(
      coordinator,
      {
        kind: "WaitingUser",
        source: "yuanshu",
        key: "decision",
        title: "需要你的决定",
        dismiss: { mode: "auto", durationMs: 10 },
      },
      time.now,
    );

    expect(coordinator.getSnapshot().active).toMatchObject({
      kind: "WaitingUser",
      dismiss: { mode: "persistent" },
      requiresUserAction: true,
      visibility: { wake: "compact", allowIdleCollapse: false },
    });
    expect(coordinator.getSnapshot().active.expiresAt).toBeUndefined();

    time.advanceBy(60_000);
    expect(coordinator.getSnapshot().active.kind).toBe("WaitingUser");

    coordinator.destroy();
  });

  it.each([0, -1, Number.NaN, 86_400_001])(
    "WaitingUser 忽略无效 auto duration %s 并仍强制保持",
    (durationMs) => {
      const time = new ManualClock();
      const coordinator = createIslandCoordinator({ clock: () => time.now, scheduler: time });
      const result = coordinator.dispatch({
        type: "upsert",
        presentation: {
          kind: "WaitingUser",
          source: "untrusted-provider",
          key: "decision",
          title: "需要决定",
          dismiss: { mode: "auto", durationMs },
        },
      });

      expect(result).toMatchObject({
        accepted: true,
        snapshot: {
          active: { kind: "WaitingUser", dismiss: { mode: "persistent" } },
        },
      });
      expect(coordinator.getSnapshot().active.expiresAt).toBeUndefined();
      time.advanceBy(86_400_000);
      expect(coordinator.getSnapshot().active.kind).toBe("WaitingUser");
      coordinator.destroy();
    },
  );

  it("忽略伪造的内部生命周期字段，persistent 状态不会被 expiresAt 注入绕过", () => {
    const time = new ManualClock();
    const coordinator = createIslandCoordinator({ clock: () => time.now, scheduler: time });
    const injectedWaiting = {
      kind: "WaitingUser",
      source: "untrusted-provider",
      key: "decision",
      title: "等待确认",
      expiresAt: Number.NaN,
      createdAt: -1,
      updatedAt: Number.POSITIVE_INFINITY,
      priority: { semantic: 99_999, source: 99_999 },
      visibility: { wake: "none", allowIdleCollapse: true },
      requiresUserAction: false,
    } as unknown as IslandPresentationInput;

    expect(upsert(coordinator, injectedWaiting, 0).accepted).toBe(true);
    expect(coordinator.getSnapshot().active).toMatchObject({
      kind: "WaitingUser",
      createdAt: 0,
      updatedAt: 0,
      requiresUserAction: true,
      visibility: { wake: "compact", allowIdleCollapse: false },
    });
    expect(coordinator.getSnapshot().active.expiresAt).toBeUndefined();

    time.advanceBy(86_400_000);
    expect(coordinator.getSnapshot().active.kind).toBe("WaitingUser");

    coordinator.dispatch({
      type: "remove",
      source: "untrusted-provider",
      key: "decision",
      at: time.now,
    });
    const injectedError = {
      kind: "Error",
      source: "untrusted-provider",
      key: "persistent-error",
      title: "持续错误",
      severity: "error",
      expiresAt: time.now + 1,
    } as unknown as IslandPresentationInput;
    expect(upsert(coordinator, injectedError, time.now).accepted).toBe(true);
    expect(coordinator.getSnapshot().active.expiresAt).toBeUndefined();
    time.advanceBy(86_400_000);
    expect(coordinator.getSnapshot().active.kind).toBe("Error");
    coordinator.destroy();
  });

  it("Executing 同 source+key 原位更新进度，不产生事件队列", () => {
    const coordinator = createIslandCoordinator();
    upsert(
      coordinator,
      {
        kind: "Executing",
        source: "codex-simulator",
        key: "task-1",
        title: "正在执行",
        progress: 10,
      },
      10,
    );
    upsert(
      coordinator,
      {
        kind: "Executing",
        source: "codex-simulator",
        key: "task-1",
        title: "正在执行",
        subtitle: "第二步",
        progress: 72,
      },
      20,
    );

    expect(coordinator.getSnapshot()).toMatchObject({
      active: {
        kind: "Executing",
        progress: 72,
        subtitle: "第二步",
        createdAt: 10,
        updatedAt: 20,
        visibility: { wake: "compact", allowIdleCollapse: false },
      },
      retainedCount: 1,
      revision: 2,
    });

    coordinator.destroy();
  });

  it("高频同键 SystemEvent 只保留最新值并从更新时重新计算 TTL", () => {
    const time = new ManualClock();
    const coordinator = createIslandCoordinator({ clock: () => time.now, scheduler: time });

    for (let volume = 61; volume <= 65; volume += 1) {
      upsert(
        coordinator,
        {
          kind: "SystemEvent",
          source: "windows",
          key: "volume",
          title: `音量 ${volume}%`,
        },
        time.now,
      );
      time.advanceBy(100);
    }

    expect(coordinator.getSnapshot()).toMatchObject({
      active: { title: "音量 65%", createdAt: 0, updatedAt: 400, expiresAt: 2_400 },
      retainedCount: 1,
    });
    time.advanceBy(1_899);
    expect(coordinator.getSnapshot().active.kind).toBe("SystemEvent");
    time.advanceBy(1);
    expect(coordinator.getSnapshot().active.kind).toBe("Idle");

    coordinator.destroy();
  });

  it("多来源同 key 的交错事件风暴各自合并且互不覆盖", () => {
    const coordinator = createIslandCoordinator();
    for (let value = 1; value <= 100; value += 1) {
      upsert(
        coordinator,
        {
          kind: "SystemEvent",
          source: "windows-volume",
          key: "level",
          title: `音量 ${value}`,
        },
        value,
      );
      upsert(
        coordinator,
        {
          kind: "SystemEvent",
          source: "windows-brightness",
          key: "level",
          title: `亮度 ${value}`,
        },
        value,
      );
    }

    expect(coordinator.getSnapshot()).toMatchObject({
      active: { source: "windows-volume", title: "音量 100" },
      retainedCount: 2,
    });
    coordinator.dispatch({ type: "remove", source: "windows-volume", key: "level", at: 101 });
    expect(coordinator.getSnapshot()).toMatchObject({
      active: { source: "windows-brightness", title: "亮度 100" },
      retainedCount: 1,
    });
    coordinator.destroy();
  });

  it("旧计时器即使被调度器误触发，也不能删除同键新更新", () => {
    const time = new ManualClock();
    const coordinator = createIslandCoordinator({ clock: () => time.now, scheduler: time });
    upsert(
      coordinator,
      { kind: "SystemEvent", source: "windows", key: "volume", title: "音量 40%" },
      0,
    );
    time.advanceBy(1_000);
    upsert(
      coordinator,
      { kind: "SystemEvent", source: "windows", key: "volume", title: "音量 55%" },
      1_000,
    );

    time.fireClearedCallbacks();
    expect(coordinator.getSnapshot().active.title).toBe("音量 55%");
    time.advanceBy(1_999);
    expect(coordinator.getSnapshot().active.kind).toBe("SystemEvent");
    time.advanceBy(1);
    expect(coordinator.getSnapshot().active.kind).toBe("Idle");

    coordinator.destroy();
  });

  it("临时状态在被 WaitingUser 遮挡期间仍会到期", () => {
    const time = new ManualClock();
    const coordinator = createIslandCoordinator({ clock: () => time.now, scheduler: time });
    upsert(
      coordinator,
      { kind: "Media", source: "media", key: "playing", title: "播放中" },
      0,
    );
    upsert(
      coordinator,
      { kind: "SystemEvent", source: "windows", key: "volume", title: "音量 80%" },
      0,
    );
    upsert(
      coordinator,
      { kind: "WaitingUser", source: "yuanshu", key: "decision", title: "需要确认" },
      0,
    );

    time.advanceBy(2_000);
    coordinator.dispatch({ type: "complete", source: "yuanshu", key: "decision", at: 2_000 });
    expect(coordinator.getSnapshot()).toMatchObject({
      active: { kind: "Media", key: "playing" },
      retainedCount: 1,
    });

    coordinator.destroy();
  });

  it("Executing 被 WaitingUser 抢占，完成等待后恢复 Executing", () => {
    const coordinator = createIslandCoordinator();
    upsert(
      coordinator,
      { kind: "Executing", source: "yuanshu", key: "task", title: "正在执行" },
      10,
    );
    upsert(
      coordinator,
      { kind: "WaitingUser", source: "yuanshu", key: "decision", title: "请选择" },
      20,
    );
    expect(coordinator.getSnapshot().active.kind).toBe("WaitingUser");

    coordinator.dispatch({ type: "complete", source: "yuanshu", key: "decision", at: 30 });
    expect(coordinator.getSnapshot().active).toMatchObject({ kind: "Executing", key: "task" });

    coordinator.destroy();
  });

  it("被 WaitingUser 遮挡的 Executing 恢复时使用最新进度", () => {
    const coordinator = createIslandCoordinator();
    upsert(
      coordinator,
      { kind: "Executing", source: "yuanshu", key: "task", title: "执行中", progress: 25 },
      10,
    );
    upsert(
      coordinator,
      { kind: "WaitingUser", source: "yuanshu", key: "decision", title: "需要确认" },
      20,
    );
    upsert(
      coordinator,
      {
        kind: "Executing",
        source: "yuanshu",
        key: "task",
        title: "执行中",
        subtitle: "后台已继续推进",
        progress: 75,
      },
      30,
    );

    coordinator.dispatch({ type: "remove", source: "yuanshu", key: "decision", at: 40 });
    expect(coordinator.getSnapshot().active).toMatchObject({
      kind: "Executing",
      key: "task",
      progress: 75,
      subtitle: "后台已继续推进",
    });
    coordinator.destroy();
  });

  it("被抢占的 Executing 若已结束，WaitingUser 解除后不会复活", () => {
    const coordinator = createIslandCoordinator();
    upsert(
      coordinator,
      { kind: "Executing", source: "yuanshu", key: "task", title: "执行中" },
      10,
    );
    upsert(
      coordinator,
      { kind: "WaitingUser", source: "yuanshu", key: "decision", title: "需要确认" },
      20,
    );
    coordinator.dispatch({ type: "complete", source: "yuanshu", key: "task", at: 30 });
    coordinator.dispatch({ type: "remove", source: "yuanshu", key: "decision", at: 40 });

    expect(coordinator.getSnapshot()).toMatchObject({
      active: { kind: "Idle" },
      retainedCount: 0,
    });
    coordinator.destroy();
  });

  it("complete 将同任务原位转为 Success，成功提示结束后恢复 Media", () => {
    const time = new ManualClock();
    const coordinator = createIslandCoordinator({ clock: () => time.now, scheduler: time });
    upsert(
      coordinator,
      { kind: "Media", source: "media", key: "playing", title: "播放中" },
      0,
    );
    time.advanceBy(10);
    upsert(
      coordinator,
      { kind: "Executing", source: "codex", key: "task", title: "正在执行", progress: 75 },
      10,
    );
    time.advanceBy(10);
    const result = coordinator.dispatch({
      type: "complete",
      source: "codex",
      key: "task",
      at: 20,
      outcome: { kind: "Success", title: "执行完成", indicator: "成功" },
    });

    expect(result).toMatchObject({
      accepted: true,
      snapshot: {
        active: { kind: "Success", key: "task", createdAt: 10, updatedAt: 20 },
        retainedCount: 2,
      },
    });
    time.advanceBy(2_500);
    expect(coordinator.getSnapshot().active.kind).toBe("Media");

    coordinator.destroy();
  });

  it("clear-source 仅移除指定来源，并为晚到旧事件保留水位", () => {
    const coordinator = createIslandCoordinator({ clock: () => 100 });
    upsert(
      coordinator,
      { kind: "Thinking", source: "codex", key: "task", title: "思考中" },
      10,
    );
    upsert(
      coordinator,
      { kind: "Media", source: "media", key: "playing", title: "播放中" },
      10,
    );

    coordinator.dispatch({ type: "clear-source", source: "codex", at: 20 });
    expect(coordinator.getSnapshot()).toMatchObject({
      active: { source: "media", key: "playing" },
      retainedCount: 1,
    });
    const stale = upsert(
      coordinator,
      { kind: "Executing", source: "codex", key: "late", title: "晚到任务" },
      19,
    );
    expect(stale).toMatchObject({ accepted: false, reason: "stale-event" });

    coordinator.destroy();
  });

  it("replace-source 原子替换 Core 投影且不影响本地 Provider", () => {
    const coordinator = createIslandCoordinator({ clock: () => 1_000 });
    upsert(
      coordinator,
      { kind: "Media", source: "windows.media", key: "playing", title: "播放中" },
      10,
    );
    upsert(
      coordinator,
      { kind: "Thinking", source: "yuanshu.core", key: "turn:old", title: "旧状态" },
      10,
    );
    const observedRevisions: number[] = [];
    const unsubscribe = coordinator.subscribe((snapshot) => {
      observedRevisions.push(snapshot.revision);
    });

    const replaced = coordinator.dispatch({
      type: "replace-source",
      source: "yuanshu.core",
      at: 20,
      presentations: [
        {
          kind: "WaitingUser",
          source: "yuanshu.core",
          key: "turn:new",
          title: "需要确认",
        },
      ],
    });

    expect(replaced).toMatchObject({
      accepted: true,
      snapshot: {
        active: { source: "yuanshu.core", key: "turn:new", kind: "WaitingUser" },
        retainedCount: 2,
        revision: 3,
      },
    });
    expect(observedRevisions).toEqual([3]);

    const invalid = coordinator.dispatch({
      type: "replace-source",
      source: "yuanshu.core",
      at: 30,
      presentations: [
        {
          kind: "Executing",
          source: "forged.source",
          key: "task:bad",
          title: "不应提交",
        },
      ],
    });
    expect(invalid).toMatchObject({ accepted: false, reason: "invalid-source-replacement" });
    expect(coordinator.getSnapshot()).toMatchObject({
      active: { key: "turn:new" },
      retainedCount: 2,
      revision: 3,
    });

    const cleared = coordinator.dispatch({
      type: "replace-source",
      source: "yuanshu.core",
      at: 40,
      presentations: [],
    });
    expect(cleared).toMatchObject({
      accepted: true,
      snapshot: {
        active: { source: "windows.media", key: "playing" },
        retainedCount: 1,
        revision: 4,
      },
    });
    expect(observedRevisions).toEqual([3, 4]);
    unsubscribe();
    coordinator.destroy();
  });

  it("旧时间戳不能覆盖更新或完成后的新记录", () => {
    const coordinator = createIslandCoordinator({ clock: () => 1_000 });
    upsert(
      coordinator,
      { kind: "Executing", source: "codex", key: "task", title: "新版", progress: 70 },
      200,
    );
    const staleUpdate = upsert(
      coordinator,
      { kind: "Executing", source: "codex", key: "task", title: "旧版", progress: 10 },
      199,
    );
    expect(staleUpdate).toMatchObject({ accepted: false, reason: "stale-event" });
    expect(coordinator.getSnapshot().active).toMatchObject({ title: "新版", progress: 70 });

    coordinator.dispatch({
      type: "complete",
      source: "codex",
      key: "task",
      at: 300,
      outcome: { kind: "Error", title: "失败", severity: "error" },
    });
    const lateProgress = upsert(
      coordinator,
      { kind: "Executing", source: "codex", key: "task", title: "迟到进度", progress: 99 },
      250,
    );
    expect(lateProgress).toMatchObject({ accepted: false, reason: "stale-event" });
    expect(coordinator.getSnapshot().active.kind).toBe("Error");

    coordinator.destroy();
  });

  it("同语义按钳制后的 sourcePriority、updatedAt、source+key 稳定决胜", () => {
    const coordinator = createIslandCoordinator();
    upsert(
      coordinator,
      { kind: "Media", source: "alpha", key: "same", title: "Alpha", sourcePriority: -999 },
      100,
    );
    upsert(
      coordinator,
      { kind: "Media", source: "beta", key: "same", title: "Beta", sourcePriority: 999 },
      1,
    );
    expect(coordinator.getSnapshot().active).toMatchObject({
      source: "beta",
      priority: { source: 99 },
    });
    expect(coordinator.getSnapshot().retainedCount).toBe(2);

    upsert(
      coordinator,
      { kind: "Media", source: "alpha", key: "same", title: "Alpha new", sourcePriority: 999 },
      200,
    );
    expect(coordinator.getSnapshot().active.source).toBe("alpha");
    upsert(
      coordinator,
      { kind: "Media", source: "zeta", key: "same", title: "Zeta", sourcePriority: 999 },
      200,
    );
    expect(coordinator.getSnapshot().active.source).toBe("zeta");

    coordinator.destroy();
  });

  it("相同时间戳的同级状态不依赖到达顺序，按 source+key 稳定决胜", () => {
    const createWithOrder = (sources: readonly string[]) => {
      const coordinator = createIslandCoordinator();
      for (const source of sources) {
        upsert(
          coordinator,
          { kind: "Media", source, key: "playing", title: source },
          100,
        );
      }
      return coordinator;
    };
    const forward = createWithOrder(["alpha", "zeta"]);
    const reverse = createWithOrder(["zeta", "alpha"]);

    expect(forward.getSnapshot().active.source).toBe("zeta");
    expect(reverse.getSnapshot().active.source).toBe("zeta");
    forward.destroy();
    reverse.destroy();
  });

  it("公开快照与复制后的 action/metadata 不能从外部改写内部状态", () => {
    const coordinator = createIslandCoordinator();
    const action = {
      id: "continue",
      label: "继续",
      payload: { internal: true },
    };
    const metadata = { detail: { stage: "one" }, steps: [1, 2] };
    coordinator.dispatch({
      type: "upsert",
      presentation: {
        kind: "WaitingUser",
        source: "provider",
        key: "decision",
        title: "原始标题",
        actions: [action],
        metadata,
      },
    });
    const snapshot = coordinator.getSnapshot();

    expect(Object.isFrozen(snapshot)).toBe(true);
    expect(Object.isFrozen(snapshot.active)).toBe(true);
    expect(Object.isFrozen(snapshot.active.actions)).toBe(true);
    expect(Object.isFrozen(snapshot.active.actions?.[0])).toBe(true);
    expect(Object.isFrozen(snapshot.active.metadata)).toBe(true);
    expect(Object.isFrozen(snapshot.active.metadata?.detail)).toBe(true);
    expect(Object.isFrozen(snapshot.active.metadata?.steps)).toBe(true);
    expect(snapshot.active.actions?.[0]).not.toHaveProperty("payload");
    action.label = "外部篡改";
    metadata.detail.stage = "mutated";
    metadata.steps.push(3);
    try {
      (snapshot.active as { title: string }).title = "快照篡改";
    } catch {
      // ES modules run in strict mode; the unchanged value is the contract under test.
    }

    expect(coordinator.getSnapshot().active).toMatchObject({
      title: "原始标题",
      actions: [{ id: "continue", label: "继续" }],
      metadata: { detail: { stage: "one" }, steps: [1, 2] },
    });
    coordinator.destroy();
  });

  it("非法运行时事件被拒绝且不抛异常、不污染当前状态", () => {
    const coordinator = createIslandCoordinator();
    const invalidEvents = [
      null,
      { type: "unknown" },
      { type: "upsert", presentation: { kind: "Thinking" } },
      {
        type: "upsert",
        presentation: {
          kind: "Executing",
          source: "sim",
          key: "task",
          title: "坏进度",
          progress: 101,
        },
      },
      {
        type: "upsert",
        presentation: {
          kind: "SystemEvent",
          source: "sim",
          key: "volume",
          title: "坏时长",
          dismiss: { mode: "auto", durationMs: -1 },
        },
      },
      {
        type: "upsert",
        presentation: {
          kind: "SystemEvent",
          source: "sim",
          key: "too-long",
          title: "过长时长",
          dismiss: { mode: "auto", durationMs: 86_400_001 },
        },
      },
      {
        type: "upsert",
        presentation: {
          kind: "WaitingUser",
          source: "sim",
          key: "duplicate-actions",
          title: "重复动作",
          actions: [
            { id: "continue", label: "继续" },
            { id: "continue", label: "仍然继续" },
          ],
        },
      },
      { type: "remove", source: "sim", key: "task", at: Number.NaN },
    ];

    for (const event of invalidEvents) {
      expect(() => coordinator.dispatch(event as unknown as IslandEvent)).not.toThrow();
      expect(coordinator.dispatch(event as unknown as IslandEvent).accepted).toBe(false);
    }
    expect(coordinator.getSnapshot()).toMatchObject({ active: { kind: "Idle" }, retainedCount: 0 });

    coordinator.destroy();
  });

  it("拒绝明显超前于接收时间的来源时间戳", () => {
    const coordinator = createIslandCoordinator({ clock: () => 1_000 });
    const result = coordinator.dispatch({
      type: "upsert",
      at: 61_001,
      presentation: {
        kind: "Media",
        source: "provider",
        key: "future",
        title: "未来事件",
      },
    });

    expect(result).toMatchObject({ accepted: false, reason: "future-timestamp" });
    expect(coordinator.getSnapshot().active.kind).toBe("Idle");
    coordinator.destroy();
  });

  it("允许范围内的未来时间会钳制为接收时间，来源可立即清除", () => {
    const coordinator = createIslandCoordinator({ clock: () => 1_000 });
    const inserted = coordinator.dispatch({
      type: "upsert",
      at: 2_000,
      presentation: {
        kind: "Executing",
        source: "provider",
        key: "task",
        title: "带偏差的事件",
      },
    });
    expect(inserted).toMatchObject({
      accepted: true,
      snapshot: { active: { updatedAt: 1_000 } },
    });

    const cleared = coordinator.dispatch({ type: "clear-source", source: "provider" });
    expect(cleared).toMatchObject({
      accepted: true,
      snapshot: { active: { kind: "Idle" }, retainedCount: 0 },
    });
    coordinator.destroy();
  });

  it("订阅只观察有效变化，destroy 后计时器和 dispatch 都失效", () => {
    const time = new ManualClock();
    const coordinator = createIslandCoordinator({ clock: () => time.now, scheduler: time });
    const revisions: number[] = [];
    const unsubscribe = coordinator.subscribe((snapshot) => revisions.push(snapshot.revision));
    upsert(
      coordinator,
      { kind: "SystemEvent", source: "windows", key: "volume", title: "音量" },
      0,
    );
    expect(revisions).toEqual([1]);

    coordinator.destroy();
    coordinator.destroy();
    unsubscribe();
    time.advanceBy(10_000);
    const afterDestroy = upsert(
      coordinator,
      { kind: "Media", source: "media", key: "playing", title: "播放中" },
      10_000,
    );
    expect(afterDestroy).toMatchObject({ accepted: false, reason: "coordinator-destroyed" });
    expect(revisions).toEqual([1]);
    expect(coordinator.getSnapshot().active.kind).toBe("SystemEvent");
  });
});
