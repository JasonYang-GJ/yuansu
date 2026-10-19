import type { CSSProperties, RefObject } from "react";
import type { CoreBridgeConnectionStatus } from "../../bridge/coreBridge";
import type { IslandAction, IslandPresentation, IslandStateKind } from "../../coordinator";
import type { IslandMode } from "../../hooks/useIslandController";

interface IslandProps {
  mode: IslandMode;
  presentation: IslandPresentation;
  coreBridgeConnectionStatus?: CoreBridgeConnectionStatus;
  onExpand: () => void;
  onCollapse: () => void;
  onAction?: (action: IslandAction) => void;
  onOpenPanel?: () => void;
  compactButtonRef: RefObject<HTMLButtonElement | null>;
  collapseButtonRef: RefObject<HTMLButtonElement | null>;
}

interface PivotGlassProps {
  size: "small" | "medium" | "full";
}

export function PivotGlass({ size }: PivotGlassProps) {
  return (
    <span className={`pivot-glass pivot-glass--${size}`} aria-hidden="true">
      <span className="pivot-glass__lens" />
      <span className="pivot-glass__orbit" />
      <span className="pivot-glass__core" />
    </span>
  );
}

interface StateVisualCopy {
  label: string;
  detail: string;
}

const STATE_VISUAL_COPY: Readonly<Record<IslandStateKind, StateVisualCopy>> = {
  Idle: { label: "空闲", detail: "系统正常" },
  Listening: { label: "正在聆听", detail: "正在接收你的声音" },
  Thinking: { label: "正在思考", detail: "正在整理思路" },
  Executing: { label: "正在执行", detail: "任务进行中" },
  WaitingUser: { label: "等待决定", detail: "需要你的操作才能继续" },
  Success: { label: "已完成", detail: "操作已成功完成" },
  Error: { label: "出现问题", detail: "请查看详情并决定下一步" },
  SystemEvent: { label: "系统事件", detail: "系统状态已更新" },
  Media: { label: "媒体", detail: "媒体正在播放" },
};

function getProgress(presentation: IslandPresentation): number | undefined {
  if (presentation.kind === "Executing") {
    return presentation.progress === undefined
      ? undefined
      : Math.min(100, Math.max(0, presentation.progress));
  }

  const volume = presentation.kind === "SystemEvent" && presentation.category === "volume"
    ? presentation.metadata?.volume
    : undefined;
  return typeof volume === "number" && Number.isFinite(volume)
    ? Math.min(100, Math.max(0, volume))
    : undefined;
}

function isVolumePresentation(presentation: IslandPresentation) {
  return presentation.kind === "SystemEvent" &&
    presentation.category === "volume" &&
    getProgress(presentation) !== undefined;
}

function isMutedVolume(presentation: IslandPresentation) {
  return isVolumePresentation(presentation) && presentation.metadata?.muted === true;
}

function getCompactStatus(
  presentation: IslandPresentation,
  stateCopy: StateVisualCopy,
  progress: number | undefined,
) {
  if (progress !== undefined) {
    if (isMutedVolume(presentation)) {
      return "Muted";
    }
    return `${Math.round(progress)}%`;
  }

  if (presentation.kind === "Media" && presentation.indicator !== undefined) {
    return presentation.indicator;
  }

  return presentation.subtitle ?? stateCopy.label;
}

export function Island({
  mode,
  presentation,
  coreBridgeConnectionStatus = "disconnected",
  onExpand,
  onCollapse,
  onAction,
  onOpenPanel,
  compactButtonRef,
  collapseButtonRef,
}: IslandProps) {
  const expanded = mode === "expanded";
  const compact = mode === "compact";
  const idle = mode === "orb" || mode === "tucked";
  const stateCopy = STATE_VISUAL_COPY[presentation.kind];
  const progress = getProgress(presentation);
  const volumePresentation = isVolumePresentation(presentation);
  const mutedVolume = isMutedVolume(presentation);
  const compactStatus = getCompactStatus(presentation, stateCopy, progress);
  const actions = presentation.actions ?? [];
  const detail = presentation.indicator ?? stateCopy.detail;
  const bridgeConnected = coreBridgeConnectionStatus === "connected";

  return (
    <div
      className={`island-shell island-shell--${mode}`}
      data-state={mode}
      data-presentation={presentation.kind}
      data-indicator={presentation.indicator}
      aria-live="polite"
    >
      <button
        ref={compactButtonRef}
        className="island-compact glass-surface"
        type="button"
        aria-label="展开元枢灵动岛"
        aria-expanded={expanded}
        aria-controls="island-details"
        aria-hidden={!compact}
        tabIndex={compact ? 0 : -1}
        onClick={onExpand}
      >
        <span className="island-compact__brand">
          <PivotGlass size="small" />
          <span className="island-compact__name">{presentation.title}</span>
        </span>
        <span className="island-compact__status">
          <span className="island-compact__status-label">{compactStatus}</span>
          <span className="island-compact__chevron" aria-hidden="true" />
        </span>
      </button>

      <button
        className="island-idle"
        type="button"
        aria-label="展开元枢灵动岛"
        aria-expanded={false}
        aria-controls="island-details"
        aria-hidden={!idle}
        tabIndex={idle ? 0 : -1}
        title="打开元枢"
        onClick={onExpand}
      >
        <PivotGlass size="full" />
      </button>

      <section
        id="island-details"
        className="island-details glass-surface"
        aria-label="元枢灵动岛详情"
        aria-hidden={!expanded}
      >
        <header className="island-details__header">
          <div className="island-details__identity">
            <PivotGlass size="medium" />
            <div>
              <h1>{presentation.title}</h1>
              <p className="island-details__bridge-line">
                <span>桌面状态层</span>
                <span
                  className="core-bridge-status"
                  data-connection={coreBridgeConnectionStatus}
                  aria-label="Core Bridge 连接状态"
                >
                  <span className="core-bridge-status__dot" aria-hidden="true" />
                  Core {bridgeConnected ? "Connected" : "Disconnected"}
                </span>
              </p>
            </div>
          </div>
          <span className="island-details__state-tag">{stateCopy.label}</span>
        </header>

        <div className="island-details__body">
          <div className="status-reading">
            <span className="status-reading__label">当前状态</span>
            <strong>{presentation.subtitle ?? stateCopy.label}</strong>
          </div>
          {presentation.kind === "Executing" || volumePresentation ? (
            <div className="status-reading status-reading--secondary status-reading--progress">
              <span className="status-reading__label">
                {volumePresentation ? "系统音量" : "执行进度"}
              </span>
              <div className="island-progress__reading">
                <span
                  className={`island-progress ${
                    progress === undefined ? "island-progress--indeterminate" : ""
                  }`}
                  role="progressbar"
                  aria-label={volumePresentation ? "系统音量" : "执行进度"}
                  aria-valuemin={0}
                  aria-valuemax={100}
                  aria-valuenow={progress === undefined ? undefined : Math.round(progress)}
                >
                  <span
                    className="island-progress__fill"
                    style={
                      progress === undefined
                        ? undefined
                        : ({ "--progress": `${progress}%` } as CSSProperties)
                    }
                  />
                </span>
                <strong>
                  {mutedVolume
                    ? "Muted"
                    : progress === undefined
                      ? "进行中"
                      : `${Math.round(progress)}%`}
                </strong>
              </div>
            </div>
          ) : (
            <div className="status-reading status-reading--secondary">
              <span className="status-reading__label">状态提示</span>
              <strong>{detail}</strong>
            </div>
          )}
        </div>

        <footer className="island-details__actions">
          <div className="island-details__context-actions">
            {actions.length > 0 ? (
              actions.map((action) => (
                <button
                  key={action.id}
                  className={`island-action island-action--context island-action--${
                    action.emphasis ?? "normal"
                  }`}
                  type="button"
                  disabled={onAction === undefined}
                  tabIndex={expanded ? 0 : -1}
                  onClick={() => onAction?.(action)}
                >
                  {action.label}
                </button>
              ))
            ) : (
              <button
                className="island-action island-action--context"
                type="button"
                disabled={onOpenPanel === undefined}
                tabIndex={expanded ? 0 : -1}
                onClick={onOpenPanel}
              >
                <span>任务与设置</span>
              </button>
            )}
          </div>
          <button
            ref={collapseButtonRef}
            className="island-action island-action--primary"
            type="button"
            tabIndex={expanded ? 0 : -1}
            onClick={onCollapse}
          >
            收起
            <span className="island-action__chevron" aria-hidden="true" />
          </button>
        </footer>
      </section>
    </div>
  );
}
