import { useCallback, useEffect, useRef, useState } from "react";
import type { MouseEvent as ReactMouseEvent } from "react";
import { invoke } from "@tauri-apps/api/core";
import { getCurrentWindow } from "@tauri-apps/api/window";
import type { IslandVisibilityPolicy } from "../coordinator";

export type IslandMode = "compact" | "expanded" | "orb" | "tucked";

export const ISLAND_IDLE_DELAYS = {
  compactToOrb: 6_000,
  orbToTucked: 2_200,
  awakenedOrbToTucked: 1_200,
} as const;

const WINDOW_COMMAND = "set_island_mode";
const STARTUP_PRESENCE_COMMAND = "get_startup_low_presence";
const MODE_RESET_EVENT = "island-mode-reset";
const ATTENTION_WAKE_RETRY_DELAY_MS = 350;
const ATTENTION_WAKE_RETRY_LIMIT = 2;

export interface IslandShellPolicy extends IslandVisibilityPolicy {
  /** Changes only when the semantic presentation changes, not for progress updates. */
  wakeToken: string;
  /** Low-presence transitions may request the existing orb without changing semantics. */
  preferredMode?: Extract<IslandMode, "compact" | "orb">;
  /** The voice surface remains readable while actions run in another window. */
  preserveExpandedOnBlur?: boolean;
}

const DEFAULT_SHELL_POLICY: IslandShellPolicy = {
  wake: "none",
  allowIdleCollapse: true,
  wakeToken: "coordinator\u0000idle\u0000Idle",
  preferredMode: "orb",
};

function isIslandMode(value: unknown): value is IslandMode {
  return value === "compact" || value === "expanded" || value === "orb" || value === "tucked";
}

function isIdleControlHovered() {
  try {
    return document.querySelector<HTMLElement>(".island-idle")?.matches(":hover") ?? false;
  } catch {
    return false;
  }
}

export function useIslandController(policy: IslandShellPolicy = DEFAULT_SHELL_POLICY) {
  const [mode, setMode] = useState<IslandMode>("compact");
  const [synchronizedMode, setSynchronizedMode] = useState<IslandMode | null>("compact");
  const [syncError, setSyncError] = useState<string | null>(null);
  const modeRef = useRef<IslandMode>("compact");
  const pointerInsideRef = useRef(false);
  const awakenedFromTuckedRef = useRef(false);
  const idleTimerRef = useRef<number | null>(null);
  const idleTimerVersionRef = useRef(0);
  const requestVersionRef = useRef(0);
  const mountedRef = useRef(true);
  const allowIdleCollapseRef = useRef(policy.allowIdleCollapse);
  const preserveExpandedOnBlurRef = useRef(policy.preserveExpandedOnBlur);
  const previousAllowIdleCollapseRef = useRef(policy.allowIdleCollapse);
  const wakeRetryCountRef = useRef(0);
  const wakeRetryTimerRef = useRef<number | null>(null);
  const requestModeRef = useRef<(nextMode: IslandMode) => void>(() => undefined);

  allowIdleCollapseRef.current = policy.allowIdleCollapse;
  preserveExpandedOnBlurRef.current = policy.preserveExpandedOnBlur;

  const clearIdleTimer = useCallback(() => {
    idleTimerVersionRef.current += 1;
    if (idleTimerRef.current !== null) {
      window.clearTimeout(idleTimerRef.current);
      idleTimerRef.current = null;
    }
  }, []);

  const clearWakeRetry = useCallback(() => {
    if (wakeRetryTimerRef.current !== null) {
      window.clearTimeout(wakeRetryTimerRef.current);
      wakeRetryTimerRef.current = null;
    }
  }, []);

  const armIdleTimer = useCallback(
    (forMode: IslandMode, delayOverride?: number) => {
      clearIdleTimer();

      if (!allowIdleCollapseRef.current) {
        return;
      }

      let delay: number | null = null;
      if (forMode === "compact") {
        delay = ISLAND_IDLE_DELAYS.compactToOrb;
      } else if (forMode === "orb" && !pointerInsideRef.current) {
        delay =
          delayOverride ??
          (awakenedFromTuckedRef.current
            ? ISLAND_IDLE_DELAYS.awakenedOrbToTucked
            : ISLAND_IDLE_DELAYS.orbToTucked);
      }

      if (delay === null) {
        return;
      }

      const timerVersion = idleTimerVersionRef.current;
      idleTimerRef.current = window.setTimeout(() => {
        if (
          timerVersion !== idleTimerVersionRef.current ||
          modeRef.current !== forMode ||
          !allowIdleCollapseRef.current
        ) {
          return;
        }

        idleTimerRef.current = null;
        if (forMode === "compact") {
          awakenedFromTuckedRef.current = false;
          requestModeRef.current("orb");
          return;
        }

        if (awakenedFromTuckedRef.current && isIdleControlHovered()) {
          pointerInsideRef.current = true;
          return;
        }

        awakenedFromTuckedRef.current = false;
        requestModeRef.current("tucked");
      }, delay);
    },
    [clearIdleTimer],
  );

  const requestMode = useCallback(
    (nextMode: IslandMode) => {
      const previousMode = modeRef.current;
      if (previousMode === nextMode) {
        armIdleTimer(nextMode);
        return;
      }

      const previousWakeSource = awakenedFromTuckedRef.current;
      const requestVersion = requestVersionRef.current + 1;
      requestVersionRef.current = requestVersion;

      if (nextMode !== "orb") {
        awakenedFromTuckedRef.current = false;
      }

      modeRef.current = nextMode;
      setSynchronizedMode(null);
      setMode(nextMode);
      setSyncError(null);
      armIdleTimer(nextMode);

      void invoke(WINDOW_COMMAND, { mode: nextMode })
        .then(() => {
          if (mountedRef.current && requestVersion === requestVersionRef.current && modeRef.current === nextMode) {
            setSynchronizedMode(nextMode);
          }
          if (
            nextMode === "compact" &&
            requestVersion === requestVersionRef.current &&
            modeRef.current === nextMode
          ) {
            wakeRetryCountRef.current = 0;
            clearWakeRetry();
          }
        })
        .catch(() => {
          if (
            !mountedRef.current ||
            requestVersion !== requestVersionRef.current ||
            modeRef.current !== nextMode
          ) {
            return;
          }

          awakenedFromTuckedRef.current = previousWakeSource;
          modeRef.current = previousMode;
          setMode(previousMode);
          setSyncError("窗口模式同步失败，请重试");
          armIdleTimer(previousMode);

          const shouldRetryAttentionWake =
            nextMode === "compact" &&
            (previousMode === "orb" || previousMode === "tucked") &&
            !allowIdleCollapseRef.current &&
            wakeRetryCountRef.current < ATTENTION_WAKE_RETRY_LIMIT;
          if (shouldRetryAttentionWake) {
            wakeRetryCountRef.current += 1;
            clearWakeRetry();
            wakeRetryTimerRef.current = window.setTimeout(() => {
              wakeRetryTimerRef.current = null;
              if (
                mountedRef.current &&
                !allowIdleCollapseRef.current &&
                (modeRef.current === "orb" || modeRef.current === "tucked")
              ) {
                requestModeRef.current("compact");
              }
            }, ATTENTION_WAKE_RETRY_DELAY_MS);
          }
        });
    },
    [armIdleTimer, clearWakeRetry],
  );

  requestModeRef.current = requestMode;

  useEffect(() => {
    const previouslyAllowed = previousAllowIdleCollapseRef.current;
    previousAllowIdleCollapseRef.current = policy.allowIdleCollapse;
    wakeRetryCountRef.current = 0;
    clearWakeRetry();

    if (!policy.allowIdleCollapse) {
      clearIdleTimer();
    } else {
      armIdleTimer(modeRef.current);
    }

    if (
      policy.wake === "compact" &&
      (modeRef.current === "orb" || modeRef.current === "tucked")
    ) {
      requestModeRef.current("compact");
    } else if (
      policy.allowIdleCollapse &&
      !previouslyAllowed &&
      policy.preferredMode === "orb" &&
      modeRef.current === "compact"
    ) {
      requestModeRef.current("orb");
    }
  }, [
    armIdleTimer,
    clearIdleTimer,
    clearWakeRetry,
    policy.allowIdleCollapse,
    policy.wake,
    policy.wakeToken,
    policy.preferredMode,
  ]);

  const expand = useCallback(() => requestMode("expanded"), [requestMode]);
  const collapse = useCallback(() => requestMode("compact"), [requestMode]);

  const markActivity = useCallback(() => {
    if (modeRef.current === "compact") {
      armIdleTimer("compact");
    }
  }, [armIdleTimer]);

  const handlePointerEnter = useCallback(() => {
    pointerInsideRef.current = true;

    if (modeRef.current === "tucked") {
      awakenedFromTuckedRef.current = true;
      requestMode("orb");
      return;
    }

    if (modeRef.current === "orb") {
      clearIdleTimer();
      return;
    }

    markActivity();
  }, [clearIdleTimer, markActivity, requestMode]);

  const handlePointerLeave = useCallback((event: ReactMouseEvent<HTMLElement>) => {
    const stillInsideViewport =
      event.clientX >= 0 &&
      event.clientX < window.innerWidth &&
      event.clientY >= 0 &&
      event.clientY < window.innerHeight;

    if (
      modeRef.current === "orb" &&
      awakenedFromTuckedRef.current &&
      stillInsideViewport
    ) {
      pointerInsideRef.current = true;
      clearIdleTimer();
      return;
    }

    pointerInsideRef.current = false;

    if (modeRef.current === "orb") {
      armIdleTimer(
        "orb",
        awakenedFromTuckedRef.current
          ? ISLAND_IDLE_DELAYS.awakenedOrbToTucked
          : ISLAND_IDLE_DELAYS.orbToTucked,
      );
    }
  }, [armIdleTimer, clearIdleTimer]);

  const syncModeFromBackend = useCallback(
    (nextMode: IslandMode) => {
      requestVersionRef.current += 1;
      pointerInsideRef.current = false;
      awakenedFromTuckedRef.current = false;
      modeRef.current = nextMode;
      setMode(nextMode);
      setSynchronizedMode(nextMode);
      setSyncError(null);
      armIdleTimer(nextMode);
    },
    [armIdleTimer],
  );

  useEffect(() => {
    let disposed = false;
    void invoke<unknown>(STARTUP_PRESENCE_COMMAND)
      .then((lowPresence) => {
        if (!disposed && lowPresence === true) {
          syncModeFromBackend("tucked");
        }
      })
      .catch(() => {
        // A missing startup hint keeps the ordinary compact startup behavior.
      });
    return () => {
      disposed = true;
    };
  }, [syncModeFromBackend]);

  useEffect(() => {
    mountedRef.current = true;
    armIdleTimer(modeRef.current);

    return () => {
      mountedRef.current = false;
      clearIdleTimer();
      clearWakeRetry();
    };
  }, [armIdleTimer, clearIdleTimer, clearWakeRetry]);

  useEffect(() => {
    let disposed = false;
    let stopFocusListener: (() => void) | undefined;
    let stopResetListener: (() => void) | undefined;
    const appWindow = getCurrentWindow();

    void appWindow
      .onFocusChanged(({ payload: focused }) => {
        if (!focused && modeRef.current === "expanded" && !preserveExpandedOnBlurRef.current) {
          collapse();
        }
      })
      .then((stopListening) => {
        if (disposed) {
          stopListening();
          return;
        }
        stopFocusListener = stopListening;
      })
      .catch(() => {
        if (!disposed && mountedRef.current) {
          setSyncError("窗口焦点监听未启动");
        }
      });

    void appWindow
      .listen<string>(MODE_RESET_EVENT, ({ payload }) => {
        if (isIslandMode(payload)) {
          syncModeFromBackend(payload);
        }
      })
      .then((stopListening) => {
        if (disposed) {
          stopListening();
          return;
        }
        stopResetListener = stopListening;
      })
      .catch(() => {
        if (!disposed && mountedRef.current) {
          setSyncError("窗口模式监听未启动");
        }
      });

    return () => {
      disposed = true;
      stopFocusListener?.();
      stopResetListener?.();
    };
  }, [collapse, syncModeFromBackend]);

  return {
    mode,
    modeSynchronized: mode === synchronizedMode,
    expand,
    collapse,
    markActivity,
    handlePointerEnter,
    handlePointerLeave,
    syncError,
  };
}
