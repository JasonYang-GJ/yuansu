use std::{
    collections::HashMap,
    fs::OpenOptions,
    io::{Read, Write},
    os::windows::io::AsRawHandle,
    sync::{
        atomic::{AtomicBool, Ordering},
        Arc, Mutex,
    },
    thread::{self, JoinHandle},
    time::{Duration, Instant},
};

use serde::{Deserialize, Serialize};
use serde_json::Value;
use tauri::{AppHandle, Emitter, State};
use windows::Win32::{
    Foundation::{CloseHandle, HANDLE},
    Security::{
        GetSidIdentifierAuthority, GetSidSubAuthority, GetSidSubAuthorityCount,
        GetTokenInformation, IsValidSid, TokenUser, SID, TOKEN_QUERY, TOKEN_USER,
    },
    System::{
        Pipes::PeekNamedPipe,
        Threading::{GetCurrentProcess, OpenProcessToken},
    },
};

pub const CORE_BRIDGE_EVENT: &str = "yuanshu-core-bridge-state";
const CORE_SOURCE: &str = "yuanshu.core";
const SNAPSHOT_CAPABILITY: &str = "bridge_snapshot_v1";
const CANCEL_CAPABILITY: &str = "island_cancel_v1";
const DESKTOP_PROTOCOL_VERSION: i32 = 12;
const BRIDGE_PROTOCOL_VERSION: i32 = 1;
const OUTER_FRAME_LIMIT: usize = 4 * 1024 * 1024;
const SNAPSHOT_LIMIT: usize = 1024 * 1024;
const EVENT_BATCH_LIMIT: i64 = 512 * 1024;
const EVENTS_PER_BATCH_LIMIT: i64 = 100;
const EVENT_LIMIT: i64 = 32 * 1024;
const PIPE_TIMEOUT: Duration = Duration::from_secs(3);
const HEALTH_CHECK_INTERVAL: Duration = Duration::from_secs(5);
const STOP_GRANULARITY: Duration = Duration::from_millis(50);

pub struct CoreBridgeHandle {
    stop: Arc<AtomicBool>,
    current: Arc<Mutex<BridgeUiState>>,
    worker: Mutex<Option<JoinHandle<()>>>,
    client_id: String,
    can_cancel: Arc<AtomicBool>,
    cancelling: Arc<AtomicBool>,
}

impl Drop for CoreBridgeHandle {
    fn drop(&mut self) {
        self.stop.store(true, Ordering::Release);
        if let Ok(mut worker) = self.worker.lock() {
            if let Some(worker) = worker.take() {
                let _ = worker.join();
            }
        }
    }
}

#[derive(Clone, Debug, PartialEq, Serialize)]
#[serde(tag = "kind", rename_all = "lowercase")]
pub enum BridgeUiState {
    Snapshot { snapshot: BridgeSnapshot },
    Clear,
}

#[derive(Clone, Debug, PartialEq, Serialize, Deserialize)]
#[serde(deny_unknown_fields)]
pub struct BridgeSnapshot {
    bridge_protocol_version: i32,
    server_instance_id: String,
    snapshot_id: String,
    snapshot_sequence: i64,
    generated_at_utc: String,
    source: String,
    presentations: Vec<BridgePresentation>,
}

#[derive(Clone, Debug, PartialEq, Serialize, Deserialize)]
#[serde(deny_unknown_fields)]
struct BridgePresentation {
    presentation_key: String,
    entity_kind: String,
    session_id: Option<String>,
    turn_id: Option<String>,
    task_id: Option<String>,
    entity_version: i64,
    phase: String,
    island_kind: String,
    title: String,
    subtitle: Option<String>,
    indicator: Option<String>,
    is_terminal: bool,
    requires_user_action: bool,
    updated_at_utc: String,
    actions: Vec<Value>,
}

#[derive(Debug, Deserialize)]
#[serde(deny_unknown_fields)]
struct HandshakeResponse {
    bridge_protocol_version: i32,
    server_instance_id: String,
    server_product: String,
    server_product_version: String,
    desktop_ipc_protocol_version: i32,
    enabled_capabilities: Vec<String>,
    disabled_capabilities: HashMap<String, String>,
    capability_parameters: HashMap<String, Value>,
    limits: BridgeLimits,
    issued_at_utc: String,
}

#[derive(Debug, Deserialize)]
#[serde(deny_unknown_fields)]
struct BridgeLimits {
    outer_frame_bytes: i64,
    snapshot_bytes: i64,
    event_batch_bytes: i64,
    events_per_batch: i64,
    event_bytes: i64,
}

#[derive(Serialize)]
struct HandshakeRequest<'a> {
    bridge_protocol_versions: [i32; 1],
    client_instance_id: &'a str,
    client_product: &'static str,
    client_product_version: &'static str,
    supported_capabilities: [&'static str; 2],
    required_capabilities: [&'static str; 1],
    client_limits: ClientLimits,
}

#[derive(Serialize)]
struct ClientLimits {
    maximum_snapshot_bytes: i64,
    maximum_event_batch_bytes: i64,
    maximum_events_per_batch: i64,
    maximum_event_bytes: i64,
}

#[derive(Serialize)]
struct SnapshotRequest<'a> {
    bridge_protocol_version: i32,
    client_instance_id: &'a str,
    expected_server_instance_id: &'a str,
}

#[derive(Serialize)]
#[serde(rename_all = "camelCase")]
struct DesktopRequest<'a, T> {
    request_id: &'a str,
    method: &'static str,
    payload: T,
    protocol_version: i32,
}

#[derive(Deserialize)]
#[serde(rename_all = "camelCase", deny_unknown_fields)]
struct DesktopResponse {
    request_id: String,
    success: bool,
    payload: Option<Value>,
    error: Option<DesktopError>,
    protocol_version: i32,
}

#[derive(Deserialize)]
#[serde(rename_all = "camelCase", deny_unknown_fields)]
struct DesktopError {
    code: String,
    user_message: String,
    technical_detail: Option<String>,
}

#[derive(Default)]
struct RetryBackoff {
    attempt: usize,
}

impl RetryBackoff {
    fn next_delay(&mut self) -> Duration {
        const DELAYS: [Duration; 7] = [
            Duration::from_millis(250),
            Duration::from_millis(500),
            Duration::from_secs(1),
            Duration::from_secs(2),
            Duration::from_secs(4),
            Duration::from_secs(8),
            Duration::from_secs(15),
        ];
        let delay = DELAYS[self.attempt.min(DELAYS.len() - 1)];
        self.attempt = self.attempt.saturating_add(1);
        delay
    }

    fn reset(&mut self) {
        self.attempt = 0;
    }
}

#[tauri::command]
pub fn get_yuanshu_core_bridge_state(state: State<'_, CoreBridgeHandle>) -> BridgeUiState {
    state
        .current
        .lock()
        .map(|current| current.clone())
        .unwrap_or(BridgeUiState::Clear)
}

pub fn start(app: AppHandle) -> Result<CoreBridgeHandle, String> {
    let client_id = new_id()?;
    let worker_client = client_id.clone();
    let can_cancel = Arc::new(AtomicBool::new(false));
    let worker_capability = Arc::clone(&can_cancel);
    let stop = Arc::new(AtomicBool::new(false));
    let current = Arc::new(Mutex::new(BridgeUiState::Clear));
    let worker_stop = Arc::clone(&stop);
    let worker_current = Arc::clone(&current);
    let worker = thread::Builder::new()
        .name("yuanshu-core-bridge".into())
        .spawn(move || {
            run_worker(
                app,
                worker_stop,
                worker_current,
                worker_client,
                worker_capability,
            )
        })
        .map_err(|error| format!("无法启动元枢 Core Bridge：{error}"))?;
    Ok(CoreBridgeHandle {
        stop,
        current,
        worker: Mutex::new(Some(worker)),
        client_id,
        can_cancel,
        cancelling: Arc::new(AtomicBool::new(false)),
    })
}

fn run_worker(
    app: AppHandle,
    stop: Arc<AtomicBool>,
    current: Arc<Mutex<BridgeUiState>>,
    client_instance_id: String,
    can_cancel: Arc<AtomicBool>,
) {
    let Ok(pipe_name) = current_user_pipe_path() else {
        return;
    };
    let mut backoff = RetryBackoff::default();

    while !stop.load(Ordering::Acquire) {
        if !crate::settings::preferences(&app).core_enabled {
            can_cancel.store(false, Ordering::Release);
            publish_state(&app, &current, BridgeUiState::Clear);
            wait_for_stop(&stop, Duration::from_millis(250));
            continue;
        }
        let handshake = match request_handshake(&pipe_name, &client_instance_id, &stop) {
            Ok(handshake) => handshake,
            Err(_) => {
                can_cancel.store(false, Ordering::Release);
                publish_state(&app, &current, BridgeUiState::Clear);
                wait_for_stop(&stop, backoff.next_delay());
                continue;
            }
        };

        {
            match request_snapshot(&pipe_name, &client_instance_id, &handshake, &stop) {
                Ok(snapshot) => {
                    if !crate::settings::preferences(&app).core_enabled {
                        continue;
                    }
                    can_cancel.store(
                        handshake
                            .enabled_capabilities
                            .iter()
                            .any(|cap| cap == CANCEL_CAPABILITY),
                        Ordering::Release,
                    );
                    publish_state(&app, &current, BridgeUiState::Snapshot { snapshot });
                    backoff.reset();
                }
                Err(_) => {
                    can_cancel.store(false, Ordering::Release);
                    publish_state(&app, &current, BridgeUiState::Clear);
                    wait_for_stop(&stop, backoff.next_delay());
                    continue;
                }
            }
        }
        wait_for_stop(&stop, HEALTH_CHECK_INTERVAL);
    }
}

fn request_handshake(
    pipe_name: &str,
    client_instance_id: &str,
    stop: &AtomicBool,
) -> Result<HandshakeResponse, String> {
    let payload = HandshakeRequest {
        bridge_protocol_versions: [BRIDGE_PROTOCOL_VERSION],
        client_instance_id,
        client_product: "yuanshu-island",
        client_product_version: env!("CARGO_PKG_VERSION"),
        supported_capabilities: [SNAPSHOT_CAPABILITY, CANCEL_CAPABILITY],
        required_capabilities: [SNAPSHOT_CAPABILITY],
        client_limits: ClientLimits {
            maximum_snapshot_bytes: SNAPSHOT_LIMIT as i64,
            maximum_event_batch_bytes: EVENT_BATCH_LIMIT,
            maximum_events_per_batch: EVENTS_PER_BATCH_LIMIT,
            maximum_event_bytes: EVENT_LIMIT,
        },
    };
    let response = send_request(pipe_name, "bridge.handshake", payload, stop)?;
    decode_handshake(response)
}

fn request_snapshot(
    pipe_name: &str,
    client_instance_id: &str,
    handshake: &HandshakeResponse,
    stop: &AtomicBool,
) -> Result<BridgeSnapshot, String> {
    let payload = SnapshotRequest {
        bridge_protocol_version: BRIDGE_PROTOCOL_VERSION,
        client_instance_id,
        expected_server_instance_id: &handshake.server_instance_id,
    };
    let response = send_request(pipe_name, "bridge.snapshot.get", payload, stop)?;
    decode_snapshot(
        response,
        &handshake.server_instance_id,
        handshake.limits.snapshot_bytes as usize,
    )
}

fn send_request<T: Serialize>(
    pipe_name: &str,
    method: &'static str,
    payload: T,
    stop: &AtomicBool,
) -> Result<Value, String> {
    send_request_with_timeout(pipe_name, method, payload, stop, PIPE_TIMEOUT)
}

#[derive(Serialize)]
#[serde(rename_all = "camelCase")]
pub struct ControlStatus {
    can_cancel: bool,
    reason: &'static str,
}

#[tauri::command]
pub fn get_island_control_status(
    app: AppHandle,
    state: State<'_, CoreBridgeHandle>,
) -> ControlStatus {
    let enabled = crate::settings::preferences(&app).core_enabled;
    let connected = state
        .current
        .lock()
        .is_ok_and(|value| matches!(*value, BridgeUiState::Snapshot { .. }));
    let can_cancel = enabled && connected && state.can_cancel.load(Ordering::Acquire);
    ControlStatus {
        can_cancel,
        reason: if !enabled {
            "元枢任务来源已关闭。"
        } else if !connected {
            "元枢未连接，连接恢复后才可操作任务。"
        } else if !can_cancel {
            "此元枢版本仅支持查看状态，请使用支持任务取消的配套版本。"
        } else {
            "任务取消可用。"
        },
    }
}

#[derive(Serialize)]
struct CancelRequest<'a> {
    bridge_protocol_version: i32,
    client_instance_id: &'a str,
    expected_server_instance_id: &'a str,
    presentation_key: &'a str,
    expected_version: i64,
    request_id: &'a str,
}

#[derive(Deserialize)]
#[serde(deny_unknown_fields)]
struct CancelResponse {
    accepted: bool,
    duplicate: bool,
}

fn validate_cancel_target(
    state: &BridgeUiState,
    server: &str,
    key: &str,
    version: i64,
) -> Result<(), String> {
    let BridgeUiState::Snapshot { snapshot } = state else {
        return Err("元枢未连接，请稍后重试。".into());
    };
    if snapshot.server_instance_id != server {
        return Err("元枢已重新连接，请重新打开任务详情。".into());
    }
    if !snapshot.presentations.iter().any(|item| {
        item.presentation_key == key && item.entity_version == version && !item.is_terminal
    }) {
        return Err("任务状态已变化，请查看最新状态后再操作。".into());
    }
    Ok(())
}

#[tauri::command]
pub async fn cancel_island_task(
    app: AppHandle,
    state: State<'_, CoreBridgeHandle>,
    expected_server_instance_id: String,
    presentation_key: String,
    expected_version: i64,
) -> Result<(), String> {
    if !crate::settings::preferences(&app).core_enabled || !state.can_cancel.load(Ordering::Acquire)
    {
        return Err("当前元枢连接不支持取消任务。".into());
    }
    validate_cancel_target(
        &*state.current.lock().map_err(|_| "无法读取任务状态。")?,
        &expected_server_instance_id,
        &presentation_key,
        expected_version,
    )?;
    if state.cancelling.swap(true, Ordering::AcqRel) {
        return Err("已有取消请求正在处理，请稍候。".into());
    }
    let busy = Arc::clone(&state.cancelling);
    let stop = Arc::clone(&state.stop);
    let client_id = state.client_id.clone();
    let result = tauri::async_runtime::spawn_blocking(move || {
        let request_id = new_id()?;
        let response = send_request_with_timeout(
            &current_user_pipe_path()?,
            "bridge.task.cancel",
            CancelRequest {
                bridge_protocol_version: BRIDGE_PROTOCOL_VERSION,
                client_instance_id: &client_id,
                expected_server_instance_id: &expected_server_instance_id,
                presentation_key: &presentation_key,
                expected_version,
                request_id: &request_id,
            },
            &stop,
            Duration::from_secs(20),
        )
        .map_err(|code| {
            match code.as_str() {
                "entity_version_mismatch" | "target_not_active" => {
                    "任务已经变化或结束，请查看最新状态。"
                }
                "server_instance_mismatch" => "元枢已重启，请等待重新连接。",
                "capability_not_enabled" => "当前元枢版本不支持取消，请回到元枢处理。",
                _ => "未能确认取消结果，请查看任务状态；不会自动重复发送。",
            }
            .to_string()
        })?;
        let receipt: CancelResponse =
            serde_json::from_value(response).map_err(|_| "取消响应无效，请查看任务状态。")?;
        let _ = receipt.duplicate;
        if !receipt.accepted {
            return Err("取消请求未被接受，请查看任务状态。".into());
        }
        Ok(())
    })
    .await
    .map_err(|_| "取消请求处理失败，请查看任务状态。".to_string());
    busy.store(false, Ordering::Release);
    result?
}

#[tauri::command]
pub fn get_island_diagnostics(app: AppHandle, state: State<'_, CoreBridgeHandle>) -> Value {
    let control = get_island_control_status(app.clone(), state);
    serde_json::json!({ "application": "yuanshu-island", "version": env!("CARGO_PKG_VERSION"),
        "desktopProtocol": DESKTOP_PROTOCOL_VERSION, "bridgeProtocol": BRIDGE_PROTOCOL_VERSION,
        "control": control, "settings": crate::settings::preferences(&app) })
}

fn send_request_with_timeout<T: Serialize>(
    pipe_name: &str,
    method: &'static str,
    payload: T,
    stop: &AtomicBool,
    timeout: Duration,
) -> Result<Value, String> {
    let request_id = new_id()?;
    let request = DesktopRequest {
        request_id: &request_id,
        method,
        payload,
        protocol_version: DESKTOP_PROTOCOL_VERSION,
    };
    let bytes = serde_json::to_vec(&request).map_err(|_| "请求编码失败".to_string())?;
    if bytes.is_empty() || bytes.len() > OUTER_FRAME_LIMIT {
        return Err("请求大小无效".into());
    }

    let mut pipe = OpenOptions::new()
        .read(true)
        .write(true)
        .open(pipe_name)
        .map_err(|_| "Core Named Pipe 当前不可用".to_string())?;
    pipe.write_all(&(bytes.len() as u32).to_le_bytes())
        .and_then(|_| pipe.write_all(&bytes))
        .and_then(|_| pipe.flush())
        .map_err(|_| "请求写入失败".to_string())?;

    wait_for_pipe_bytes(&pipe, 4, stop, timeout)?;
    let mut header = [0_u8; 4];
    pipe.read_exact(&mut header)
        .map_err(|_| "响应头读取失败".to_string())?;
    let length = u32::from_le_bytes(header) as usize;
    if length == 0 || length > OUTER_FRAME_LIMIT {
        return Err("响应长度无效".into());
    }
    wait_for_pipe_bytes(&pipe, length, stop, timeout)?;
    let mut body = vec![0_u8; length];
    pipe.read_exact(&mut body)
        .map_err(|_| "响应读取失败".to_string())?;
    let response: DesktopResponse =
        serde_json::from_slice(&body).map_err(|_| "响应 JSON 无效".to_string())?;
    if response.request_id != request_id || response.protocol_version != DESKTOP_PROTOCOL_VERSION {
        return Err("响应身份或协议不匹配".into());
    }
    if !response.success {
        let error = response
            .error
            .ok_or_else(|| "Core 返回未知错误".to_string())?;
        if error.code.is_empty()
            || error.user_message.chars().count() > 512
            || error
                .technical_detail
                .as_deref()
                .is_some_and(|value| value.chars().count() > 512)
        {
            return Err("Core 返回无效错误".into());
        }
        return Err(error.code);
    }
    if response.error.is_some() {
        return Err("成功响应携带错误".into());
    }
    response.payload.ok_or_else(|| "响应缺少 payload".into())
}

fn wait_for_pipe_bytes(
    pipe: &std::fs::File,
    expected: usize,
    stop: &AtomicBool,
    timeout: Duration,
) -> Result<(), String> {
    let deadline = Instant::now() + timeout;
    loop {
        if stop.load(Ordering::Acquire) {
            return Err("Bridge 正在退出".into());
        }
        let mut available = 0_u32;
        let handle = HANDLE(pipe.as_raw_handle());
        unsafe { PeekNamedPipe(handle, None, 0, None, Some(&mut available), None) }
            .map_err(|_| "Named Pipe 已断开".to_string())?;
        if available as usize >= expected {
            return Ok(());
        }
        if Instant::now() >= deadline {
            return Err("Core 响应超时".into());
        }
        thread::sleep(STOP_GRANULARITY);
    }
}

fn decode_handshake(value: Value) -> Result<HandshakeResponse, String> {
    let handshake: HandshakeResponse =
        serde_json::from_value(value).map_err(|_| "握手响应结构无效".to_string())?;
    if handshake.bridge_protocol_version != BRIDGE_PROTOCOL_VERSION
        || handshake.desktop_ipc_protocol_version != DESKTOP_PROTOCOL_VERSION
        || !is_id(&handshake.server_instance_id)
        || !(handshake.enabled_capabilities == [SNAPSHOT_CAPABILITY]
            || handshake.enabled_capabilities == [SNAPSHOT_CAPABILITY, CANCEL_CAPABILITY])
        || !handshake.capability_parameters.is_empty()
        || !is_safe_token(&handshake.server_product, 64)
        || !is_safe_product_version(&handshake.server_product_version, 64)
        || !is_timestamp(&handshake.issued_at_utc)
        || handshake.disabled_capabilities.len() > 32
        || handshake
            .disabled_capabilities
            .iter()
            .any(|(key, value)| !is_safe_token(key, 64) || !is_safe_text(value, 160, false))
        || !valid_limits(&handshake.limits)
    {
        return Err("握手响应语义无效".into());
    }
    Ok(handshake)
}

fn valid_limits(limits: &BridgeLimits) -> bool {
    limits.outer_frame_bytes > 0
        && limits.outer_frame_bytes <= OUTER_FRAME_LIMIT as i64
        && limits.snapshot_bytes > 0
        && limits.snapshot_bytes <= SNAPSHOT_LIMIT as i64
        && limits.event_batch_bytes > 0
        && limits.event_batch_bytes <= EVENT_BATCH_LIMIT
        && limits.events_per_batch > 0
        && limits.events_per_batch <= EVENTS_PER_BATCH_LIMIT
        && limits.event_bytes > 0
        && limits.event_bytes <= EVENT_LIMIT
}

fn decode_snapshot(
    value: Value,
    expected_server_instance_id: &str,
    maximum_bytes: usize,
) -> Result<BridgeSnapshot, String> {
    let encoded = serde_json::to_vec(&value).map_err(|_| "Snapshot 编码无效".to_string())?;
    if encoded.is_empty() || encoded.len() > maximum_bytes.min(SNAPSHOT_LIMIT) {
        return Err("Snapshot 超过协商限制".into());
    }
    let snapshot: BridgeSnapshot =
        serde_json::from_value(value).map_err(|_| "Snapshot 结构无效".to_string())?;
    if snapshot.bridge_protocol_version != BRIDGE_PROTOCOL_VERSION
        || snapshot.server_instance_id != expected_server_instance_id
        || !is_id(&snapshot.server_instance_id)
        || !is_id(&snapshot.snapshot_id)
        || snapshot.snapshot_sequence < 0
        || !is_timestamp(&snapshot.generated_at_utc)
        || snapshot.source != CORE_SOURCE
        || snapshot.presentations.len() > 32
    {
        return Err("Snapshot 语义无效".into());
    }
    let mut keys = std::collections::HashSet::new();
    if snapshot.presentations.iter().any(|presentation| {
        !keys.insert(presentation.presentation_key.as_str()) || !valid_presentation(presentation)
    }) {
        return Err("Snapshot Presentation 无效".into());
    }
    Ok(snapshot)
}

fn valid_presentation(value: &BridgePresentation) -> bool {
    if value.entity_version < 0
        || !is_safe_text(&value.phase, 64, false)
        || expected_kind(&value.phase) != Some(value.island_kind.as_str())
        || !is_safe_text(&value.title, 160, false)
        || value
            .subtitle
            .as_deref()
            .is_some_and(|text| !is_safe_text(text, 300, true))
        || value
            .indicator
            .as_deref()
            .is_some_and(|text| !is_safe_text(text, 64, true))
        || value.is_terminal != matches!(value.island_kind.as_str(), "Success" | "Error")
        || value.requires_user_action != (value.island_kind == "WaitingUser")
        || !is_timestamp(&value.updated_at_utc)
        || !value.actions.is_empty()
    {
        return false;
    }
    match value.entity_kind.as_str() {
        "turn" => {
            value.session_id.as_deref().is_some_and(is_id)
                && value.turn_id.as_deref().is_some_and(is_id)
                && value.task_id.as_deref().is_none_or(is_id)
                && value.presentation_key
                    == format!("turn:{}", value.turn_id.as_deref().unwrap_or_default())
        }
        "task" => {
            value.session_id.is_none()
                && value.turn_id.is_none()
                && value.task_id.as_deref().is_some_and(is_id)
                && value.presentation_key
                    == format!("task:{}", value.task_id.as_deref().unwrap_or_default())
        }
        _ => false,
    }
}

fn expected_kind(phase: &str) -> Option<&'static str> {
    match phase {
        "Understanding" | "Responding" => Some("Thinking"),
        "WaitingForProject"
        | "WaitingForFile"
        | "WaitingForWindow"
        | "WaitingForWindowConsent"
        | "WaitingForConfirmation"
        | "WaitingForUser"
        | "WaitingUser" => Some("WaitingUser"),
        "Executing"
        | "ObservingWindow"
        | "ProgrammingTask"
        | "Pending"
        | "Running"
        | "CancellationRequested" => Some("Executing"),
        "Completed" | "Succeeded" | "Cancelled" => Some("Success"),
        "Failed" | "Interrupted" => Some("Error"),
        _ => None,
    }
}

fn is_id(value: &str) -> bool {
    if value.len() != 36 || value == "00000000-0000-0000-0000-000000000000" {
        return false;
    }
    value.bytes().enumerate().all(|(index, byte)| match index {
        8 | 13 | 18 | 23 => byte == b'-',
        _ => byte.is_ascii_digit() || (b'a'..=b'f').contains(&byte),
    })
}

fn is_safe_token(value: &str, maximum: usize) -> bool {
    !value.is_empty()
        && value.len() <= maximum
        && value
            .bytes()
            .all(|byte| byte.is_ascii_alphanumeric() || matches!(byte, b'.' | b'_' | b'-'))
}

fn is_safe_product_version(value: &str, maximum: usize) -> bool {
    if value.is_empty() || value.len() > maximum || !value.is_ascii() {
        return false;
    }

    let mut parts = value.split('+');
    let version = parts.next().unwrap_or_default();
    let build_metadata = parts.next();
    if parts.next().is_some() || !is_semver_compatible_part(version) {
        return false;
    }

    build_metadata.is_none_or(is_semver_compatible_part)
}

fn is_semver_compatible_part(value: &str) -> bool {
    !value.is_empty()
        && value.split('.').all(|identifier| {
            !identifier.is_empty()
                && identifier
                    .bytes()
                    .all(|byte| byte.is_ascii_alphanumeric() || byte == b'-')
        })
}

fn is_safe_text(value: &str, maximum: usize, allow_empty: bool) -> bool {
    (allow_empty || !value.is_empty())
        && value.chars().count() <= maximum
        && !value.chars().any(is_unsafe_character)
}

fn is_unsafe_character(value: char) -> bool {
    value.is_control()
        || matches!(
            value,
            '\u{061c}'
                | '\u{200b}'
                | '\u{200c}'
                | '\u{200e}'
                | '\u{200f}'
                | '\u{202a}'..='\u{202e}'
                | '\u{2060}'..='\u{2069}'
                | '\u{feff}'
        )
}

fn is_timestamp(value: &str) -> bool {
    value.ends_with('Z')
        && value.len() >= 20
        && value.len() <= 40
        && !value.chars().any(is_unsafe_character)
}

fn publish_state(app: &AppHandle, current: &Mutex<BridgeUiState>, next: BridgeUiState) {
    let should_emit = current.lock().is_ok_and(|mut state| {
        if same_projection(&state, &next) {
            false
        } else {
            *state = next.clone();
            true
        }
    });
    if should_emit {
        let _ = app.emit(CORE_BRIDGE_EVENT, next);
    }
}

fn same_projection(left: &BridgeUiState, right: &BridgeUiState) -> bool {
    match (left, right) {
        (BridgeUiState::Clear, BridgeUiState::Clear) => true,
        (
            BridgeUiState::Snapshot { snapshot: left },
            BridgeUiState::Snapshot { snapshot: right },
        ) => {
            left.server_instance_id == right.server_instance_id
                && left.source == right.source
                && left.presentations == right.presentations
        }
        _ => false,
    }
}

fn wait_for_stop(stop: &AtomicBool, duration: Duration) {
    let deadline = Instant::now() + duration;
    while !stop.load(Ordering::Acquire) && Instant::now() < deadline {
        thread::sleep(STOP_GRANULARITY.min(deadline.saturating_duration_since(Instant::now())));
    }
}

fn new_id() -> Result<String, String> {
    windows_core::GUID::new()
        .map(|id| format!("{id:?}").to_ascii_lowercase())
        .map_err(|_| "无法生成 Bridge 请求标识".to_string())
}

fn current_user_pipe_path() -> Result<String, String> {
    // The integrated child uses exactly the Core endpoint selected by its
    // launcher. Standalone/profile behavior remains unchanged.
    if let Some(pipe) = crate::assistant::integrated_core_pipe()? {
        return Ok(format!(r"\\.\pipe\{pipe}"));
    }
    let sid = current_user_sid()?;
    let profile = crate::settings::profile()?;
    Ok(format!(
        r"\\.\pipe\ScreenGuide.DesktopHost.V01.{}{}",
        sid.replace('-', "_"),
        profile
            .map(|value| format!(".Island.{value}"))
            .unwrap_or_default()
    ))
}

fn current_user_sid() -> Result<String, String> {
    struct OwnedHandle(HANDLE);
    impl Drop for OwnedHandle {
        fn drop(&mut self) {
            let _ = unsafe { CloseHandle(self.0) };
        }
    }

    let mut raw_token = HANDLE::default();
    unsafe { OpenProcessToken(GetCurrentProcess(), TOKEN_QUERY, &mut raw_token) }
        .map_err(|_| "无法读取当前用户令牌".to_string())?;
    let token = OwnedHandle(raw_token);
    let mut required = 0_u32;
    let _ = unsafe { GetTokenInformation(token.0, TokenUser, None, 0, &mut required) };
    if required < std::mem::size_of::<TOKEN_USER>() as u32 {
        return Err("当前用户令牌内容无效".into());
    }
    let mut buffer = vec![0_u8; required as usize];
    unsafe {
        GetTokenInformation(
            token.0,
            TokenUser,
            Some(buffer.as_mut_ptr().cast()),
            required,
            &mut required,
        )
    }
    .map_err(|_| "无法读取当前用户 SID".to_string())?;
    let user = unsafe { &*(buffer.as_ptr().cast::<TOKEN_USER>()) };
    if !unsafe { IsValidSid(user.User.Sid) }.as_bool() {
        return Err("当前用户 SID 无效".into());
    }
    let authority = unsafe { (*GetSidIdentifierAuthority(user.User.Sid)).Value };
    let authority_value = authority
        .into_iter()
        .fold(0_u64, |value, byte| (value << 8) | u64::from(byte));
    let sub_authority_count = unsafe { *GetSidSubAuthorityCount(user.User.Sid) } as u32;
    let sid = unsafe { &*user.User.Sid.0.cast::<SID>() };
    let mut result = format!("S-{}-{authority_value}", sid.Revision);
    for index in 0..sub_authority_count {
        let sub_authority = unsafe { *GetSidSubAuthority(user.User.Sid, index) };
        result.push_str(&format!("-{sub_authority}"));
    }
    Ok(result)
}

#[cfg(test)]
mod tests {
    use super::*;

    const SERVER_ID: &str = "aaaaaaaa-aaaa-4aaa-8aaa-aaaaaaaaaaaa";
    const SNAPSHOT_ID: &str = "55555555-5555-4555-8555-555555555555";

    fn handshake_json() -> serde_json::Value {
        serde_json::json!({
            "bridge_protocol_version": 1,
            "server_instance_id": SERVER_ID,
            "server_product": "yuanshu-core",
            "server_product_version": "2.0.0",
            "desktop_ipc_protocol_version": DESKTOP_PROTOCOL_VERSION,
            "enabled_capabilities": ["bridge_snapshot_v1"],
            "disabled_capabilities": {},
            "capability_parameters": {},
            "limits": {
                "outer_frame_bytes": 4_194_304,
                "snapshot_bytes": 1_048_576,
                "event_batch_bytes": 524_288,
                "events_per_batch": 100,
                "event_bytes": 32_768
            },
            "issued_at_utc": "2026-08-26T00:00:00Z"
        })
    }

    fn snapshot_json() -> serde_json::Value {
        serde_json::json!({
            "bridge_protocol_version": 1,
            "server_instance_id": SERVER_ID,
            "snapshot_id": SNAPSHOT_ID,
            "snapshot_sequence": 0,
            "generated_at_utc": "2026-08-26T00:00:00Z",
            "source": "yuanshu.core",
            "presentations": [{
                "presentation_key": "turn:33333333-3333-4333-8333-333333333333",
                "entity_kind": "turn",
                "session_id": "22222222-2222-4222-8222-222222222222",
                "turn_id": "33333333-3333-4333-8333-333333333333",
                "task_id": null,
                "entity_version": 7,
                "phase": "Executing",
                "island_kind": "Executing",
                "title": "正在执行",
                "subtitle": "请在元枢主窗口查看详情。",
                "indicator": "Executing",
                "is_terminal": false,
                "requires_user_action": false,
                "updated_at_utc": "2026-08-26T00:00:00Z",
                "actions": []
            }]
        })
    }

    #[test]
    fn accepts_optional_cancel_but_does_not_require_it_for_old_cores() {
        assert!(decode_handshake(handshake_json()).is_ok());
        let mut value = handshake_json();
        value["enabled_capabilities"] = serde_json::json!([SNAPSHOT_CAPABILITY, CANCEL_CAPABILITY]);
        assert!(decode_handshake(value.clone()).is_ok());
        value["enabled_capabilities"] = serde_json::json!([CANCEL_CAPABILITY]);
        assert!(decode_handshake(value).is_err());
    }

    #[test]
    fn cancellation_rejects_stale_version_restarted_server_and_disconnected_state() {
        let snapshot: BridgeSnapshot = serde_json::from_value(snapshot_json()).unwrap();
        let key = snapshot.presentations[0].presentation_key.clone();
        let state = BridgeUiState::Snapshot { snapshot };
        assert!(validate_cancel_target(&state, SERVER_ID, &key, 7).is_ok());
        assert!(validate_cancel_target(&state, SERVER_ID, &key, 6).is_err());
        assert!(validate_cancel_target(&state, SNAPSHOT_ID, &key, 7).is_err());
        assert!(validate_cancel_target(&BridgeUiState::Clear, SERVER_ID, &key, 7).is_err());
        assert!(validate_cancel_target(&state, SERVER_ID, "another-target", 7).is_err());
    }

    #[test]
    fn accepts_only_the_negotiated_snapshot_capability_and_safe_limits() {
        let handshake = decode_handshake(handshake_json()).expect("valid handshake");
        assert_eq!(handshake.server_instance_id, SERVER_ID);
        assert_eq!(
            handshake.desktop_ipc_protocol_version,
            DESKTOP_PROTOCOL_VERSION
        );

        let mut invalid = handshake_json();
        invalid["enabled_capabilities"] =
            serde_json::json!(["bridge_snapshot_v1", "tool_execution_v1"]);
        assert!(decode_handshake(invalid).is_err());

        for unsupported in [8, 10, 11, 13] {
            let mut wrong_desktop_protocol = handshake_json();
            wrong_desktop_protocol["desktop_ipc_protocol_version"] = serde_json::json!(unsupported);
            assert!(decode_handshake(wrong_desktop_protocol).is_err());
        }
    }

    #[test]
    fn accepts_semver_compatible_server_product_versions_and_rejects_unsafe_forms() {
        for version in ["0.6.0", "0.6.0+294e07074b4c62d808ccc8419990ab0a56cdb3cb"] {
            let mut handshake = handshake_json();
            handshake["server_product_version"] = serde_json::json!(version);
            assert!(
                decode_handshake(handshake).is_ok(),
                "expected version to be accepted: {version}"
            );
        }

        for version in [
            "0.6.0+bad\nvalue",
            "0.6.0+bad\u{202e}value",
            "0.6.0+bad value",
            "0.6.0+bad/value",
            "0.6.0+bad\\value",
            "0.6.0++build",
            "+0.6.0",
            "0.6.0+",
        ] {
            let mut handshake = handshake_json();
            handshake["server_product_version"] = serde_json::json!(version);
            assert!(
                decode_handshake(handshake).is_err(),
                "expected version to be rejected: {version:?}"
            );
        }

        let mut oversized = handshake_json();
        oversized["server_product_version"] =
            serde_json::json!(format!("0.6.0+{}", "a".repeat(59)));
        assert!(decode_handshake(oversized).is_err());
    }

    #[test]
    fn rejects_malformed_sensitive_or_oversized_snapshot_fields() {
        let snapshot =
            decode_snapshot(snapshot_json(), SERVER_ID, 1_048_576).expect("valid snapshot");
        assert_eq!(snapshot.presentations.len(), 1);

        let mut sensitive = snapshot_json();
        sensitive["input_text"] = serde_json::json!("secret");
        assert!(decode_snapshot(sensitive, SERVER_ID, 1_048_576).is_err());

        let mut unsafe_title = snapshot_json();
        unsafe_title["presentations"][0]["title"] = serde_json::json!("bad\u{202e}title");
        assert!(decode_snapshot(unsafe_title, SERVER_ID, 1_048_576).is_err());

        assert!(decode_snapshot(snapshot_json(), SERVER_ID, 16).is_err());
    }

    #[test]
    fn reconnect_backoff_is_bounded_and_resettable() {
        let mut backoff = RetryBackoff::default();
        assert_eq!(backoff.next_delay(), std::time::Duration::from_millis(250));
        assert_eq!(backoff.next_delay(), std::time::Duration::from_millis(500));
        assert_eq!(backoff.next_delay(), std::time::Duration::from_secs(1));
        for _ in 0..8 {
            let _ = backoff.next_delay();
        }
        assert_eq!(backoff.next_delay(), std::time::Duration::from_secs(15));
        backoff.reset();
        assert_eq!(backoff.next_delay(), std::time::Duration::from_millis(250));
    }

    #[test]
    fn process_identity_builds_the_registered_current_user_pipe_name() {
        assert!(is_id(&new_id().expect("new GUID")));
        let pipe = current_user_pipe_path().expect("current user SID");
        assert!(pipe.starts_with(r"\\.\pipe\ScreenGuide.DesktopHost.V01.S_1_5_"));
    }

    #[test]
    fn duplicate_poll_snapshots_do_not_reenter_but_restart_and_clear_do() {
        let first = BridgeUiState::Snapshot {
            snapshot: decode_snapshot(snapshot_json(), SERVER_ID, 1_048_576).unwrap(),
        };
        let mut duplicate_value = snapshot_json();
        duplicate_value["snapshot_id"] = serde_json::json!("66666666-6666-4666-8666-666666666666");
        duplicate_value["generated_at_utc"] = serde_json::json!("2026-08-26T00:00:01Z");
        let duplicate = BridgeUiState::Snapshot {
            snapshot: decode_snapshot(duplicate_value, SERVER_ID, 1_048_576).unwrap(),
        };

        assert!(same_projection(&first, &duplicate));
        assert!(!same_projection(&first, &BridgeUiState::Clear));
        assert!(HEALTH_CHECK_INTERVAL >= std::time::Duration::from_secs(5));
    }
}
