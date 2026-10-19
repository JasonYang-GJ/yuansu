export const ISLAND_STATE_KINDS = [
  "Idle",
  "Listening",
  "Thinking",
  "Executing",
  "WaitingUser",
  "Success",
  "Error",
  "SystemEvent",
  "Media",
] as const;

export type IslandStateKind = (typeof ISLAND_STATE_KINDS)[number];

export type IslandDismissBehavior =
  | { readonly mode: "persistent" }
  | { readonly mode: "auto"; readonly durationMs?: number };

export interface IslandAction {
  readonly id: string;
  readonly label: string;
  readonly emphasis?: "normal" | "primary" | "danger";
}

export type IslandMetadataValue =
  | string
  | number
  | boolean
  | null
  | readonly IslandMetadataValue[]
  | { readonly [key: string]: IslandMetadataValue };

export type IslandMetadata = Readonly<Record<string, IslandMetadataValue>>;

interface IslandPresentationBase<TKind extends IslandStateKind> {
  readonly kind: TKind;
  readonly source: string;
  readonly key: string;
  readonly title: string;
  readonly subtitle?: string;
  /** Short user-facing status text, not an icon or technical identifier. */
  readonly indicator?: string;
  readonly sourcePriority?: number;
  readonly dismiss?: IslandDismissBehavior;
  readonly actions?: readonly IslandAction[];
  readonly metadata?: IslandMetadata;
}

export type IdlePresentationInput = IslandPresentationBase<"Idle">;
export type ListeningPresentationInput = IslandPresentationBase<"Listening">;
export type ThinkingPresentationInput = IslandPresentationBase<"Thinking">;
export type ExecutingPresentationInput = IslandPresentationBase<"Executing"> & {
  readonly progress?: number;
};
export type WaitingUserPresentationInput = IslandPresentationBase<"WaitingUser">;
export type SuccessPresentationInput = IslandPresentationBase<"Success">;
export type ErrorPresentationInput = IslandPresentationBase<"Error"> & {
  readonly severity?: "error" | "critical";
};
export type SystemEventPresentationInput = IslandPresentationBase<"SystemEvent"> & {
  readonly category?: string;
};
export type MediaPresentationInput = IslandPresentationBase<"Media">;

export type IslandPresentationInput =
  | IdlePresentationInput
  | ListeningPresentationInput
  | ThinkingPresentationInput
  | ExecutingPresentationInput
  | WaitingUserPresentationInput
  | SuccessPresentationInput
  | ErrorPresentationInput
  | SystemEventPresentationInput
  | MediaPresentationInput;

export interface ResolvedIslandPriority {
  /** Coordinator-owned semantic rank. UI and providers must not compare it. */
  readonly semantic: number;
  /** Source-local tie breaker, clamped by the Coordinator. */
  readonly source: number;
}

export interface IslandVisibilityPolicy {
  readonly wake: "none" | "compact";
  readonly allowIdleCollapse: boolean;
}

export type IslandPresentation = IslandPresentationInput & {
  readonly priority: Readonly<ResolvedIslandPriority>;
  readonly dismiss: IslandDismissBehavior;
  readonly requiresUserAction: boolean;
  readonly visibility: Readonly<IslandVisibilityPolicy>;
  readonly createdAt: number;
  readonly updatedAt: number;
  readonly expiresAt?: number;
};

export type IslandCompletionOutcome =
  | Omit<SuccessPresentationInput, "source" | "key">
  | Omit<ErrorPresentationInput, "source" | "key">;

export type IslandEvent =
  | {
      type: "upsert";
      presentation: IslandPresentationInput;
      at?: number;
    }
  | {
      type: "remove";
      source: string;
      key: string;
      at?: number;
    }
  | {
      type: "complete";
      source: string;
      key: string;
      outcome?: IslandCompletionOutcome;
      at?: number;
    }
  | {
      type: "clear-source";
      source: string;
      at?: number;
    }
  | {
      type: "replace-source";
      source: string;
      presentations: readonly IslandPresentationInput[];
      at?: number;
    };

export interface IslandCoordinatorSnapshot {
  readonly active: IslandPresentation;
  readonly retainedCount: number;
  readonly revision: number;
}

export type IslandDispatchResult =
  | {
      accepted: true;
      snapshot: IslandCoordinatorSnapshot;
    }
  | {
      accepted: false;
      reason: string;
      snapshot: IslandCoordinatorSnapshot;
    };

export interface IslandScheduler {
  setTimeout(callback: () => void, delayMs: number): unknown;
  clearTimeout(handle: unknown): void;
}

export interface IslandCoordinatorOptions {
  clock?: () => number;
  scheduler?: IslandScheduler;
}

export type IslandCoordinatorListener = (snapshot: IslandCoordinatorSnapshot) => void;

export interface IslandCoordinator {
  dispatch(event: IslandEvent): IslandDispatchResult;
  getSnapshot(): IslandCoordinatorSnapshot;
  subscribe(listener: IslandCoordinatorListener): () => void;
  destroy(): void;
}
