import { ISLAND_STATE_KINDS } from "./types";
import type {
  IslandCompletionOutcome,
  IslandCoordinator,
  IslandCoordinatorListener,
  IslandCoordinatorOptions,
  IslandCoordinatorSnapshot,
  IslandDismissBehavior,
  IslandDispatchResult,
  IslandEvent,
  IslandMetadata,
  IslandMetadataValue,
  IslandPresentation,
  IslandPresentationInput,
  IslandScheduler,
  IslandStateKind,
} from "./types";

const SEMANTIC_PRIORITY: Readonly<Record<IslandStateKind, number>> = {
  Idle: 0,
  Media: 300,
  SystemEvent: 400,
  Success: 450,
  Thinking: 500,
  Executing: 600,
  Listening: 700,
  Error: 800,
  WaitingUser: 900,
};

const CRITICAL_ERROR_BONUS = 200;
const DEFAULT_AUTO_DURATION_MS: Readonly<Partial<Record<IslandStateKind, number>>> = {
  Success: 2_500,
  SystemEvent: 2_000,
};
const EXPLICIT_AUTO_FALLBACK_MS = 3_000;
const MAX_AUTO_DURATION_MS = 86_400_000;
const MAX_FUTURE_EVENT_SKEW_MS = 60_000;
const MAX_SOURCE_REPLACEMENT_ITEMS = 32;

const DEFAULT_SCHEDULER: IslandScheduler = {
  setTimeout(callback, delayMs) {
    return globalThis.setTimeout(callback, delayMs);
  },
  clearTimeout(handle) {
    globalThis.clearTimeout(handle as ReturnType<typeof globalThis.setTimeout>);
  },
};

const IDLE_PRESENTATION: IslandPresentation = Object.freeze({
  kind: "Idle",
  source: "coordinator",
  key: "idle",
  title: "元枢",
  subtitle: "Ready",
  priority: Object.freeze({ semantic: SEMANTIC_PRIORITY.Idle, source: 0 }),
  dismiss: Object.freeze({ mode: "persistent" }),
  requiresUserAction: false,
  visibility: Object.freeze({ wake: "none", allowIdleCollapse: true }),
  createdAt: 0,
  updatedAt: 0,
});

function isRecord(value: unknown): value is Record<string, unknown> {
  return typeof value === "object" && value !== null && !Array.isArray(value);
}

function isNonEmptyString(value: unknown): value is string {
  return typeof value === "string" && value.trim().length > 0;
}

function isFiniteNumber(value: unknown): value is number {
  return typeof value === "number" && Number.isFinite(value);
}

function isStateKind(value: unknown): value is IslandStateKind {
  return typeof value === "string" &&
    (ISLAND_STATE_KINDS as readonly string[]).includes(value);
}

function isDismissBehavior(value: unknown) {
  if (!isRecord(value)) {
    return false;
  }
  if (value.mode === "persistent") {
    return true;
  }
  return value.mode === "auto" &&
    (value.durationMs === undefined ||
      (isFiniteNumber(value.durationMs) &&
        value.durationMs > 0 &&
        value.durationMs <= MAX_AUTO_DURATION_MS));
}

function isAction(value: unknown) {
  return isRecord(value) &&
    isNonEmptyString(value.id) &&
    isNonEmptyString(value.label) &&
    (value.emphasis === undefined ||
      value.emphasis === "normal" ||
      value.emphasis === "primary" ||
      value.emphasis === "danger");
}

function isPlainObject(value: unknown): value is Record<string, unknown> {
  if (!isRecord(value)) {
    return false;
  }
  const prototype = Object.getPrototypeOf(value);
  return prototype === Object.prototype || prototype === null;
}

function isMetadataValue(value: unknown, ancestors = new Set<object>()): value is IslandMetadataValue {
  if (
    value === null ||
    typeof value === "string" ||
    typeof value === "boolean" ||
    isFiniteNumber(value)
  ) {
    return true;
  }
  if (typeof value !== "object" || value === null || ancestors.has(value)) {
    return false;
  }

  ancestors.add(value);
  const valid = Array.isArray(value)
    ? value.every((entry) => isMetadataValue(entry, ancestors))
    : isPlainObject(value) &&
      Object.values(value).every((entry) => isMetadataValue(entry, ancestors));
  ancestors.delete(value);
  return valid;
}

function cloneAndFreezeMetadataValue(value: IslandMetadataValue): IslandMetadataValue {
  if (Array.isArray(value)) {
    return Object.freeze(value.map(cloneAndFreezeMetadataValue));
  }
  if (typeof value === "object" && value !== null) {
    return Object.freeze(
      Object.fromEntries(
        Object.entries(value).map(([key, entry]) => [
          key,
          cloneAndFreezeMetadataValue(entry),
        ]),
      ),
    );
  }
  return value;
}

function isPresentationInput(value: unknown): value is IslandPresentationInput {
  if (!isRecord(value) ||
    !isStateKind(value.kind) ||
    !isNonEmptyString(value.source) ||
    !isNonEmptyString(value.key) ||
    !isNonEmptyString(value.title)) {
    return false;
  }
  if (value.subtitle !== undefined && typeof value.subtitle !== "string") {
    return false;
  }
  if (value.indicator !== undefined && typeof value.indicator !== "string") {
    return false;
  }
  if (value.sourcePriority !== undefined && !isFiniteNumber(value.sourcePriority)) {
    return false;
  }
  if (
    value.kind !== "WaitingUser" &&
    value.dismiss !== undefined &&
    !isDismissBehavior(value.dismiss)
  ) {
    return false;
  }
  if (value.actions !== undefined) {
    if (!Array.isArray(value.actions) || !value.actions.every(isAction)) {
      return false;
    }
    const actionIds = value.actions.map((action) => action.id);
    if (new Set(actionIds).size !== actionIds.length) {
      return false;
    }
  }
  if (
    value.metadata !== undefined &&
    (!isPlainObject(value.metadata) || !isMetadataValue(value.metadata))
  ) {
    return false;
  }
  if ("progress" in value) {
    if (value.kind !== "Executing" ||
      (value.progress !== undefined &&
        (!isFiniteNumber(value.progress) || value.progress < 0 || value.progress > 100))) {
      return false;
    }
  }
  if (value.kind === "Error" &&
    value.severity !== undefined &&
    value.severity !== "error" &&
    value.severity !== "critical") {
    return false;
  }
  if (value.kind === "SystemEvent" &&
    value.category !== undefined &&
    typeof value.category !== "string") {
    return false;
  }
  return true;
}

function isCompletionOutcome(value: unknown): value is IslandCompletionOutcome {
  if (!isRecord(value) || (value.kind !== "Success" && value.kind !== "Error")) {
    return false;
  }
  return isPresentationInput({ ...value, source: "validation", key: "validation" });
}

function identity(source: string, key: string) {
  return `${source}\u0000${key}`;
}

function clampSourcePriority(value: number | undefined) {
  return value === undefined ? 0 : Math.max(-99, Math.min(99, value));
}

function resolveDismiss(input: IslandPresentationInput): IslandDismissBehavior {
  if (input.kind === "WaitingUser") {
    return { mode: "persistent" };
  }
  if (input.dismiss?.mode === "persistent") {
    return { mode: "persistent" };
  }
  if (input.dismiss?.mode === "auto") {
    return {
      mode: "auto",
      durationMs:
        input.dismiss.durationMs ??
        DEFAULT_AUTO_DURATION_MS[input.kind] ??
        EXPLICIT_AUTO_FALLBACK_MS,
    };
  }
  const defaultDuration = DEFAULT_AUTO_DURATION_MS[input.kind];
  return defaultDuration === undefined
    ? { mode: "persistent" }
    : { mode: "auto", durationMs: defaultDuration };
}

function normalizePresentation(
  input: IslandPresentationInput,
  eventAt: number,
  receivedAt: number,
  previous?: IslandPresentation,
): IslandPresentation {
  const dismiss = resolveDismiss(input);
  const sourcePriority = clampSourcePriority(input.sourcePriority);
  const expiresAt = dismiss.mode === "auto"
    ? receivedAt + (dismiss.durationMs ?? EXPLICIT_AUTO_FALLBACK_MS)
    : undefined;
  const actions = input.actions === undefined
    ? undefined
    : Object.freeze(
        input.actions.map((action) =>
          Object.freeze({
            id: action.id,
            label: action.label,
            ...(action.emphasis === undefined ? {} : { emphasis: action.emphasis }),
          }),
        ),
      );
  const metadata = input.metadata === undefined
    ? undefined
    : cloneAndFreezeMetadataValue(input.metadata) as IslandMetadata;
  const kindFields = input.kind === "Executing"
    ? (input.progress === undefined ? {} : { progress: input.progress })
    : input.kind === "Error"
      ? (input.severity === undefined ? {} : { severity: input.severity })
      : input.kind === "SystemEvent"
        ? (input.category === undefined ? {} : { category: input.category })
        : {};

  return Object.freeze({
    kind: input.kind,
    source: input.source,
    key: input.key,
    title: input.title,
    ...(input.subtitle === undefined ? {} : { subtitle: input.subtitle }),
    ...(input.indicator === undefined ? {} : { indicator: input.indicator }),
    sourcePriority,
    ...(actions === undefined ? {} : { actions }),
    ...(metadata === undefined ? {} : { metadata }),
    ...kindFields,
    priority: Object.freeze({
      semantic:
        SEMANTIC_PRIORITY[input.kind] +
        (input.kind === "Error" && input.severity === "critical"
          ? CRITICAL_ERROR_BONUS
          : 0),
      source: sourcePriority,
    }),
    dismiss: Object.freeze(dismiss),
    requiresUserAction: input.kind === "WaitingUser",
    visibility: Object.freeze(
      input.kind === "Idle" || input.kind === "Media"
        ? { wake: "none", allowIdleCollapse: true }
        : { wake: "compact", allowIdleCollapse: false },
    ),
    createdAt: previous?.createdAt ?? eventAt,
    updatedAt: eventAt,
    ...(expiresAt === undefined ? {} : { expiresAt }),
  }) as IslandPresentation;
}

function comparePresentation(left: IslandPresentation, right: IslandPresentation) {
  if (left.priority.semantic !== right.priority.semantic) {
    return left.priority.semantic - right.priority.semantic;
  }
  if (left.priority.source !== right.priority.source) {
    return left.priority.source - right.priority.source;
  }
  if (left.updatedAt !== right.updatedAt) {
    return left.updatedAt - right.updatedAt;
  }
  const leftIdentity = identity(left.source, left.key);
  const rightIdentity = identity(right.source, right.key);
  if (leftIdentity === rightIdentity) {
    return 0;
  }
  return leftIdentity > rightIdentity ? 1 : -1;
}

function selectActive(presentations: Iterable<IslandPresentation>) {
  let active: IslandPresentation = IDLE_PRESENTATION;
  for (const presentation of presentations) {
    if (active === IDLE_PRESENTATION || comparePresentation(presentation, active) > 0) {
      active = presentation;
    }
  }
  return active;
}

export function createIslandCoordinator(
  options: IslandCoordinatorOptions = {},
): IslandCoordinator {
  const clock = options.clock ?? Date.now;
  const scheduler = options.scheduler ?? DEFAULT_SCHEDULER;
  const presentations = new Map<string, IslandPresentation>();
  const lastEventAt = new Map<string, number>();
  const sourceClearedAt = new Map<string, number>();
  const listeners = new Set<IslandCoordinatorListener>();
  let revision = 0;
  let destroyed = false;
  let expirationHandle: unknown;
  let expirationToken = 0;
  let snapshot: IslandCoordinatorSnapshot = Object.freeze({
    active: IDLE_PRESENTATION,
    retainedCount: 0,
    revision,
  });

  const notify = () => {
    for (const listener of listeners) {
      try {
        listener(snapshot);
      } catch {
        // One observer must not break lifecycle processing for every consumer.
      }
    }
  };

  const rebuildSnapshot = () => {
    snapshot = Object.freeze({
      active: selectActive(presentations.values()),
      retainedCount: presentations.size,
      revision,
    });
    notify();
  };

  const cancelExpiration = () => {
    expirationToken += 1;
    if (expirationHandle !== undefined) {
      scheduler.clearTimeout(expirationHandle);
      expirationHandle = undefined;
    }
  };

  const scheduleNextExpiration = () => {
    cancelExpiration();
    if (destroyed) {
      return;
    }

    let earliest: number | undefined;
    for (const presentation of presentations.values()) {
      if (presentation.expiresAt !== undefined &&
        (earliest === undefined || presentation.expiresAt < earliest)) {
        earliest = presentation.expiresAt;
      }
    }
    if (earliest === undefined) {
      return;
    }

    const token = expirationToken;
    expirationHandle = scheduler.setTimeout(() => {
      if (destroyed || token !== expirationToken) {
        return;
      }
      expirationHandle = undefined;
      const now = clock();
      let changed = false;
      for (const [eventIdentity, presentation] of presentations) {
        if (presentation.expiresAt !== undefined && presentation.expiresAt <= now) {
          presentations.delete(eventIdentity);
          lastEventAt.set(
            eventIdentity,
            Math.max(lastEventAt.get(eventIdentity) ?? Number.NEGATIVE_INFINITY, now),
          );
          changed = true;
        }
      }
      if (changed) {
        revision += 1;
        rebuildSnapshot();
      }
      scheduleNextExpiration();
    }, Math.max(0, earliest - clock()));
  };

  const commitPresentationChange = () => {
    revision += 1;
    rebuildSnapshot();
    scheduleNextExpiration();
  };

  const rejected = (reason: string): IslandDispatchResult => ({
    accepted: false,
    reason,
    snapshot,
  });

  const staleWatermark = (source: string, eventIdentity: string) => Math.max(
    lastEventAt.get(eventIdentity) ?? Number.NEGATIVE_INFINITY,
    sourceClearedAt.get(source) ?? Number.NEGATIVE_INFINITY,
  );

  return {
    dispatch(event: IslandEvent): IslandDispatchResult {
      if (destroyed) {
        return rejected("coordinator-destroyed");
      }
      if (!isRecord(event) || typeof event.type !== "string") {
        return rejected("invalid-event");
      }

      const receivedAt = clock();
      const providedEventAt = event.at === undefined ? receivedAt : event.at;
      if (!isFiniteNumber(providedEventAt)) {
        return rejected("invalid-timestamp");
      }
      if (providedEventAt > receivedAt + MAX_FUTURE_EVENT_SKEW_MS) {
        return rejected("future-timestamp");
      }
      const eventAt = Math.min(providedEventAt, receivedAt);

      if (event.type === "upsert") {
        if (!isPresentationInput(event.presentation)) {
          return rejected("invalid-presentation");
        }
        const { presentation } = event;
        const eventIdentity = identity(presentation.source, presentation.key);
        if (eventAt < staleWatermark(presentation.source, eventIdentity)) {
          return rejected("stale-event");
        }
        const previous = presentations.get(eventIdentity);
        presentations.set(
          eventIdentity,
          normalizePresentation(presentation, eventAt, receivedAt, previous),
        );
        lastEventAt.set(eventIdentity, eventAt);
        commitPresentationChange();
        return { accepted: true, snapshot };
      }

      if (event.type === "remove") {
        if (!isNonEmptyString(event.source) || !isNonEmptyString(event.key)) {
          return rejected("invalid-target");
        }
        const eventIdentity = identity(event.source, event.key);
        if (eventAt < staleWatermark(event.source, eventIdentity)) {
          return rejected("stale-event");
        }
        lastEventAt.set(eventIdentity, eventAt);
        if (presentations.delete(eventIdentity)) {
          commitPresentationChange();
        }
        return { accepted: true, snapshot };
      }

      if (event.type === "complete") {
        if (!isNonEmptyString(event.source) ||
          !isNonEmptyString(event.key) ||
          (event.outcome !== undefined && !isCompletionOutcome(event.outcome))) {
          return rejected("invalid-completion");
        }
        const eventIdentity = identity(event.source, event.key);
        if (eventAt < staleWatermark(event.source, eventIdentity)) {
          return rejected("stale-event");
        }
        const previous = presentations.get(eventIdentity);
        lastEventAt.set(eventIdentity, eventAt);
        if (event.outcome === undefined) {
          if (presentations.delete(eventIdentity)) {
            commitPresentationChange();
          }
          return { accepted: true, snapshot };
        }
        const completedPresentation = {
          ...event.outcome,
          source: event.source,
          key: event.key,
        } as IslandPresentationInput;
        presentations.set(
          eventIdentity,
          normalizePresentation(completedPresentation, eventAt, receivedAt, previous),
        );
        commitPresentationChange();
        return { accepted: true, snapshot };
      }

      if (event.type === "replace-source") {
        if (
          !isNonEmptyString(event.source) ||
          !Array.isArray(event.presentations) ||
          event.presentations.length > MAX_SOURCE_REPLACEMENT_ITEMS
        ) {
          return rejected("invalid-source-replacement");
        }
        const previousClear = sourceClearedAt.get(event.source);
        if (previousClear !== undefined && eventAt < previousClear) {
          return rejected("stale-event");
        }
        const keys = new Set<string>();
        for (const presentation of event.presentations) {
          if (
            !isPresentationInput(presentation) ||
            presentation.source !== event.source ||
            keys.has(presentation.key)
          ) {
            return rejected("invalid-source-replacement");
          }
          keys.add(presentation.key);
          const eventIdentity = identity(event.source, presentation.key);
          if (eventAt < staleWatermark(event.source, eventIdentity)) {
            return rejected("stale-event");
          }
        }
        for (const presentation of presentations.values()) {
          if (presentation.source === event.source && presentation.updatedAt > eventAt) {
            return rejected("stale-event");
          }
        }

        const replacements = event.presentations.map((presentation) => {
          const eventIdentity = identity(event.source, presentation.key);
          return [
            eventIdentity,
            normalizePresentation(
              presentation,
              eventAt,
              receivedAt,
              presentations.get(eventIdentity),
            ),
          ] as const;
        });
        sourceClearedAt.set(event.source, eventAt);
        for (const [eventIdentity, presentation] of presentations) {
          if (presentation.source === event.source) {
            presentations.delete(eventIdentity);
            lastEventAt.set(
              eventIdentity,
              Math.max(lastEventAt.get(eventIdentity) ?? Number.NEGATIVE_INFINITY, eventAt),
            );
          }
        }
        for (const [eventIdentity, presentation] of replacements) {
          presentations.set(eventIdentity, presentation);
          lastEventAt.set(eventIdentity, eventAt);
        }
        commitPresentationChange();
        return { accepted: true, snapshot };
      }

      if (event.type === "clear-source") {
        if (!isNonEmptyString(event.source)) {
          return rejected("invalid-source");
        }
        const previousClear = sourceClearedAt.get(event.source);
        if (previousClear !== undefined && eventAt < previousClear) {
          return rejected("stale-event");
        }
        sourceClearedAt.set(event.source, eventAt);
        let changed = false;
        for (const [eventIdentity, presentation] of presentations) {
          if (presentation.source === event.source && presentation.updatedAt <= eventAt) {
            presentations.delete(eventIdentity);
            lastEventAt.set(eventIdentity, eventAt);
            changed = true;
          }
        }
        if (changed) {
          commitPresentationChange();
        }
        return { accepted: true, snapshot };
      }

      return rejected("unknown-event-type");
    },
    getSnapshot() {
      return snapshot;
    },
    subscribe(listener) {
      if (destroyed) {
        return () => undefined;
      }
      listeners.add(listener);
      return () => listeners.delete(listener);
    },
    destroy() {
      if (destroyed) {
        return;
      }
      destroyed = true;
      cancelExpiration();
      listeners.clear();
    },
  };
}
