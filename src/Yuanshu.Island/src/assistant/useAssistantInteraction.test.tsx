import { act, renderHook } from "@testing-library/react";
import { beforeEach, describe, expect, it, vi } from "vitest";
import { decodeSurfaceState, useAssistantInteraction, type AssistantSurfaceState } from "./useAssistantInteraction";

const native = vi.hoisted(() => ({ invoke: vi.fn(), listen: vi.fn(), handler: undefined as undefined | ((event: { payload: unknown }) => void) }));
vi.mock("@tauri-apps/api/core", () => ({ invoke: native.invoke }));
vi.mock("@tauri-apps/api/window", () => ({ getCurrentWindow: () => ({ listen: native.listen }) }));
const state = (revision: number, interactive = false, shortcutAvailable = true): AssistantSurfaceState => ({ revision, interactive, shortcutAvailable });
function deferred<T>() { let resolve!: (value: T) => void; const promise = new Promise<T>((done) => { resolve = done; }); return { promise, resolve }; }
async function flush() { await act(async () => { await Promise.resolve(); await Promise.resolve(); }); }
beforeEach(() => {
  native.handler = undefined;
  native.invoke.mockReset(); native.listen.mockReset();
  native.invoke.mockResolvedValue(state(1));
  native.listen.mockImplementation(async (_event: string, handler: typeof native.handler) => { native.handler = handler; return () => {}; });
});

describe("native assistant interaction authority", () => {
  it("validates exact fields and safe monotonic revision values", () => {
    expect(decodeSurfaceState(state(0))).toEqual(state(0));
    for (const invalid of [null, [], { ...state(1), interactive: "true" }, { ...state(1), revision: -1 },
      { ...state(1), revision: 1.5 }, { ...state(1), revision: Number.MAX_SAFE_INTEGER + 1 }, { ...state(1), extra: true }]) {
      expect(decodeSurfaceState(invalid)).toBeNull();
    }
  });

  it("leaves standalone mode untouched", async () => {
    const { result } = renderHook(() => useAssistantInteraction(false));
    await flush(); await act(async () => { await result.current.release(); });
    expect(native.invoke).not.toHaveBeenCalled(); expect(native.listen).not.toHaveBeenCalled();
  });

  it("does not let a delayed initial read or stale event undo a manual entry", async () => {
    const initial = deferred<unknown>(); native.invoke.mockReturnValue(initial.promise);
    const { result } = renderHook(() => useAssistantInteraction(true)); await flush();
    expect(native.listen).toHaveBeenCalledWith("assistant-surface-changed", expect.any(Function));
    act(() => { native.handler?.({ payload: state(3, true) }); });
    await act(async () => { initial.resolve(state(1)); });
    expect(result.current.interactive).toBe(true);
    act(() => { native.handler?.({ payload: state(2) }); native.handler?.({ payload: state(3) }); });
    expect(result.current.interactive).toBe(true);
    act(() => { native.handler?.({ payload: state(4) }); });
    expect(result.current.interactive).toBe(false);
  });

  it("releases controls once without sending a microphone command and accepts a newer native event", async () => {
    native.invoke.mockResolvedValue(state(1, true));
    const { result } = renderHook(() => useAssistantInteraction(true)); await flush();
    const pending = deferred<unknown>(); native.invoke.mockReturnValue(pending.promise);
    let release!: Promise<void>;
    await act(async () => { release = result.current.release(); void result.current.release(); });
    expect(native.invoke.mock.calls.filter(([name]) => name === "assistant_release_controls")).toHaveLength(1);
    act(() => { native.handler?.({ payload: state(4, true, false) }); });
    await act(async () => { pending.resolve(state(3)); await release; });
    expect(result.current.interactive).toBe(true); expect(result.current.shortcutAvailable).toBe(false);
    expect(native.invoke.mock.calls.every(([name]) => name !== "assistant_command")).toBe(true);
  });

  it("does not pretend release succeeded when the native operation fails", async () => {
    native.invoke.mockResolvedValue(state(1, true));
    const { result } = renderHook(() => useAssistantInteraction(true)); await flush();
    native.invoke.mockRejectedValue(new Error("native error"));
    await act(async () => { await result.current.release(); });
    expect(result.current.interactive).toBe(true); expect(result.current.error).toBe("暂时无法收起，请稍后重试。");
  });
});
