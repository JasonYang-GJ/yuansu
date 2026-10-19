import { act, fireEvent, render, renderHook, screen } from "@testing-library/react";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import { decodePlacement, useAssistantPlacement, type AssistantPlacement } from "./useAssistantPlacement";
import { useAssistantDragHandle } from "./useAssistantDragHandle";

const native = vi.hoisted(() => ({ invoke: vi.fn(), handler: undefined as undefined | ((event: { payload: unknown }) => void) }));
vi.mock("@tauri-apps/api/core", () => ({ invoke: native.invoke }));
vi.mock("@tauri-apps/api/window", () => ({ getCurrentWindow: () => ({ listen: async (_name: string, handler: typeof native.handler) => { native.handler = handler; return () => {}; } }) }));
const initial: AssistantPlacement = { revision: 1, mode: "compact", edge: null, dragging: false, error: null };
function deferred<T>() { let resolve!: (value: T) => void; const promise = new Promise<T>((done) => { resolve = done; }); return { promise, resolve }; }
async function flush() { await act(async () => { await Promise.resolve(); await Promise.resolve(); }); }
beforeEach(() => { native.invoke.mockReset(); native.invoke.mockResolvedValue(initial); native.handler = undefined; vi.stubGlobal("PointerEvent", MouseEvent); });
afterEach(() => vi.unstubAllGlobals());

describe("native assistant placement", () => {
  it("rejects unknown fields, modes, edges and malformed values", () => {
    expect(decodePlacement(initial)).toEqual(initial);
    for (const change of [{ mode: "expanded" }, { edge: "other" }, { dragging: 1 }, { revision: -1 }, { revision: 1.5 }, { error: "字".repeat(501) }, { x: 5 }]) {
      expect(decodePlacement({ ...initial, ...change })).toBeNull();
    }
  });

  it("does not let a delayed initial position or stale event replace a newer drag", async () => {
    const pending = deferred<unknown>(); native.invoke.mockReturnValue(pending.promise);
    const { result } = renderHook(useAssistantPlacement); await flush();
    act(() => { native.handler?.({ payload: { ...initial, revision: 3, dragging: true } }); });
    await act(async () => pending.resolve(initial));
    expect(result.current.dragging).toBe(true);
    act(() => { native.handler?.({ payload: { ...initial, revision: 2, mode: "orb", edge: "left" } }); });
    expect(result.current.placement?.mode).toBe("compact");
    act(() => { native.handler?.({ payload: { ...initial, revision: 4, mode: "orb", edge: "left" } }); });
    expect(result.current.dragging).toBe(false); expect(result.current.placement?.mode).toBe("orb");
  });

  it("freezes immediately, sends one drag and never opens controls during the native gesture", async () => {
    const { result } = renderHook(useAssistantPlacement); await flush();
    const drag = deferred<unknown>(); native.invoke.mockReturnValue(drag.promise);
    let request!: Promise<void>;
    await act(async () => { request = result.current.beginDrag(); void result.current.beginDrag(); void result.current.openControls(); });
    expect(result.current.dragging).toBe(true);
    expect(native.invoke.mock.calls.filter(([name]) => name === "assistant_begin_drag")).toHaveLength(1);
    expect(native.invoke.mock.calls.some(([name]) => name === "assistant_open_controls")).toBe(false);
    await act(async () => { drag.resolve(null); await request; });
    expect(result.current.dragging).toBe(false);
  });

  it("reports a failed native drag without leaving the projection frozen", async () => {
    const { result } = renderHook(useAssistantPlacement); await flush(); native.invoke.mockRejectedValue(new Error("blocked"));
    await act(async () => { await result.current.beginDrag(); });
    expect(result.current.dragging).toBe(false); expect(result.current.error).toContain("松开鼠标后重试");
  });
});

function Handle({ drag, open, header = false }: { drag: () => Promise<void>; open: () => Promise<void>; header?: boolean }) {
  const props = useAssistantDragHandle(true, drag, header ? undefined : open, header ? 88 : 0);
  return header ? <header data-testid="handle" {...props}><span>标题</span><button type="button">控件</button></header> : <button type="button" {...props}>移动元枢</button>;
}
describe("assistant drag gestures", () => {
  it("starts only after four pixels, suppresses the drag click, and opens on a new short click", async () => {
    const drag = vi.fn(async () => {}), open = vi.fn(async () => {}); render(<Handle drag={drag} open={open} />);
    const button = screen.getByRole("button");
    fireEvent.pointerDown(button, { button: 0, buttons: 1, clientX: 10, clientY: 10 });
    fireEvent.pointerMove(button, { buttons: 1, clientX: 13, clientY: 10 }); expect(drag).not.toHaveBeenCalled();
    fireEvent.pointerMove(button, { buttons: 1, clientX: 14, clientY: 10 });
    fireEvent.pointerMove(button, { buttons: 1, clientX: 30, clientY: 10 });
    fireEvent.pointerUp(button); fireEvent.click(button, { detail: 1 });
    expect(drag).toHaveBeenCalledTimes(1); expect(open).not.toHaveBeenCalled();
    fireEvent.pointerDown(button, { button: 0, buttons: 1 }); fireEvent.pointerUp(button); fireEvent.click(button, { detail: 1 });
    expect(open).toHaveBeenCalledTimes(1);
  });

  it("allows keyboard activation after dragging and rejects right-button movement", () => {
    const drag = vi.fn(async () => {}), open = vi.fn(async () => {}); render(<Handle drag={drag} open={open} />);
    const button = screen.getByRole("button");
    fireEvent.pointerDown(button, { button: 2, buttons: 2, clientX: 10 }); fireEvent.pointerMove(button, { buttons: 2, clientX: 30 });
    expect(drag).not.toHaveBeenCalled();
    fireEvent.pointerDown(button, { button: 0, buttons: 1, clientX: 10 }); fireEvent.pointerMove(button, { buttons: 1, clientX: 30 });
    fireEvent.click(button, { detail: 0 }); expect(open).toHaveBeenCalledTimes(1);
  });

  it("never drags buttons or the header control zone", () => {
    const drag = vi.fn(async () => {}), open = vi.fn(async () => {}); render(<Handle drag={drag} open={open} header />);
    const header = screen.getByTestId("handle"); header.getBoundingClientRect = () => ({ left: 0, right: 480, x: 0, y: 0, top: 0, bottom: 64, width: 480, height: 64, toJSON: () => ({}) });
    fireEvent.pointerDown(screen.getByRole("button"), { button: 0, buttons: 1, clientX: 20 }); fireEvent.pointerMove(header, { buttons: 1, clientX: 30 });
    fireEvent.pointerDown(header, { button: 0, buttons: 1, clientX: 420 }); fireEvent.pointerMove(header, { buttons: 1, clientX: 430 });
    expect(drag).not.toHaveBeenCalled();
    fireEvent.pointerDown(screen.getByText("标题"), { button: 0, buttons: 1, clientX: 20 }); fireEvent.pointerMove(header, { buttons: 1, clientX: 30 });
    expect(drag).toHaveBeenCalledTimes(1); expect(open).not.toHaveBeenCalled();
  });
});
