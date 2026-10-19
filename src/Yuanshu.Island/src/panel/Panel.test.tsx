import { act, fireEvent, render, screen, waitFor } from "@testing-library/react";
import { beforeEach, describe, expect, it, vi } from "vitest";
import { Panel } from "./Panel";
import { defaultPreferences } from "../settings/useSettings";

const mocks = vi.hoisted(() => ({ invoke: vi.fn(), handlers: new Map<string, (event: { payload: unknown }) => void>() }));
vi.mock("@tauri-apps/api/core", () => ({ invoke: mocks.invoke }));
vi.mock("@tauri-apps/api/window", () => ({ getCurrentWindow: () => ({ listen: (name: string, handler: (event: { payload: unknown }) => void) => {
  mocks.handlers.set(name, handler); return Promise.resolve(() => mocks.handlers.delete(name));
} }) }));

const server = "aaaaaaaa-aaaa-4aaa-8aaa-aaaaaaaaaaaa";
const turn = "33333333-3333-4333-8333-333333333333";
function snapshot(version = 4) {
  return { kind: "snapshot", snapshot: {
    bridge_protocol_version: 1, server_instance_id: server,
    snapshot_id: "55555555-5555-4555-8555-555555555555", snapshot_sequence: 0,
    generated_at_utc: "2026-09-22T00:00:00.000Z", source: "yuanshu.core",
    presentations: [{ presentation_key: `turn:${turn}`, entity_kind: "turn",
      session_id: "22222222-2222-4222-8222-222222222222", turn_id: turn, task_id: null,
      entity_version: version, phase: "Executing", island_kind: "Executing", title: "正在执行",
      subtitle: "元枢正在处理当前请求。", indicator: "Executing", is_terminal: false,
      requires_user_action: false, updated_at_utc: "2026-09-22T00:00:00.000Z", actions: [] }],
  } };
}

describe("任务与设置面板", () => {
  beforeEach(() => {
    mocks.handlers.clear();
    mocks.invoke.mockReset();
    mocks.invoke.mockImplementation(async (command: string) => {
      if (command === "get_island_settings") return { preferences: defaultPreferences, warning: null };
      if (command === "get_island_autostart") return { enabled: false };
      if (command === "get_island_control_status") return { canCancel: true, reason: "" };
      if (command === "get_yuanshu_core_bridge_state") return snapshot();
      return undefined;
    });
  });
  it("shows authoritative task phases and requires confirmation bound to the displayed target", async () => {
    render(<Panel />);
    await screen.findByText("正在执行");
    fireEvent.click(screen.getByRole("button", { name: "任务详情" }));
    expect(screen.getByText("核心尚未提供百分比，以当前处理阶段为准。")).toBeVisible();
    fireEvent.click(screen.getByRole("button", { name: "取消任务" }));
    expect(mocks.invoke.mock.calls.some(([name]) => name === "cancel_island_task")).toBe(false);
    fireEvent.click(screen.getByRole("button", { name: "确认取消" }));
    await screen.findByText("取消请求已被核心接收，正在等待任务状态更新。");
    expect(mocks.invoke).toHaveBeenCalledWith("cancel_island_task", {
      expectedServerInstanceId: server, presentationKey: `turn:${turn}`, expectedVersion: 4,
    });
    expect(screen.getByText("正在执行")).toBeVisible();
  });
  it("disconnect clears task detail and disables a pending cancellation", async () => {
    render(<Panel />);
    await screen.findByText("正在执行");
    fireEvent.click(screen.getByRole("button", { name: "取消任务" }));
    await act(async () => { mocks.handlers.get("yuanshu-core-bridge-state")?.({ payload: { kind: "clear" } }); });
    expect(screen.getByRole("button", { name: "确认取消" })).toBeDisabled();
    expect(screen.queryByRole("button", { name: "任务详情" })).not.toBeInTheDocument();
  });
  it("save failure is visible and does not silently flip a preference", async () => {
    render(<Panel />);
    await screen.findByText("正在执行");
    fireEvent.click(screen.getByRole("button", { name: "设置" }));
    mocks.invoke.mockRejectedValueOnce("设置写入失败，原设置仍然有效。");
    fireEvent.click(screen.getByRole("switch", { name: /媒体信息/ }));
    expect(await screen.findByRole("alert")).toHaveTextContent("设置写入失败");
    expect(screen.getByRole("switch", { name: /媒体信息/ })).toBeChecked();
  });
  it("keeps legacy preferences visible without exposing independent startup or inactive display controls", async () => {
    render(<Panel />);
    await screen.findByText("正在执行");
    fireEvent.click(screen.getByRole("button", { name: "设置" }));
    expect(screen.getByRole("switch", { name: /空闲时自动藏边/ })).toBeDisabled();
    expect(screen.getByRole("switch", { name: /全屏时隐藏/ })).toBeDisabled();
    expect(screen.queryByRole("switch", { name: /登录 Windows 时启动/ })).not.toBeInTheDocument();
    expect(screen.getByText(/随元枢一起启动和退出/)).toBeVisible();
    expect(mocks.invoke.mock.calls.some(([name]) => name === "set_island_autostart")).toBe(false);
  });
  it("does not offer task cancellation when the current Host did not negotiate it", async () => {
    const base = mocks.invoke.getMockImplementation()!;
    mocks.invoke.mockImplementation((command: string) => command === "get_island_control_status"
      ? Promise.resolve({ canCancel: false, reason: "当前元枢未提供任务取消能力，请回到元枢管理窗口操作。" }) : base(command));
    render(<Panel />);
    await screen.findByText("正在执行");
    expect(screen.getByRole("button", { name: "取消任务" })).toBeDisabled();
    expect(screen.getByText(/当前元枢未提供任务取消能力/)).toBeVisible();
    expect(mocks.invoke.mock.calls.some(([name]) => name === "cancel_island_task")).toBe(false);
  });
  it("an old initial snapshot cannot overwrite a newer disconnect event", async () => {
    let resolveSnapshot: (value: unknown) => void = () => undefined;
    const base = mocks.invoke.getMockImplementation()!;
    mocks.invoke.mockImplementation((command: string) => command === "get_yuanshu_core_bridge_state"
      ? new Promise((resolve) => { resolveSnapshot = resolve; }) : base(command));
    render(<Panel />);
    await waitFor(() => expect(mocks.handlers.has("yuanshu-core-bridge-state")).toBe(true));
    await act(async () => { mocks.handlers.get("yuanshu-core-bridge-state")!({ payload: { kind: "clear" } }); resolveSnapshot(snapshot()); });
    expect(screen.getByText("等待元枢连接")).toBeVisible();
    expect(screen.queryByRole("button", { name: "任务详情" })).not.toBeInTheDocument();
  });
});
