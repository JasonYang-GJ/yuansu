import { useEffect, useState } from "react";
import { invoke } from "@tauri-apps/api/core";
import { getCurrentWindow } from "@tauri-apps/api/window";
import type { IslandCoordinator } from "../coordinator";
import {
  CORE_BRIDGE_EVENT,
  CORE_BRIDGE_STATE_COMMAND,
  coreBridgePayloadToIslandEvent,
  coreBridgeStateToConnectionStatus,
  decodeCoreBridgeState,
  type CoreBridgeConnectionStatus,
} from "./coreBridge";

export function useCoreBridgeProvider(
  coordinator: IslandCoordinator,
  enabled = true,
): CoreBridgeConnectionStatus {
  const [connectionStatus, setConnectionStatus] = useState<CoreBridgeConnectionStatus>(
    "disconnected",
  );

  useEffect(() => {
    if (!enabled) {
      setConnectionStatus("disconnected");
      coordinator.dispatch({ type: "clear-source", source: "yuanshu.core" });
      return;
    }
    let disposed = false;
    let revision = 0;
    let stopListening: (() => void) | undefined;
    let lastIdentity: string | undefined;

    const apply = (payload: unknown) => {
      if (disposed) {
        return;
      }
      const decoded = decodeCoreBridgeState(payload);
      setConnectionStatus(coreBridgeStateToConnectionStatus(decoded));
      const identity = decoded?.kind === "snapshot"
        ? `${decoded.snapshot.server_instance_id}:${decoded.snapshot.snapshot_id}`
        : "clear";
      if (identity === lastIdentity) {
        return;
      }
      lastIdentity = identity;
      coordinator.dispatch(coreBridgePayloadToIslandEvent(payload));
    };

    void getCurrentWindow()
      .listen<unknown>(CORE_BRIDGE_EVENT, ({ payload }) => { revision += 1; apply(payload); })
      .then((stop) => {
        if (disposed) {
          stop();
          return undefined;
        }
        stopListening = stop;
        const requested = revision;
        return invoke<unknown>(CORE_BRIDGE_STATE_COMMAND).then((value) => revision === requested ? value : undefined);
      })
      .then((payload) => {
        if (payload !== undefined) {
          apply(payload);
        }
      })
      .catch(() => apply({ kind: "clear" }));

    return () => {
      disposed = true;
      stopListening?.();
    };
  }, [coordinator, enabled]);

  return connectionStatus;
}
