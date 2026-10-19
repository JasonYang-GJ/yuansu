export const ASSISTANT_PHASES = ["idle", "listening", "transcribing", "recognized", "working", "speaking", "waiting", "success", "error", "standby", "paused", "stopping"] as const;
export type AssistantPhase = typeof ASSISTANT_PHASES[number];
export type AssistantAction = "start" | "pause" | "stop" | "management" | "memory" | "exit";
export interface AssistantSnapshot {
  protocolVersion: 1;
  instanceId: string;
  revision: number;
  controlToken: string;
  phase: AssistantPhase;
  status: string;
  transcript: string;
  detail: string;
  microphoneOn: boolean;
  canStart: boolean;
  canPause: boolean;
  canStop: boolean;
  needsAttention: boolean;
  updatedAtUtc: string;
}
export interface AssistantResponse { ok: boolean; error: string | null; snapshot: AssistantSnapshot | null }
const UUID = /^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$/i;
const fields = ["protocolVersion", "instanceId", "revision", "controlToken", "phase", "status", "transcript", "detail", "microphoneOn", "canStart", "canPause", "canStop", "needsAttention", "updatedAtUtc"];
function record(value: unknown): value is Record<string, unknown> { return typeof value === "object" && value !== null && !Array.isArray(value); }
export function decodeAssistantSnapshot(value: unknown): AssistantSnapshot | null {
  if (!record(value) || Object.keys(value).length !== fields.length || fields.some((field) => !(field in value))) return null;
  if (value.protocolVersion !== 1 || typeof value.instanceId !== "string" || !UUID.test(value.instanceId) ||
      typeof value.controlToken !== "string" || !UUID.test(value.controlToken) ||
      !Number.isSafeInteger(value.revision) || (value.revision as number) < 0 ||
      !ASSISTANT_PHASES.includes(value.phase as AssistantPhase)) return null;
  for (const [key, limit] of [["status", 160], ["transcript", 2000], ["detail", 4000]] as const) {
    if (typeof value[key] !== "string" || value[key].length > limit) return null;
  }
  if (["microphoneOn", "canStart", "canPause", "canStop", "needsAttention"].some((key) => typeof value[key] !== "boolean")) return null;
  if (typeof value.updatedAtUtc !== "string" || !/^\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2}(?:\.\d{1,7})?(?:Z|\+00:00)$/.test(value.updatedAtUtc) || !Number.isFinite(Date.parse(value.updatedAtUtc))) return null;
  return value as unknown as AssistantSnapshot;
}
export function decodeAssistantResponse(value: unknown): AssistantResponse | null {
  if (!record(value) || Object.keys(value).length !== 3 || typeof value.ok !== "boolean" ||
      !(value.error === null || (typeof value.error === "string" && /^[a-z0-9_-]{1,80}$/i.test(value.error)))) return null;
  const snapshot = value.snapshot === null ? null : decodeAssistantSnapshot(value.snapshot);
  if (value.snapshot !== null && snapshot === null) return null;
  return { ok: value.ok, error: value.error, snapshot };
}
export function actionAvailable(snapshot: AssistantSnapshot, action: AssistantAction) {
  switch (action) {
    case "start": return snapshot.canStart;
    case "pause": return snapshot.canPause;
    case "stop": return snapshot.canStop;
    case "management": case "memory": case "exit": return true;
    default: return false;
  }
}
export function needsExpandedSurface(snapshot: AssistantSnapshot) {
  return ["transcribing", "recognized", "working", "speaking", "waiting", "stopping"].includes(snapshot.phase) || snapshot.needsAttention;
}
