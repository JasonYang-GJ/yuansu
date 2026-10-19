import { act, fireEvent, render, screen } from "@testing-library/react";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import { SimulatorApp } from "./SimulatorApp";

vi.mock("@tauri-apps/api/core", () => ({
  invoke: vi.fn(() => Promise.resolve(null)),
}));

vi.mock("@tauri-apps/api/window", () => ({
  getCurrentWindow: () => ({
    listen: vi.fn(() => Promise.resolve(vi.fn())),
  }),
}));

async function advanceTime(milliseconds: number) {
  await act(async () => {
    vi.advanceTimersByTime(milliseconds);
    await Promise.resolve();
  });
}

describe("Island State Simulator", () => {
  beforeEach(() => {
    vi.useFakeTimers();
  });

  afterEach(() => {
    vi.useRealTimers();
  });

  it.each([
    ["Idle", "Idle"],
    ["Listening", "Listening"],
    ["Thinking", "Thinking"],
    ["Executing 25%", "Executing"],
    ["Executing 75%", "Executing"],
    ["WaitingUser", "WaitingUser"],
    ["Success", "Success"],
    ["Error", "Error"],
    ["SystemEvent", "SystemEvent"],
    ["Media", "Media"],
  ])("%s 控件触发 %s 展示", (buttonName, expectedKind) => {
    render(<SimulatorApp />);

    fireEvent.click(screen.getByRole("button", { name: buttonName }));

    expect(screen.getByTestId("simulator-active")).toHaveTextContent(expectedKind);
  });

  it("SystemEvent 到期后恢复之前的 Media", async () => {
    render(<SimulatorApp />);
    fireEvent.click(
      screen.getByRole("button", { name: "Media → SystemEvent → Media" }),
    );

    expect(screen.getByTestId("simulator-active")).toHaveTextContent("SystemEvent");
    await advanceTime(1_999);
    expect(screen.getByTestId("simulator-active")).toHaveTextContent("SystemEvent");
    await advanceTime(1);
    expect(screen.getByTestId("simulator-active")).toHaveTextContent("Media");
  });

  it("Executing 25% 到 75% 是同一展示的原位更新", () => {
    render(<SimulatorApp />);
    fireEvent.click(screen.getByRole("button", { name: "Executing 25%" }));
    const progressbar = screen.getByRole("progressbar", { name: "执行进度" });
    expect(progressbar).toHaveAttribute("aria-valuenow", "25");

    fireEvent.click(screen.getByRole("button", { name: "Executing 75%" }));

    expect(screen.getByRole("progressbar", { name: "执行进度" })).toBe(progressbar);
    expect(progressbar).toHaveAttribute("aria-valuenow", "75");
    expect(screen.getByText(/retained 1/)).toBeVisible();
  });

  it("WaitingUser 被处理后恢复被抢占的 Executing", () => {
    render(<SimulatorApp />);
    fireEvent.click(screen.getByRole("button", { name: "Executing → WaitingUser" }));
    expect(screen.getByTestId("simulator-active")).toHaveTextContent("WaitingUser");

    fireEvent.click(screen.getByRole("button", { name: "处理 WaitingUser" }));
    expect(screen.getByTestId("simulator-active")).toHaveTextContent("Executing");
    expect(screen.getByText("导出任务继续进行")).toBeVisible();
    expect(screen.getByRole("progressbar", { name: "执行进度" })).toHaveAttribute(
      "aria-valuenow",
      "48",
    );
  });
});
