import { useEffect, useRef } from "react";
import { invoke } from "@tauri-apps/api/core";
import { useSettings } from "./settings/useSettings";
import { Island } from "./components/Island/Island";
import { useIslandController } from "./hooks/useIslandController";
import {
  useIslandCoordinator,
  useIslandCoordinatorSnapshot,
} from "./hooks/useIslandCoordinator";
import type { IslandAction, IslandCoordinator, IslandPresentation } from "./coordinator";
import { useCoreBridgeProvider } from "./bridge/useCoreBridgeProvider";
import { useWindowsMediaProvider } from "./providers/windowsMedia";
import { useWindowsVolumeProvider } from "./providers/windowsVolume";
import { presenceShellPolicy } from "./presence/presencePolicy";
import { usePresenceProjection } from "./presence/usePresenceProjection";
import { useAssistantBridge } from "./assistant/useAssistantBridge";
import { AssistantIsland } from "./assistant/AssistantIsland";
import { useAssistantPresented, useAssistantSurface } from "./assistant/useAssistantSurface";
import { useAssistantInteraction } from "./assistant/useAssistantInteraction";
import { useAssistantPlacement } from "./assistant/useAssistantPlacement";

export interface AppProps {
  coordinator?: IslandCoordinator;
  onPresentationAction?: (
    action: IslandAction,
    presentation: IslandPresentation,
  ) => void;
}

function App({ coordinator: providedCoordinator, onPresentationAction }: AppProps) {
  const assistantPlacement = useAssistantPlacement();
  const assistant = useAssistantBridge(assistantPlacement.dragging);
  const assistantInteraction = useAssistantInteraction(assistant.integrated);
  const coordinator = useIslandCoordinator(providedCoordinator);
  const { preferences } = useSettings();
  useWindowsMediaProvider(coordinator, preferences.mediaEnabled);
  useWindowsVolumeProvider(coordinator, preferences.volumeEnabled);
  const coreBridgeConnectionStatus = useCoreBridgeProvider(coordinator, preferences.coreEnabled);
  const { active } = useIslandCoordinatorSnapshot(coordinator);
  const presence = usePresenceProjection(active);
  const baseShellPolicy = presenceShellPolicy(presence);
  const shellPolicy = assistant.integrated ? {
    wake: "compact" as const, allowIdleCollapse: false, preferredMode: "compact" as const, preserveExpandedOnBlur: true,
  } : preferences.idleCollapse ? baseShellPolicy : {
    ...baseShellPolicy, allowIdleCollapse: false, preferredMode: "compact" as const, wake: "compact" as const,
  };
  const wakeToken = `${active.source}\u0000${active.key}\u0000${active.kind}`;
  const {
    mode,
    modeSynchronized,
    expand,
    collapse,
    markActivity,
    handlePointerEnter,
    handlePointerLeave,
    syncError,
  } = useIslandController({ ...shellPolicy, wakeToken });
  const compactButtonRef = useRef<HTMLButtonElement>(null);
  const collapseButtonRef = useRef<HTMLButtonElement>(null);
  const shouldRestoreCompactFocus = useRef(false);
  const expanded = mode === "expanded";
  const assistantSurface = useAssistantSurface(assistant.integrated, assistant.snapshot, mode, expand, collapse, assistantInteraction.interactive, assistantPlacement.dragging);
  useAssistantPresented(assistant.snapshot, assistant.poll, assistantSurface.presentationReady && modeSynchronized, assistant.presented);

  useEffect(() => {
    if (!assistant.integrated && presence === "hidden-fullscreen" && expanded) {
      collapse();
    }
  }, [assistant.integrated, collapse, expanded, presence]);

  useEffect(() => {
    if (!document.hasFocus() || (assistant.integrated && !assistantInteraction.interactive)) {
      return;
    }

    if (expanded) {
      collapseButtonRef.current?.focus({ preventScroll: true });
      return;
    }

    if (mode === "compact" && shouldRestoreCompactFocus.current) {
      shouldRestoreCompactFocus.current = false;
      compactButtonRef.current?.focus({ preventScroll: true });
    }
  }, [expanded, mode, assistant.integrated, assistantInteraction.interactive]);

  useEffect(() => {
    const handleKeyDown = (event: KeyboardEvent) => {
      if (event.key === "Escape" && assistant.integrated) {
        event.preventDefault();
        void assistantInteraction.release();
        return;
      }
      if (event.key === "Escape" && expanded) {
        event.preventDefault();
        shouldRestoreCompactFocus.current = true;
        collapse();
        return;
      }

      markActivity();
    };

    window.addEventListener("keydown", handleKeyDown);
    return () => window.removeEventListener("keydown", handleKeyDown);
  }, [collapse, expanded, markActivity, assistant.integrated, assistantInteraction.release]);

  const handleCollapse = () => {
    shouldRestoreCompactFocus.current = true;
    collapse();
  };

  const handlePresentationAction = onPresentationAction
    ? (action: IslandAction) => {
        const focusedAction = document.activeElement;
        onPresentationAction(action, active);
        queueMicrotask(() => {
          if (
            expanded &&
            focusedAction instanceof HTMLElement &&
            !focusedAction.isConnected
          ) {
            collapseButtonRef.current?.focus({ preventScroll: true });
          }
        });
      }
    : undefined;

  return (
    <main
      className={`app-frame app-frame--${mode}`}
      data-mode={mode}
      data-presence={assistant.integrated ? "assistant" : presence}
      data-assistant-interactive={assistant.integrated ? assistantInteraction.interactive : undefined}
      aria-label="元枢灵动岛"
      onMouseEnter={handlePointerEnter}
      onMouseLeave={(event) => handlePointerLeave(event)}
      onMouseMove={markActivity}
    >
      {assistant.integrated ? <AssistantIsland
        mode={mode} snapshot={assistant.snapshot} busy={assistant.commandBusy} error={assistant.commandError ?? assistantInteraction.error ?? assistantPlacement.error}
        placement={assistantPlacement.placement} dragging={assistantPlacement.dragging} onBeginDrag={assistantPlacement.beginDrag} onOpenControls={assistantPlacement.openControls}
        interactive={assistantInteraction.interactive} shortcutAvailable={assistantInteraction.shortcutAvailable}
        desktopPresentation={active} onOpenPanel={() => { void invoke("open_island_panel"); }}
        onCommand={assistant.command} onCollapse={() => { void assistantInteraction.release(); }}
        collapseButtonRef={collapseButtonRef}
      /> : <Island
        mode={mode}
        presentation={active}
        coreBridgeConnectionStatus={coreBridgeConnectionStatus}
        onExpand={expand}
        onCollapse={handleCollapse}
        onAction={handlePresentationAction}
        onOpenPanel={() => { void invoke("open_island_panel"); }}
        compactButtonRef={compactButtonRef}
        collapseButtonRef={collapseButtonRef}
      />}
      <p className="visually-hidden" role="status" aria-live="polite">
        {syncError ?? ""}
      </p>
    </main>
  );
}

export default App;
