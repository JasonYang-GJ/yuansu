import { useEffect, useState } from "react";
import { invoke } from "@tauri-apps/api/core";
import { getCurrentWindow } from "@tauri-apps/api/window";
import { CORE_BRIDGE_EVENT, CORE_BRIDGE_STATE_COMMAND, decodeCoreBridgeState, type CoreBridgeState, type CoreBridgePresentation } from "../bridge/coreBridge";
import { useSettings, type Preferences } from "../settings/useSettings";
import "./panel.css";

type Tab = "tasks" | "settings" | "diagnostics";
interface ControlStatus { canCancel: boolean; reason: string }

function useBridge() {
  const [state, setState] = useState<CoreBridgeState>({ kind: "clear" });
  const [control, setControl] = useState<ControlStatus>({ canCancel: false, reason: "正在读取连接能力…" });
  useEffect(() => {
    let disposed = false;
    let stop: (() => void) | undefined;
    let revision = 0;
    const refreshControl = () => {
      void invoke<ControlStatus>("get_island_control_status").then((value) => {
        if (!disposed) setControl(value);
      }).catch(() => { if (!disposed) setControl({ canCancel: false, reason: "暂时无法读取任务操作能力。" }); });
    };
    void getCurrentWindow().listen<unknown>(CORE_BRIDGE_EVENT, ({ payload }) => {
      revision += 1;
      if (!disposed) { setState(decodeCoreBridgeState(payload) ?? { kind: "clear" }); refreshControl(); }
    }).then((unlisten) => {
      if (disposed) { unlisten(); return; }
      stop = unlisten;
      const requested = revision;
      return invoke<unknown>(CORE_BRIDGE_STATE_COMMAND).then((value) => {
        if (!disposed && requested === revision) setState(decodeCoreBridgeState(value) ?? { kind: "clear" });
        refreshControl();
      });
    }).catch(() => { if (!disposed) setState({ kind: "clear" }); });
    return () => { disposed = true; stop?.(); };
  }, []);
  return { state, control };
}

function Toggle({ label, description, checked, disabled, onChange }: {
  label: string; description: string; checked: boolean; disabled: boolean; onChange: (value: boolean) => void;
}) {
  return <label className="setting-row"><span><strong>{label}</strong><span className="setting-description">{description}</span></span>
    <input type="checkbox" role="switch" checked={checked} disabled={disabled} onChange={(event) => onChange(event.target.checked)} />
  </label>;
}

function taskPhase(task: CoreBridgePresentation): string {
  if (task.phase === "CancellationRequested") return "正在取消";
  if (task.requires_user_action) return "等待你处理";
  if (task.island_kind === "Thinking") return "正在处理";
  if (task.is_terminal) return "已结束";
  return "执行中";
}

export function Panel() {
  const [tab, setTab] = useState<Tab>("tasks");
  const settings = useSettings();
  const { state, control } = useBridge();
  const [busy, setBusy] = useState(false);
  const [message, setMessage] = useState("");
  const [error, setError] = useState("");
  const [selected, setSelected] = useState<string | null>(null);
  const [confirm, setConfirm] = useState<CoreBridgePresentation | null>(null);
  const [diagnostics, setDiagnostics] = useState("");
  const connected = settings.preferences.coreEnabled && state.kind === "snapshot";
  const tasks = connected && state.kind === "snapshot" ? state.snapshot.presentations : [];
  const detail = tasks.find((task) => task.presentation_key === selected);

  async function action(work: () => Promise<unknown>, success: string) {
    if (busy) return;
    setBusy(true); setError(""); setMessage("");
    try { await work(); setMessage(success); }
    catch (failure) { setError(typeof failure === "string" ? failure : "操作未完成，请稍后重试。"); }
    finally { setBusy(false); }
  }
  function update(key: keyof Omit<Preferences, "schemaVersion">, value: boolean) {
    void action(() => invoke("save_island_settings", { preferences: { ...settings.preferences, [key]: value } }), "设置已保存");
  }
  async function cancel(task: CoreBridgePresentation) {
    if (state.kind !== "snapshot") return;
    const expectedServerInstanceId = state.snapshot.server_instance_id;
    setConfirm(null);
    await action(() => invoke("cancel_island_task", {
      expectedServerInstanceId, presentationKey: task.presentation_key, expectedVersion: task.entity_version,
    }), "取消请求已被核心接收，正在等待任务状态更新。");
  }

  return <main className="panel-root">
    <header className="panel-header"><div><h1>元枢灵动岛</h1><p>任务在这里，桌面少一点打扰。</p></div>
      <span className={`connection-label ${connected ? "connection-label--online" : ""}`}>
        <span aria-hidden="true" />{connected ? "元枢已连接" : settings.preferences.coreEnabled ? "元枢未连接" : "元枢来源已关闭"}
      </span>
    </header>
    <nav className="panel-tabs" aria-label="面板内容">
      {([ ["tasks", "任务"], ["settings", "设置"], ["diagnostics", "连接与隐私"] ] as const).map(([value, label]) =>
        <button key={value} type="button" aria-current={tab === value ? "page" : undefined} onClick={() => setTab(value)}>{label}</button>)}
    </nav>
    <div className="panel-content">
      {settings.warning && <p className="panel-alert" role="alert">{settings.warning}</p>}
      {error && <p className="panel-alert" role="alert">{error}</p>}
      <p className="panel-notice" role="status">{message}</p>
      {tab === "tasks" && <section aria-labelledby="tasks-title">
        <div className="section-heading"><h2 id="tasks-title">当前任务</h2><span>{tasks.length > 0 ? `${tasks.length} 项` : ""}</span></div>
        {!connected ? <div className="panel-empty"><h3>{settings.preferences.coreEnabled ? "等待元枢连接" : "任务来源已关闭"}</h3>
          <p>{settings.preferences.coreEnabled ? "元枢任务连接恢复后，正在处理的任务会自动显示在这里。媒体和音量提示不受任务连接影响。" : "在设置中开启元枢任务状态，即可查看正在处理的任务。"}</p>
          <button type="button" onClick={() => setTab(settings.preferences.coreEnabled ? "diagnostics" : "settings")}>{settings.preferences.coreEnabled ? "查看连接说明" : "前往设置"}</button></div>
        : tasks.length === 0 ? <div className="panel-empty"><h3>当前没有进行中的任务</h3><p>在元枢中发起请求后，这里会显示它的处理阶段。</p></div>
        : <ul className="task-list">{tasks.map((task) => <li key={task.presentation_key}>
          <div className="task-row"><div><h3>{task.title}</h3><p>{task.subtitle}</p></div><span className="task-phase">{taskPhase(task)}</span></div>
          <div className="task-actions"><button type="button" onClick={() => setSelected(selected === task.presentation_key ? null : task.presentation_key)} aria-expanded={selected === task.presentation_key}>任务详情</button>
            {!task.is_terminal && <button type="button" disabled={busy || !control.canCancel || task.phase === "CancellationRequested"} onClick={() => setConfirm(task)}>取消任务</button>}</div>
          {detail === task && <dl className="task-detail"><div><dt>类型</dt><dd>{task.entity_kind === "task" ? "编程任务" : "AI 请求"}</dd></div>
            <div><dt>处理阶段</dt><dd>{taskPhase(task)}</dd></div><div><dt>最近更新</dt><dd>{new Date(task.updated_at_utc).toLocaleTimeString("zh-CN")}</dd></div>
            <div><dt>进度</dt><dd>核心尚未提供百分比，以当前处理阶段为准。</dd></div></dl>}
        </li>)}</ul>}
        {connected && !control.canCancel && <p className="panel-help">{control.reason}</p>}
        {confirm && <div className="cancel-confirm" role="alertdialog" aria-labelledby="cancel-title" aria-describedby="cancel-description">
          <h3 id="cancel-title">取消“{confirm.title}”？</h3><p id="cancel-description">停止这项任务后，已经完成的操作不会自动撤销。</p>
          <div className="task-actions"><button type="button" autoFocus onClick={() => setConfirm(null)}>继续任务</button>
            <button className="danger-button" type="button" disabled={busy || !connected || !control.canCancel} onClick={() => { void cancel(confirm); }}>确认取消</button></div>
        </div>}
      </section>}
      {tab === "settings" && <section aria-labelledby="settings-title">
        <h2 id="settings-title">桌面显示</h2>
        <p>灵动岛已内置到元枢，随元枢一起启动和退出。登录启动请在元枢或 Windows 启动应用设置中管理。</p>
        <p className="panel-help">当前助手模式保留语音状态显示，拖到屏幕边缘可收成小球。下面两项旧版偏好已保留，但不应用于助手模式。</p>
        <Toggle label="空闲时自动藏边（旧版偏好）" description="助手模式使用手动拖边收球，避免自动移动位置。" checked={settings.preferences.idleCollapse} disabled onChange={() => {}} />
        <Toggle label="全屏时隐藏（旧版偏好）" description="助手模式在全屏时仍显示麦克风和执行状态。" checked={settings.preferences.hideInFullscreen} disabled onChange={() => {}} />
        <div className="task-actions display-actions"><button type="button" disabled={busy} onClick={() => { void action(() => invoke("set_island_visible", { visible: true }), "灵动岛已显示。"); }}>显示灵动岛</button>
          <button type="button" disabled={busy} onClick={() => { void action(() => invoke("set_island_visible", { visible: false }), "灵动岛已隐藏，可从托盘恢复。"); }}>隐藏灵动岛</button></div>
        <h2>数据来源</h2>
        <Toggle label="元枢任务状态" description="只获取任务阶段和连接信息，关闭后停止连接。" checked={settings.preferences.coreEnabled} disabled={busy || !settings.loaded} onChange={(v) => update("coreEnabled", v)} />
        <Toggle label="媒体信息" description="读取 Windows 提供的曲目和播放状态。" checked={settings.preferences.mediaEnabled} disabled={busy || !settings.loaded} onChange={(v) => update("mediaEnabled", v)} />
        <Toggle label="音量提示" description="音量或静音变化时短暂显示提示。" checked={settings.preferences.volumeEnabled} disabled={busy || !settings.loaded} onChange={(v) => update("volumeEnabled", v)} />
      </section>}
      {tab === "diagnostics" && <section aria-labelledby="diagnostics-title">
        <h2 id="diagnostics-title">连接与恢复</h2><p>{connected ? "正在读取元枢状态。任务操作是否可用，由元枢支持的能力决定。" : "灵动岛会自动尝试连接同一 Windows 用户下运行的元枢。请确认元枢已打开；仍无法连接时，请使用配套版本。"}</p>
        <p className="panel-help">核心退出或重启后，会自动清除旧任务并重新连接。媒体与音量不依赖核心连接。</p>
        <h2>你的数据</h2><p>灵动岛临时展示元枢提供的语音文字、回复和任务状态，不保存这些内容，不截图、不录音。API 配置、聊天记录和任务执行由元枢统一管理。</p>
        <p className="panel-help">设置保存在当前用户的本机配置目录。诊断摘要只包含版本、开关和连接状态，不含任务内容或个人路径。</p>
        <button type="button" disabled={busy} onClick={() => { void action(async () => { const result = await invoke<unknown>("get_island_diagnostics"); setDiagnostics(JSON.stringify(result, null, 2)); }, "诊断摘要已生成，可选中后复制。"); }}>生成诊断摘要</button>
        {diagnostics && <textarea className="diagnostic-text" aria-label="诊断摘要" readOnly value={diagnostics} rows={11} />}
      </section>}
    </div>
    <footer className="panel-footer">关闭此面板后，灵动岛继续运行。完全退出请使用托盘菜单。</footer>
  </main>;
}
