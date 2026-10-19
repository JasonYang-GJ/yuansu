import { useEffect } from "react";
import { invoke } from "@tauri-apps/api/core";
import { getCurrentWindow } from "@tauri-apps/api/window";
import type { IslandCoordinator, IslandEvent } from "../coordinator";

export const WINDOWS_MEDIA_EVENT = "windows-media-changed";
export const WINDOWS_MEDIA_SOURCE = "windows.media";
export const WINDOWS_MEDIA_KEY = "current-session";
export const WINDOWS_MEDIA_EVENT_ID = "windows.media.current-session";
export const WINDOWS_MEDIA_STATE_COMMAND = "get_windows_media_state";
export const WINDOWS_MEDIA_TITLE_LIMIT = 160;
export const WINDOWS_MEDIA_ARTIST_LIMIT = 120;
export const WINDOWS_MEDIA_APP_ID_LIMIT = 160;
const UNSAFE_MEDIA_TEXT = /[\u0000-\u001f\u007f-\u009f\u061c\u200b\u200e\u200f\u202a-\u202e\u2060-\u2069\ufeff]/u;

interface WindowsMediaIdentity {
  readonly source: typeof WINDOWS_MEDIA_SOURCE;
  readonly key: typeof WINDOWS_MEDIA_KEY;
  readonly eventId: typeof WINDOWS_MEDIA_EVENT_ID;
  readonly timestampMs: number;
}

export interface WindowsMediaUpdatePayload extends WindowsMediaIdentity {
  readonly kind: "update";
  readonly title: string;
  readonly artist: string;
  readonly playbackState: "playing" | "paused";
  readonly appId: string;
}

export interface WindowsMediaClearPayload extends WindowsMediaIdentity {
  readonly kind: "clear";
}

export type WindowsMediaPayload = WindowsMediaUpdatePayload | WindowsMediaClearPayload;

function isBoundedText(value: unknown, limit: number) {
  return typeof value === "string" &&
    value.length > 0 &&
    [...value].length <= limit &&
    !UNSAFE_MEDIA_TEXT.test(value);
}

function hasValidIdentity(payload: Record<string, unknown>) {
  return payload.source === WINDOWS_MEDIA_SOURCE &&
    payload.key === WINDOWS_MEDIA_KEY &&
    payload.eventId === WINDOWS_MEDIA_EVENT_ID &&
    Number.isSafeInteger(payload.timestampMs) &&
    (payload.timestampMs as number) >= 0;
}

export function isWindowsMediaPayload(value: unknown): value is WindowsMediaPayload {
  if (typeof value !== "object" || value === null) {
    return false;
  }

  const payload = value as Record<string, unknown>;
  if (!hasValidIdentity(payload)) {
    return false;
  }
  if (payload.kind === "clear") {
    return true;
  }
  return payload.kind === "update" &&
    isBoundedText(payload.title, WINDOWS_MEDIA_TITLE_LIMIT) &&
    isBoundedText(payload.artist, WINDOWS_MEDIA_ARTIST_LIMIT) &&
    isBoundedText(payload.appId, WINDOWS_MEDIA_APP_ID_LIMIT) &&
    (payload.playbackState === "playing" || payload.playbackState === "paused");
}

export function windowsMediaToIslandEvent(payload: unknown): IslandEvent | null {
  if (!isWindowsMediaPayload(payload)) {
    return null;
  }
  if (payload.kind === "clear") {
    return {
      type: "remove",
      source: payload.source,
      key: payload.key,
      at: payload.timestampMs,
    };
  }

  const stateLabel = payload.playbackState === "playing" ? "Playing" : "Paused";
  return {
    type: "upsert",
    at: payload.timestampMs,
    presentation: {
      kind: "Media",
      source: payload.source,
      key: payload.key,
      title: payload.title,
      subtitle: payload.artist,
      indicator: stateLabel,
      dismiss: { mode: "persistent" },
      metadata: {
        artist: payload.artist,
        playbackState: payload.playbackState,
        appId: payload.appId,
        eventId: payload.eventId,
        timestampMs: payload.timestampMs,
      },
    },
  };
}

export function useWindowsMediaProvider(coordinator: IslandCoordinator, enabled = true) {
  useEffect(() => {
    if (!enabled) {
      coordinator.dispatch({ type: "clear-source", source: "windows.media" });
      return;
    }
    let disposed = false;
    let stopListening: (() => void) | undefined;

    void getCurrentWindow()
      .listen<unknown>(WINDOWS_MEDIA_EVENT, ({ payload }) => {
        const event = windowsMediaToIslandEvent(payload);
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
        return invoke<unknown>(WINDOWS_MEDIA_STATE_COMMAND);
      })
      .then((payload) => {
        const event = windowsMediaToIslandEvent(payload);
        if (!disposed && event !== null) {
          coordinator.dispatch(event);
        }
      })
      .catch(() => {
        // A bridge failure must not bypass the Coordinator or disturb other providers.
      });

    return () => {
      disposed = true;
      stopListening?.();
    };
  }, [coordinator, enabled]);
}
