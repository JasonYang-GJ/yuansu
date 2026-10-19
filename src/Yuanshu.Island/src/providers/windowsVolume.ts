import { useEffect } from "react";
import { getCurrentWindow } from "@tauri-apps/api/window";
import type { IslandCoordinator, IslandEvent } from "../coordinator";

export const WINDOWS_VOLUME_EVENT = "windows-volume-changed";
export const WINDOWS_VOLUME_SOURCE = "windows.volume";
export const WINDOWS_VOLUME_KEY = "default-playback";
export const WINDOWS_VOLUME_EVENT_ID = "windows.volume.default-playback";
export const WINDOWS_VOLUME_TTL_MS = 2_000;

export interface WindowsVolumePayload {
  readonly volume: number;
  readonly muted: boolean;
  readonly source: typeof WINDOWS_VOLUME_SOURCE;
  readonly key: typeof WINDOWS_VOLUME_KEY;
  readonly eventId: typeof WINDOWS_VOLUME_EVENT_ID;
  readonly timestampMs: number;
}

export function isWindowsVolumePayload(value: unknown): value is WindowsVolumePayload {
  if (typeof value !== "object" || value === null) {
    return false;
  }

  const payload = value as Record<string, unknown>;
  return Number.isInteger(payload.volume) &&
    (payload.volume as number) >= 0 &&
    (payload.volume as number) <= 100 &&
    typeof payload.muted === "boolean" &&
    payload.source === WINDOWS_VOLUME_SOURCE &&
    payload.key === WINDOWS_VOLUME_KEY &&
    payload.eventId === WINDOWS_VOLUME_EVENT_ID &&
    Number.isInteger(payload.timestampMs) &&
    (payload.timestampMs as number) >= 0;
}

export function windowsVolumeToIslandEvent(payload: unknown): IslandEvent | null {
  if (!isWindowsVolumePayload(payload)) {
    return null;
  }

  return {
    type: "upsert",
    presentation: {
      kind: "SystemEvent",
      source: payload.source,
      key: payload.key,
      title: "音量",
      subtitle: payload.muted ? "Muted" : `${payload.volume}%`,
      indicator: payload.muted ? "Muted" : `音量 ${payload.volume}%`,
      category: "volume",
      dismiss: { mode: "auto", durationMs: WINDOWS_VOLUME_TTL_MS },
      metadata: {
        volume: payload.volume,
        muted: payload.muted,
        eventId: payload.eventId,
        timestampMs: payload.timestampMs,
      },
    },
  };
}

export function useWindowsVolumeProvider(coordinator: IslandCoordinator, enabled = true) {
  useEffect(() => {
    if (!enabled) {
      coordinator.dispatch({ type: "clear-source", source: "windows.volume" });
      return;
    }
    let disposed = false;
    let stopListening: (() => void) | undefined;

    void getCurrentWindow()
      .listen<unknown>(WINDOWS_VOLUME_EVENT, ({ payload }) => {
        const event = windowsVolumeToIslandEvent(payload);
        if (!disposed && event !== null) {
          coordinator.dispatch(event);
        }
      })
      .then((stop) => {
        if (disposed) {
          stop();
          return;
        }
        stopListening = stop;
      })
      .catch(() => {
        // A bridge failure must not bypass the Coordinator or disturb Stage 0 controls.
      });

    return () => {
      disposed = true;
      stopListening?.();
    };
  }, [coordinator, enabled]);
}
