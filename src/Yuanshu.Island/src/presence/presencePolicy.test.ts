import { describe, expect, it } from "vitest";
import type { IslandPresentation } from "../coordinator";
import {
  PAUSED_ACTIVE_GRACE_MS,
  derivePresence,
  presenceShellPolicy,
} from "./presencePolicy";

function presentation(
  kind: IslandPresentation["kind"],
  overrides: Partial<IslandPresentation> = {},
): IslandPresentation {
  return {
    kind,
    source: "test",
    key: "presence",
    title: "测试状态",
    priority: { semantic: 0, source: 0 },
    dismiss: { mode: "persistent" },
    requiresUserAction: kind === "WaitingUser",
    visibility: { wake: "none", allowIdleCollapse: true },
    createdAt: 1_000,
    updatedAt: 1_000,
    ...overrides,
  } as IslandPresentation;
}

describe("Presence 显示投影", () => {
  it("Fullscreen 是最高抑制层且不改写业务 Presentation", () => {
    const active = presentation("WaitingUser");

    expect(derivePresence(active, true, 2_000)).toBe("hidden-fullscreen");
    expect(active.kind).toBe("WaitingUser");
  });

  it("Idle 与 Closed 后的 Coordinator Idle 都投影为 Peek", () => {
    expect(derivePresence(presentation("Idle"), false, 2_000)).toBe("peek");
  });

  it("WaitingUser、SystemEvent 和 Playing Media 保持 Active", () => {
    expect(derivePresence(presentation("WaitingUser"), false, 2_000)).toBe("active");
    expect(derivePresence(presentation("SystemEvent"), false, 2_000)).toBe("active");
    expect(
      derivePresence(presentation("Media", { indicator: "Playing" }), false, 2_000),
    ).toBe("active");
  });

  it("Paused 先给出反馈，宽限期结束后降为 Peek", () => {
    const paused = presentation("Media", { indicator: "Paused", updatedAt: 10_000 });

    expect(derivePresence(paused, false, 10_000 + PAUSED_ACTIVE_GRACE_MS - 1)).toBe(
      "active",
    );
    expect(derivePresence(paused, false, 10_000 + PAUSED_ACTIVE_GRACE_MS)).toBe("peek");
  });

  it("Active 禁止自动藏边，Peek 直接使用低存在感窗口策略", () => {
    expect(presenceShellPolicy("active")).toEqual({
      wake: "compact",
      allowIdleCollapse: false,
      preferredMode: "compact",
    });
    expect(presenceShellPolicy("peek")).toEqual({
      wake: "none",
      allowIdleCollapse: true,
      preferredMode: "orb",
    });
    expect(presenceShellPolicy("hidden-fullscreen")).toEqual({
      wake: "none",
      allowIdleCollapse: false,
      preferredMode: "compact",
    });
  });
});
