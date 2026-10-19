import { useCallback, useEffect, useRef, useState } from "react";
import { invoke } from "@tauri-apps/api/core";
import { getCurrentWindow } from "@tauri-apps/api/window";

export interface AssistantSurfaceState {
  interactive: boolean;
  shortcutAvailable: boolean;
  revision: number;
}

export function decodeSurfaceState(value: unknown): AssistantSurfaceState | null {
  if (!value || typeof value !== "object" || Array.isArray(value)) return null;
  const state = value as Record<string, unknown>;
  if (Object.keys(state).sort().join(",") !== "interactive,revision,shortcutAvailable" ||
      typeof state.interactive !== "boolean" || typeof state.shortcutAvailable !== "boolean" ||
      !Number.isSafeInteger(state.revision) || (state.revision as number) < 0) return null;
  return state as unknown as AssistantSurfaceState;
}

/** Native hit-testing is authoritative. CSS visibility never grants interaction. */
export function useAssistantInteraction(integrated: boolean) {
  const [state, setState] = useState<AssistantSurfaceState | null>(null);
  const [error, setError] = useState<string | null>(null);
  const latest = useRef<AssistantSurfaceState | null>(null);
  const mounted = useRef(false);
  const releasing = useRef(false);

  const accept = useCallback((raw: unknown) => {
    const next = decodeSurfaceState(raw);
    if (!mounted.current || !next) return;
    const previous = latest.current;
    if (previous && (next.revision < previous.revision ||
        (next.revision === previous.revision &&
          (next.interactive !== previous.interactive || next.shortcutAvailable !== previous.shortcutAvailable)))) return;
    latest.current = next;
    setState(next);
    setError(null);
  }, []);

  useEffect(() => {
    if (!integrated) return;
    mounted.current = true;
    let disposed = false;
    let unlisten: (() => void) | undefined;
    void getCurrentWindow().listen<unknown>("assistant-surface-changed", ({ payload }) => {
      if (!disposed) accept(payload);
    }).then(async (stop) => {
      if (disposed) { stop(); return; }
      unlisten = stop;
      // Subscribe first: an entry event can overtake this initial state reply.
      const initial = await invoke<unknown>("assistant_surface_state");
      if (!disposed) accept(initial);
    }).catch(() => {
      if (!disposed) setError("操作入口暂未连接，请从托盘重新打开。");
    });
    return () => { disposed = true; mounted.current = false; unlisten?.(); };
  }, [accept, integrated]);

  const release = useCallback(async () => {
    if (!mounted.current || !latest.current?.interactive || releasing.current) return;
    releasing.current = true;
    try { accept(await invoke<unknown>("assistant_release_controls")); }
    catch { if (mounted.current) setError("暂时无法收起，请稍后重试。"); }
    finally { releasing.current = false; }
  }, [accept]);

  return { interactive: state?.interactive === true, shortcutAvailable: state?.shortcutAvailable === true, error, release };
}
