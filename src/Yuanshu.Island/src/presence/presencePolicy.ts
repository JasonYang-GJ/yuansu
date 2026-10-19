import type { IslandPresentation } from "../coordinator";
import type { IslandMode, IslandShellPolicy } from "../hooks/useIslandController";

export type PresenceLevel = "active" | "peek" | "hidden-fullscreen";

export const PAUSED_ACTIVE_GRACE_MS = 5_000;

export interface PresenceShellPolicy
  extends Pick<IslandShellPolicy, "wake" | "allowIdleCollapse"> {
  readonly preferredMode: Extract<IslandMode, "compact" | "orb">;
}

export function derivePresence(
  presentation: IslandPresentation,
  fullscreen: boolean,
  now: number,
): PresenceLevel {
  if (fullscreen) {
    return "hidden-fullscreen";
  }
  if (presentation.kind === "Idle") {
    return "peek";
  }
  if (presentation.kind !== "Media") {
    return "active";
  }
  if (presentation.indicator === "Playing") {
    return "active";
  }
  return now - presentation.updatedAt < PAUSED_ACTIVE_GRACE_MS ? "active" : "peek";
}

export function presenceShellPolicy(level: PresenceLevel): PresenceShellPolicy {
  if (level === "active") {
    return { wake: "compact", allowIdleCollapse: false, preferredMode: "compact" };
  }
  if (level === "peek") {
    return { wake: "none", allowIdleCollapse: true, preferredMode: "orb" };
  }
  return { wake: "none", allowIdleCollapse: false, preferredMode: "compact" };
}
