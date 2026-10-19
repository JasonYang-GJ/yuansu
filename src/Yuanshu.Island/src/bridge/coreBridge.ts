import type {
  IslandEvent,
  IslandPresentationInput,
  IslandStateKind,
} from "../coordinator";

export const CORE_BRIDGE_EVENT = "yuanshu-core-bridge-state";
export const CORE_BRIDGE_STATE_COMMAND = "get_yuanshu_core_bridge_state";
export const CORE_BRIDGE_SOURCE = "yuanshu.core";
const MAX_SNAPSHOT_BYTES = 1024 * 1024;
const MAX_PRESENTATIONS = 32;
const UUID_PATTERN = /^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$/u;
const UTC_TIMESTAMP_PATTERN = /^\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2}(?:\.\d{1,3})?Z$/u;
const UNSAFE_TEXT = /[\u0000-\u001f\u007f-\u009f\u061c\u200b\u200c\u200e\u200f\u202a-\u202e\u2060-\u2069\ufeff]/u;

export interface CoreBridgePresentation {
  readonly presentation_key: string;
  readonly entity_kind: "turn" | "task";
  readonly session_id: string | null;
  readonly turn_id: string | null;
  readonly task_id: string | null;
  readonly entity_version: number;
  readonly phase: string;
  readonly island_kind: Exclude<IslandStateKind, "Idle" | "Listening" | "SystemEvent" | "Media">;
  readonly title: string;
  readonly subtitle: string | null;
  readonly indicator: string | null;
  readonly is_terminal: boolean;
  readonly requires_user_action: boolean;
  readonly updated_at_utc: string;
  readonly actions: readonly [];
}

export interface CoreBridgeSnapshot {
  readonly bridge_protocol_version: 1;
  readonly server_instance_id: string;
  readonly snapshot_id: string;
  readonly snapshot_sequence: number;
  readonly generated_at_utc: string;
  readonly source: typeof CORE_BRIDGE_SOURCE;
  readonly presentations: readonly CoreBridgePresentation[];
}

export type CoreBridgeState =
  | { readonly kind: "snapshot"; readonly snapshot: CoreBridgeSnapshot }
  | { readonly kind: "clear" };

export type CoreBridgeConnectionStatus = "connected" | "disconnected";

export function coreBridgeStateToConnectionStatus(
  state: CoreBridgeState | null,
): CoreBridgeConnectionStatus {
  return state?.kind === "snapshot" ? "connected" : "disconnected";
}

function isRecord(value: unknown): value is Record<string, unknown> {
  return typeof value === "object" && value !== null && !Array.isArray(value);
}

function hasExactKeys(value: Record<string, unknown>, keys: readonly string[]) {
  const actual = Object.keys(value);
  return actual.length === keys.length &&
    keys.every((key) => Object.prototype.hasOwnProperty.call(value, key));
}

function isUuid(value: unknown): value is string {
  return typeof value === "string" &&
    UUID_PATTERN.test(value) &&
    value !== "00000000-0000-0000-0000-000000000000";
}

function isUtcTimestamp(value: unknown): value is string {
  return typeof value === "string" &&
    UTC_TIMESTAMP_PATTERN.test(value) &&
    Number.isFinite(Date.parse(value));
}

function isSafeText(value: unknown, maximumScalars: number, allowEmpty = false): value is string {
  return typeof value === "string" &&
    (allowEmpty || value.length > 0) &&
    [...value].length <= maximumScalars &&
    value.normalize("NFC") === value &&
    !UNSAFE_TEXT.test(value);
}

function expectedKind(phase: string): CoreBridgePresentation["island_kind"] | null {
  if (phase === "Understanding" || phase === "Responding") {
    return "Thinking";
  }
  if (
    phase === "WaitingForProject" ||
    phase === "WaitingForFile" ||
    phase === "WaitingForWindow" ||
    phase === "WaitingForWindowConsent" ||
    phase === "WaitingForConfirmation" ||
    phase === "WaitingForUser" || phase === "WaitingUser"
  ) {
    return "WaitingUser";
  }
  if (
    phase === "Executing" ||
    phase === "ObservingWindow" ||
    phase === "ProgrammingTask" ||
    phase === "Pending" ||
    phase === "Running" ||
    phase === "CancellationRequested"
  ) {
    return "Executing";
  }
  if (phase === "Completed" || phase === "Succeeded" || phase === "Cancelled") {
    return "Success";
  }
  if (phase === "Failed" || phase === "Interrupted") {
    return "Error";
  }
  return null;
}

function isPresentation(value: unknown): value is CoreBridgePresentation {
  if (!isRecord(value) || !hasExactKeys(value, [
    "presentation_key",
    "entity_kind",
    "session_id",
    "turn_id",
    "task_id",
    "entity_version",
    "phase",
    "island_kind",
    "title",
    "subtitle",
    "indicator",
    "is_terminal",
    "requires_user_action",
    "updated_at_utc",
    "actions",
  ])) {
    return false;
  }
  if (
    !Number.isSafeInteger(value.entity_version) ||
    (value.entity_version as number) < 0 ||
    !isSafeText(value.phase, 64) ||
    expectedKind(value.phase) !== value.island_kind ||
    !isSafeText(value.title, 160) ||
    !(value.subtitle === null || isSafeText(value.subtitle, 300, true)) ||
    !(value.indicator === null || isSafeText(value.indicator, 64, true)) ||
    typeof value.is_terminal !== "boolean" ||
    typeof value.requires_user_action !== "boolean" ||
    value.requires_user_action !== (value.island_kind === "WaitingUser") ||
    value.is_terminal !== (value.island_kind === "Success" || value.island_kind === "Error") ||
    !isUtcTimestamp(value.updated_at_utc) ||
    !Array.isArray(value.actions) ||
    value.actions.length !== 0
  ) {
    return false;
  }
  if (value.entity_kind === "turn") {
    return isUuid(value.session_id) &&
      isUuid(value.turn_id) &&
      (value.task_id === null || isUuid(value.task_id)) &&
      value.presentation_key === `turn:${value.turn_id}`;
  }
  if (value.entity_kind === "task") {
    return value.session_id === null &&
      value.turn_id === null &&
      isUuid(value.task_id) &&
      value.presentation_key === `task:${value.task_id}`;
  }
  return false;
}

function isSnapshot(value: unknown): value is CoreBridgeSnapshot {
  if (!isRecord(value) || !hasExactKeys(value, [
    "bridge_protocol_version",
    "server_instance_id",
    "snapshot_id",
    "snapshot_sequence",
    "generated_at_utc",
    "source",
    "presentations",
  ])) {
    return false;
  }
  if (
    value.bridge_protocol_version !== 1 ||
    !isUuid(value.server_instance_id) ||
    !isUuid(value.snapshot_id) ||
    !Number.isSafeInteger(value.snapshot_sequence) ||
    (value.snapshot_sequence as number) < 0 ||
    !isUtcTimestamp(value.generated_at_utc) ||
    value.source !== CORE_BRIDGE_SOURCE ||
    !Array.isArray(value.presentations) ||
    value.presentations.length > MAX_PRESENTATIONS ||
    !value.presentations.every(isPresentation)
  ) {
    return false;
  }
  const keys = value.presentations.map((presentation) => presentation.presentation_key);
  return new Set(keys).size === keys.length;
}

export function decodeCoreBridgeState(value: unknown): CoreBridgeState | null {
  try {
    if (new TextEncoder().encode(JSON.stringify(value)).length > MAX_SNAPSHOT_BYTES) {
      return null;
    }
  } catch {
    return null;
  }
  if (!isRecord(value) || typeof value.kind !== "string") {
    return null;
  }
  if (value.kind === "clear") {
    return hasExactKeys(value, ["kind"]) ? { kind: "clear" } : null;
  }
  if (
    value.kind === "snapshot" &&
    hasExactKeys(value, ["kind", "snapshot"]) &&
    isSnapshot(value.snapshot)
  ) {
    return { kind: "snapshot", snapshot: value.snapshot };
  }
  return null;
}

function toPresentation(
  snapshot: CoreBridgeSnapshot,
  presentation: CoreBridgePresentation,
): IslandPresentationInput {
  const dismiss = presentation.island_kind === "Success"
    ? { mode: "auto" as const, durationMs: 2_500 }
    : { mode: "persistent" as const };
  return {
    kind: presentation.island_kind,
    source: CORE_BRIDGE_SOURCE,
    key: presentation.presentation_key,
    title: presentation.title,
    ...(presentation.subtitle === null ? {} : { subtitle: presentation.subtitle }),
    ...(presentation.indicator === null ? {} : { indicator: presentation.indicator }),
    dismiss,
    metadata: {
      serverInstanceId: snapshot.server_instance_id,
      snapshotId: snapshot.snapshot_id,
      entityKind: presentation.entity_kind,
      sessionId: presentation.session_id,
      turnId: presentation.turn_id,
      taskId: presentation.task_id,
      entityVersion: presentation.entity_version,
      phase: presentation.phase,
      updatedAtUtc: presentation.updated_at_utc,
    },
  } as IslandPresentationInput;
}

export function coreBridgePayloadToIslandEvent(value: unknown, at = Date.now()): IslandEvent {
  const decoded = decodeCoreBridgeState(value);
  return {
    type: "replace-source",
    source: CORE_BRIDGE_SOURCE,
    at,
    presentations: decoded?.kind === "snapshot"
      ? decoded.snapshot.presentations.map((presentation) =>
          toPresentation(decoded.snapshot, presentation))
      : [],
  };
}
