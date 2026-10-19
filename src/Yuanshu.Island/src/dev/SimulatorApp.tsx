import { useCallback, useEffect, useRef, useState } from "react";
import { Island } from "../components/Island/Island";
import type {
  IslandAction,
  IslandCoordinator,
  IslandPresentationInput,
  IslandStateKind,
} from "../coordinator";
import {
  useIslandCoordinator,
  useIslandCoordinatorSnapshot,
} from "../hooks/useIslandCoordinator";
import { useWindowsMediaProvider } from "../providers/windowsMedia";
import { useWindowsVolumeProvider } from "../providers/windowsVolume";
import "./simulator.css";

const SIMULATOR_SOURCES = [
  "stage1-simulator",
  "stage1-media-scenario",
  "stage1-system-scenario",
  "stage1-task-scenario",
  "stage1-decision-scenario",
] as const;

interface SimulatorStateDefinition {
  label: string;
  presentation?: IslandPresentationInput;
}

const STATE_DEFINITIONS: readonly SimulatorStateDefinition[] = [
  { label: "Idle" },
  {
    label: "Listening",
    presentation: {
      kind: "Listening",
      source: "stage1-simulator",
      key: "listening",
      title: "元枢",
      subtitle: "正在聆听",
      indicator: "等待语音输入",
    },
  },
  {
    label: "Thinking",
    presentation: {
      kind: "Thinking",
      source: "stage1-simulator",
      key: "thinking",
      title: "元枢",
      subtitle: "正在思考",
      indicator: "正在整理上下文",
    },
  },
  {
    label: "Executing 25%",
    presentation: {
      kind: "Executing",
      source: "stage1-simulator",
      key: "executing",
      title: "正在执行任务",
      subtitle: "准备工作区",
      progress: 25,
    },
  },
  {
    label: "Executing 75%",
    presentation: {
      kind: "Executing",
      source: "stage1-simulator",
      key: "executing",
      title: "正在执行任务",
      subtitle: "正在验证结果",
      progress: 75,
    },
  },
  {
    label: "WaitingUser",
    presentation: {
      kind: "WaitingUser",
      source: "stage1-simulator",
      key: "waiting-user",
      title: "需要你的决定",
      subtitle: "是否继续执行测试？",
      actions: [
        { id: "continue", label: "继续", emphasis: "primary" },
        { id: "cancel", label: "取消" },
      ],
    },
  },
  {
    label: "Success",
    presentation: {
      kind: "Success",
      source: "stage1-simulator",
      key: "success",
      title: "操作完成",
      subtitle: "阶段 1 模拟成功",
    },
  },
  {
    label: "Error",
    presentation: {
      kind: "Error",
      source: "stage1-simulator",
      key: "error",
      title: "操作失败",
      subtitle: "这是开发模拟错误",
      severity: "error",
    },
  },
  {
    label: "SystemEvent",
    presentation: {
      kind: "SystemEvent",
      source: "stage1-simulator",
      key: "system-event",
      title: "系统音量",
      subtitle: "70%",
      indicator: "模拟事件 · 2 秒",
    },
  },
  {
    label: "Media",
    presentation: {
      kind: "Media",
      source: "stage1-simulator",
      key: "media",
      title: "正在播放",
      subtitle: "Yuanshu Test Track",
      indicator: "模拟媒体状态",
    },
  },
] as const;

function clearSimulator(coordinator: IslandCoordinator) {
  for (const source of SIMULATOR_SOURCES) {
    coordinator.dispatch({ type: "clear-source", source });
  }
}

function describeActive(kind: IslandStateKind, title: string) {
  return `${kind} · ${title}`;
}

export function SimulatorApp() {
  const coordinator = useIslandCoordinator();
  useWindowsMediaProvider(coordinator);
  useWindowsVolumeProvider(coordinator);
  const snapshot = useIslandCoordinatorSnapshot(coordinator);
  const [history, setHistory] = useState<string[]>([
    describeActive(snapshot.active.kind, snapshot.active.title),
  ]);
  const compactButtonRef = useRef<HTMLButtonElement>(null);
  const collapseButtonRef = useRef<HTMLButtonElement>(null);

  useEffect(
    () =>
      coordinator.subscribe((nextSnapshot) => {
        setHistory((current) => [
          describeActive(nextSnapshot.active.kind, nextSnapshot.active.title),
          ...current,
        ].slice(0, 8));
      }),
    [coordinator],
  );

  const showState = useCallback(
    (presentation?: IslandPresentationInput) => {
      const current = coordinator.getSnapshot().active;
      const updatesCurrent =
        presentation !== undefined &&
        presentation.source === current.source &&
        presentation.key === current.key;

      if (!updatesCurrent) {
        clearSimulator(coordinator);
      }
      if (presentation !== undefined) {
        coordinator.dispatch({ type: "upsert", presentation });
      }
    },
    [coordinator],
  );

  const runMediaScenario = useCallback(() => {
    clearSimulator(coordinator);
    coordinator.dispatch({
      type: "upsert",
      presentation: {
        kind: "Media",
        source: "stage1-media-scenario",
        key: "playing",
        title: "正在播放",
        subtitle: "低优先级媒体保持中",
        indicator: "等待临时事件结束",
      },
    });
    coordinator.dispatch({
      type: "upsert",
      presentation: {
        kind: "SystemEvent",
        source: "stage1-system-scenario",
        key: "volume",
        title: "音量 70%",
        subtitle: "2 秒后恢复媒体",
        indicator: "临时覆盖",
        dismiss: { mode: "auto", durationMs: 2_000 },
      },
    });
  }, [coordinator]);

  const runWaitingScenario = useCallback(() => {
    clearSimulator(coordinator);
    coordinator.dispatch({
      type: "upsert",
      presentation: {
        kind: "Executing",
        source: "stage1-task-scenario",
        key: "export",
        title: "正在执行导出",
        subtitle: "导出任务继续进行",
        progress: 48,
      },
    });
    coordinator.dispatch({
      type: "upsert",
      presentation: {
        kind: "WaitingUser",
        source: "stage1-decision-scenario",
        key: "permission",
        title: "需要你的决定",
        subtitle: "允许继续模拟导出吗？",
        actions: [{ id: "continue", label: "继续", emphasis: "primary" }],
      },
    });
  }, [coordinator]);

  const resolveWaiting = useCallback(() => {
    coordinator.dispatch({
      type: "remove",
      source: "stage1-decision-scenario",
      key: "permission",
    });
  }, [coordinator]);

  const handleAction = useCallback(
    (_action: IslandAction) => {
      if (snapshot.active.kind === "WaitingUser") {
        coordinator.dispatch({
          type: "remove",
          source: snapshot.active.source,
          key: snapshot.active.key,
        });
      }
    },
    [coordinator, snapshot.active],
  );

  return (
    <main className="simulator" aria-label="Island State Simulator">
      <aside className="simulator__controls">
        <div className="simulator__eyebrow">DEVELOPMENT ONLY</div>
        <h1>Island State Simulator</h1>
        <p>状态按钮使用本地测试数据，Windows Media / Volume Provider 保持真实接入。</p>

        <section aria-labelledby="state-controls-title">
          <h2 id="state-controls-title">单状态</h2>
          <div className="simulator__button-grid">
            {STATE_DEFINITIONS.map((definition) => (
              <button
                key={definition.label}
                type="button"
                onClick={() => showState(definition.presentation)}
              >
                {definition.label}
              </button>
            ))}
          </div>
        </section>

        <section aria-labelledby="scenario-controls-title">
          <h2 id="scenario-controls-title">抢占与恢复</h2>
          <div className="simulator__scenario-actions">
            <button type="button" onClick={runMediaScenario}>
              Media → SystemEvent → Media
            </button>
            <button type="button" onClick={runWaitingScenario}>
              Executing → WaitingUser
            </button>
            <button type="button" onClick={resolveWaiting}>
              处理 WaitingUser
            </button>
          </div>
        </section>

        <button
          className="simulator__reset"
          type="button"
          onClick={() => clearSimulator(coordinator)}
        >
          Reset to Idle
        </button>
      </aside>

      <section className="simulator__stage" aria-label="实时预览">
        <div className="simulator__statusbar">
          <span>ACTIVE PRESENTATION</span>
          <strong
            data-testid="simulator-active"
            aria-live="polite"
            aria-atomic="true"
          >
            {snapshot.active.kind} · {snapshot.active.source}/{snapshot.active.key}
          </strong>
          <small>retained {snapshot.retainedCount} · revision {snapshot.revision}</small>
        </div>
        <div className="simulator__preview">
          <Island
            mode="expanded"
            presentation={snapshot.active}
            onExpand={() => undefined}
            onCollapse={() => undefined}
            onAction={handleAction}
            compactButtonRef={compactButtonRef}
            collapseButtonRef={collapseButtonRef}
          />
        </div>
        <ol className="simulator__history" aria-label="展示状态历史">
          {history.map((entry, index) => (
            <li key={`${entry}-${index}`}>{entry}</li>
          ))}
        </ol>
      </section>
    </main>
  );
}
