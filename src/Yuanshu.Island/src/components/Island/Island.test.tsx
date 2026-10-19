import { createRef } from "react";
import { fireEvent, render, screen, within } from "@testing-library/react";
import { describe, expect, it, vi } from "vitest";
import { ISLAND_STATE_KINDS } from "../../coordinator";
import type { IslandAction, IslandPresentation, IslandStateKind } from "../../coordinator";
import { Island } from "./Island";

function createPresentation(
  kind: IslandStateKind,
  overrides: Partial<IslandPresentation> = {},
): IslandPresentation {
  return {
    kind,
    source: "ui-test",
    key: kind.toLowerCase(),
    title: `${kind} 标题`,
    subtitle: `${kind} 状态`,
    priority: { semantic: 0, source: 0 },
    dismiss: { mode: "persistent" },
    requiresUserAction: kind === "WaitingUser",
    visibility: { wake: "none", allowIdleCollapse: true },
    createdAt: 1,
    updatedAt: 1,
    ...overrides,
  } as IslandPresentation;
}

function renderIsland(
  presentation: IslandPresentation,
  options: {
    mode?: "compact" | "expanded" | "orb" | "tucked";
    onAction?: (action: IslandAction) => void;
    coreBridgeConnectionStatus?: "connected" | "disconnected";
  } = {},
) {
  const props = {
    mode: options.mode ?? ("expanded" as const),
    presentation,
    coreBridgeConnectionStatus: options.coreBridgeConnectionStatus ?? "disconnected",
    onExpand: vi.fn(),
    onCollapse: vi.fn(),
    onAction: options.onAction,
    compactButtonRef: createRef<HTMLButtonElement>(),
    collapseButtonRef: createRef<HTMLButtonElement>(),
  };

  return { ...render(<Island {...props} />), props };
}

describe("Island 状态展示", () => {
  it("在现有详情标题区只读显示 Core Bridge 连接状态", () => {
    const presentation = createPresentation("Idle");
    const { rerender, props } = renderIsland(presentation, {
      coreBridgeConnectionStatus: "connected",
    });
    const status = screen.getByLabelText("Core Bridge 连接状态");

    expect(status).toHaveAttribute("data-connection", "connected");
    expect(status).toHaveTextContent("Core Connected");

    rerender(
      <Island
        {...props}
        presentation={presentation}
        coreBridgeConnectionStatus="disconnected"
      />,
    );
    expect(status).toHaveAttribute("data-connection", "disconnected");
    expect(status).toHaveTextContent("Core Disconnected");
  });

  it.each(ISLAND_STATE_KINDS)("展示 %s presentation 而不改变窗口 DOM 骨架", (kind) => {
    const presentation = createPresentation(kind);
    const { container } = renderIsland(presentation);
    const shell = container.querySelector(".island-shell");
    const details = screen.getByRole("region", { name: "元枢灵动岛详情" });

    expect(shell).toHaveAttribute("data-state", "expanded");
    expect(shell).toHaveAttribute("data-presentation", kind);
    expect(within(details).getByRole("heading", { name: `${kind} 标题` })).toBeVisible();
    expect(within(details).getByText(`${kind} 状态`)).toBeVisible();
    expect(within(details).getByRole("button", { name: "收起" })).toBeVisible();
  });

  it("Executing 进度更新复用同一个进度节点并只更新数值", () => {
    const initial = createPresentation("Executing", { progress: 25 });
    const { rerender, props } = renderIsland(initial);
    const progressbar = screen.getByRole("progressbar", { name: "执行进度" });
    const fill = progressbar.querySelector(".island-progress__fill");

    expect(progressbar).toHaveAttribute("aria-valuenow", "25");
    expect(fill).toHaveAttribute("style", expect.stringContaining("--progress: 25%"));

    rerender(
      <Island
        {...props}
        presentation={createPresentation("Executing", {
          key: initial.key,
          createdAt: initial.createdAt,
          updatedAt: 2,
          progress: 75,
        })}
      />,
    );

    const updatedProgressbar = screen.getByRole("progressbar", { name: "执行进度" });
    expect(updatedProgressbar).toBe(progressbar);
    expect(updatedProgressbar).toHaveAttribute("aria-valuenow", "75");
    expect(updatedProgressbar.querySelector(".island-progress__fill")).toHaveAttribute(
      "style",
      expect.stringContaining("--progress: 75%"),
    );
  });

  it("Executing 没有百分比时显示克制的不定进度", () => {
    renderIsland(createPresentation("Executing"));

    const progressbar = screen.getByRole("progressbar", { name: "执行进度" });
    expect(progressbar).not.toHaveAttribute("aria-valuenow");
    expect(progressbar).toHaveClass("island-progress--indeterminate");
    expect(screen.getByText("进行中")).toBeVisible();
  });

  it("Volume SystemEvent 沿用进度样式并明确展示音量", () => {
    renderIsland(createPresentation("SystemEvent", {
      category: "volume",
      metadata: { volume: 64, muted: false },
      subtitle: "64%",
    }));

    const progressbar = screen.getByRole("progressbar", { name: "系统音量" });
    expect(progressbar).toHaveAttribute("aria-valuenow", "64");
    expect(progressbar.querySelector(".island-progress__fill")).toHaveAttribute(
      "style",
      expect.stringContaining("--progress: 64%"),
    );
    expect(screen.getByText("系统音量")).toBeVisible();
  });

  it("静音 Volume 保留底层音量进度并明确显示 Muted", () => {
    renderIsland(createPresentation("SystemEvent", {
      category: "volume",
      metadata: { volume: 37, muted: true },
      subtitle: "Muted",
      indicator: "Muted",
    }));

    expect(screen.getByRole("progressbar", { name: "系统音量" })).toHaveAttribute(
      "aria-valuenow",
      "37",
    );
    expect(
      within(screen.getByRole("region", { name: "元枢灵动岛详情" })).getAllByText("Muted"),
    ).toHaveLength(2);
  });

  it("Media 沿用现有结构展示标题、artist 与 Playing/Paused", () => {
    const presentation = createPresentation("Media", {
      title: "测试歌曲",
      subtitle: "测试艺术家",
      indicator: "Paused",
    });
    const { rerender, props } = renderIsland(presentation, { mode: "compact" });

    const compactButton = screen.getByRole("button", { name: "展开元枢灵动岛" });
    expect(within(compactButton).getByText("测试歌曲")).toBeVisible();
    expect(within(compactButton).getByText("Paused")).toBeVisible();

    rerender(<Island {...props} mode="expanded" presentation={presentation} />);
    const details = screen.getByRole("region", { name: "元枢灵动岛详情" });
    expect(within(details).getByText("测试艺术家")).toBeVisible();
    expect(within(details).getByText("Paused")).toBeVisible();
  });

  it("展示 Coordinator 提供的动作并把完整 action 原样回传", () => {
    const onAction = vi.fn();
    const action: IslandAction = { id: "confirm", label: "确认继续", emphasis: "primary" };
    renderIsland(createPresentation("WaitingUser", { actions: [action] }), { onAction });

    fireEvent.click(screen.getByRole("button", { name: "确认继续" }));

    expect(onAction).toHaveBeenCalledOnce();
    expect(onAction).toHaveBeenCalledWith(action);
    expect(screen.getByRole("button", { name: "收起" })).toBeVisible();
    expect(screen.queryByRole("button", { name: /设置/ })).not.toBeInTheDocument();
  });

  it("没有上层 action handler 时不显示伪可用动作", () => {
    const action: IslandAction = { id: "confirm", label: "确认继续", emphasis: "primary" };
    renderIsland(createPresentation("WaitingUser", { actions: [action] }));

    expect(screen.getByRole("button", { name: "确认继续" })).toBeDisabled();
  });

  it.each(["compact", "orb", "tucked"] as const)(
    "%s 保留阶段 0 的展开按钮语义",
    (mode) => {
      const { props } = renderIsland(createPresentation("Idle"), { mode });

      fireEvent.click(screen.getByRole("button", { name: "展开元枢灵动岛" }));

      expect(props.onExpand).toHaveBeenCalledOnce();
    },
  );
});
