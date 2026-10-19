import { act, renderHook } from "@testing-library/react";
import { describe, expect, it } from "vitest";
import { createIslandCoordinator, type IslandCoordinator } from "../coordinator";
import { useIslandCoordinator } from "./useIslandCoordinator";

describe("useIslandCoordinator", () => {
  it("从内部实例切到外部再切回时创建新的可用内部实例", async () => {
    type HookProps = { coordinator?: IslandCoordinator };
    const external = createIslandCoordinator();
    const { result, rerender, unmount } = renderHook(
      ({ coordinator }: HookProps) => useIslandCoordinator(coordinator),
      { initialProps: { coordinator: undefined } as HookProps },
    );
    const firstOwned = result.current;

    rerender({ coordinator: external });
    await act(async () => {
      await Promise.resolve();
    });
    expect(result.current).toBe(external);

    rerender({ coordinator: undefined });
    const nextOwned = result.current;
    expect(nextOwned).not.toBe(firstOwned);
    expect(
      nextOwned.dispatch({
        type: "upsert",
        presentation: {
          kind: "Thinking",
          source: "hook-test",
          key: "task",
          title: "思考中",
        },
      }),
    ).toMatchObject({ accepted: true });

    unmount();
    external.destroy();
  });
});
