import { useEffect, useState } from "react";
import { invoke } from "@tauri-apps/api/core";
import { getCurrentWindow } from "@tauri-apps/api/window";
import type { IslandPresentation } from "../coordinator";
import {
  derivePresence,
  PAUSED_ACTIVE_GRACE_MS,
  type PresenceLevel,
} from "./presencePolicy";

export const FULLSCREEN_PRESENCE_EVENT = "fullscreen-presence-changed";
export const FULLSCREEN_PRESENCE_COMMAND = "get_fullscreen_presence";

export function usePresenceProjection(presentation: IslandPresentation): PresenceLevel {
  const [fullscreen, setFullscreen] = useState(false);
  const [clockTick, setClockTick] = useState(() => Date.now());

  useEffect(() => {
    let disposed = false;
    let eventRevision = 0;
    let stopListening: (() => void) | undefined;

    void getCurrentWindow()
      .listen<unknown>(FULLSCREEN_PRESENCE_EVENT, ({ payload }) => {
        eventRevision += 1;
        if (!disposed && typeof payload === "boolean") {
          setFullscreen(payload);
        }
      })
      .then((stop) => {
        if (disposed) {
          stop();
          return;
        }
        stopListening = stop;
        const snapshotRevision = eventRevision;
        return invoke<unknown>(FULLSCREEN_PRESENCE_COMMAND).then((payload) => {
          if (
            !disposed &&
            eventRevision === snapshotRevision &&
            typeof payload === "boolean"
          ) {
            setFullscreen(payload);
          }
        });
      })
      .catch(() => {
        // Native presence is a display-only projection. Failure leaves content visible.
      });

    return () => {
      disposed = true;
      stopListening?.();
    };
  }, []);

  useEffect(() => {
    const now = Date.now();
    setClockTick(now);
    if (
      fullscreen ||
      presentation.kind !== "Media" ||
      presentation.indicator !== "Paused"
    ) {
      return;
    }

    const remaining = Math.max(
      0,
      presentation.updatedAt + PAUSED_ACTIVE_GRACE_MS - now,
    );
    const timer = window.setTimeout(() => setClockTick(Date.now()), remaining);
    return () => window.clearTimeout(timer);
  }, [fullscreen, presentation.indicator, presentation.kind, presentation.updatedAt]);

  return derivePresence(presentation, fullscreen, clockTick);
}
