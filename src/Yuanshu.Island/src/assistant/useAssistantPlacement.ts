import { useCallback, useEffect, useRef, useState } from "react";
import { invoke } from "@tauri-apps/api/core";
import { getCurrentWindow } from "@tauri-apps/api/window";

export interface AssistantPlacement {
  revision: number;
  mode: "compact" | "orb";
  edge: "left" | "right" | "top" | "bottom" | null;
  dragging: boolean;
  error: string | null;
}

export function decodePlacement(value: unknown): AssistantPlacement | null {
  if (!value || typeof value !== "object" || Array.isArray(value)) return null;
  const next = value as Record<string, unknown>;
  if (Object.keys(next).sort().join(",") !== "dragging,edge,error,mode,revision" ||
      !Number.isSafeInteger(next.revision) || (next.revision as number) < 0 ||
      !["compact", "orb"].includes(next.mode as string) ||
      ![null, "left", "right", "top", "bottom"].includes(next.edge as null | string) ||
      typeof next.dragging !== "boolean" ||
      !(next.error === null || (typeof next.error === "string" && next.error.length <= 500))) return null;
  return next as unknown as AssistantPlacement;
}

/** Native placement is the only saved position; the UI only projects it. */
export function useAssistantPlacement() {
  const [placement, setPlacement] = useState<AssistantPlacement | null>(null);
  const [beginning, setBeginning] = useState(false);
  const [error, setError] = useState<string | null>(null);
  const latest = useRef<AssistantPlacement | null>(null);
  const alive = useRef(false);
  const busy = useRef(false);
  const opening = useRef(false);
  const accept = useCallback((raw: unknown) => {
    const next = decodePlacement(raw);
    if (!alive.current || !next) return;
    const previous = latest.current;
    if (previous && (next.revision < previous.revision ||
      (next.revision === previous.revision && JSON.stringify(next) !== JSON.stringify(previous)))) return;
    latest.current = next; setPlacement(next); setError(null);
  }, []);
  useEffect(() => {
    alive.current = true;
    let disposed = false;
    let unlisten: (() => void) | undefined;
    void getCurrentWindow().listen<unknown>("assistant-placement-changed", ({ payload }) => { if (!disposed) accept(payload); })
      .then(async (stop) => {
        if (disposed) { stop(); return; }
        unlisten = stop;
        const initial = await invoke<unknown>("assistant_placement_state");
        if (!disposed) accept(initial);
      }).catch(() => { /* Standalone does not depend on assistant placement. */ });
    return () => { disposed = true; alive.current = false; unlisten?.(); };
  }, [accept]);

  const beginDrag = useCallback(async () => {
    if (!alive.current || !latest.current || latest.current.dragging || busy.current) return;
    busy.current = true; setBeginning(true); setError(null);
    try { await invoke("assistant_begin_drag"); }
    catch { if (alive.current) setError("暂时无法移动，请松开鼠标后重试。"); }
    finally { busy.current = false; if (alive.current) setBeginning(false); }
  }, []);
  const openControls = useCallback(async () => {
    if (!alive.current || busy.current || latest.current?.dragging || opening.current) return;
    opening.current = true; setError(null);
    try { await invoke("assistant_open_controls"); }
    catch { if (alive.current) setError("暂时无法打开操作，请从系统托盘重试。"); }
    finally { opening.current = false; }
  }, []);
  return { placement, dragging: beginning || placement?.dragging === true, error: error ?? placement?.error ?? null, beginDrag, openControls };
}
