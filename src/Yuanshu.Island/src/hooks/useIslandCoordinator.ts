import { useCallback, useEffect, useRef, useSyncExternalStore } from "react";
import {
  createIslandCoordinator,
  type IslandCoordinator,
  type IslandCoordinatorSnapshot,
} from "../coordinator";

export function useIslandCoordinator(provided?: IslandCoordinator) {
  const ownedCoordinatorRef = useRef<IslandCoordinator | null>(null);
  const lifecycleGenerationRef = useRef(0);

  if (provided === undefined && ownedCoordinatorRef.current === null) {
    ownedCoordinatorRef.current = createIslandCoordinator();
  }

  const coordinator = provided ?? ownedCoordinatorRef.current;
  if (coordinator === null) {
    throw new Error("Island Coordinator 初始化失败");
  }

  useEffect(() => {
    if (provided !== undefined) {
      return;
    }

    const generation = lifecycleGenerationRef.current + 1;
    lifecycleGenerationRef.current = generation;

    return () => {
      const ownedCoordinator = ownedCoordinatorRef.current;
      queueMicrotask(() => {
        if (lifecycleGenerationRef.current === generation) {
          ownedCoordinator?.destroy();
          if (ownedCoordinatorRef.current === ownedCoordinator) {
            ownedCoordinatorRef.current = null;
          }
        }
      });
    };
  }, [provided]);

  return coordinator;
}

export function useIslandCoordinatorSnapshot(
  coordinator: IslandCoordinator,
): IslandCoordinatorSnapshot {
  const subscribe = useCallback(
    (listener: () => void) => coordinator.subscribe(listener),
    [coordinator],
  );
  const getSnapshot = useCallback(() => coordinator.getSnapshot(), [coordinator]);

  return useSyncExternalStore(subscribe, getSnapshot, getSnapshot);
}
