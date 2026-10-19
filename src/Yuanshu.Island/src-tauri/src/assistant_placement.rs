//! User-owned window placement, separate from Host/session and voice state.
use crate::window::{IslandMode, PhysicalWindowFrame, WorkAreaGeometry};
use serde::{Deserialize, Serialize};
use std::{
    fs,
    io::Write,
    path::PathBuf,
    sync::{
        atomic::{AtomicBool, Ordering},
        mpsc, Arc, Mutex,
    },
    thread::{self, JoinHandle},
    time::Duration,
};
use tauri::{AppHandle, Emitter, Manager, WebviewWindow};

const EVENT: &str = "assistant-placement-changed";
const SNAP_DISTANCE: f64 = 24.0;
const EDGE_GAP: f64 = 4.0;
const ORB_DIAMETER: f64 = 44.0;
const ORB_VISIBLE_FRACTION: f64 = 1.0 / 3.0;

#[derive(Clone, Copy, Debug, PartialEq, Eq, Serialize, Deserialize)]
#[serde(rename_all = "lowercase")]
pub enum Edge {
    Left,
    Right,
    Top,
    Bottom,
}

#[derive(Clone, Debug, PartialEq, Serialize, Deserialize)]
#[serde(rename_all = "camelCase", deny_unknown_fields)]
struct SavedPlacement {
    schema_version: u32,
    monitor: String,
    center_x: f64,
    center_y: f64,
    edge: Option<Edge>,
}

#[derive(Clone, Debug, Serialize)]
#[serde(rename_all = "camelCase")]
pub struct PlacementSnapshot {
    revision: u64,
    mode: &'static str,
    edge: Option<Edge>,
    dragging: bool,
    error: Option<String>,
}

impl Default for PlacementSnapshot {
    fn default() -> Self {
        Self {
            revision: 0,
            mode: "compact",
            edge: None,
            dragging: false,
            error: None,
        }
    }
}

#[derive(Clone, Debug, PartialEq)]
struct Monitor {
    id: String,
    area: WorkAreaGeometry,
}
struct DragSession {
    start: PhysicalWindowFrame,
    expanded: bool,
    stop: Arc<AtomicBool>,
}
type ModeReply = mpsc::Sender<Result<(), String>>;
struct Inner {
    saved: Option<SavedPlacement>,
    snapshot: PlacementSnapshot,
    expanded: bool,
    drag: Option<DragSession>,
    pending: Option<(IslandMode, ModeReply)>,
    generation: u64,
    monitors: Vec<Monitor>,
}
pub struct AssistantPlacement {
    path: PathBuf,
    damaged: AtomicBool,
    inner: Mutex<Inner>,
    worker: Mutex<Option<JoinHandle<()>>>,
}

impl AssistantPlacement {
    pub fn load(path: PathBuf) -> Self {
        let (saved, error, damaged) = match fs::read(&path) {
            Ok(bytes) => match decode(&bytes) {
                Ok(value) => (Some(value), None, false),
                Err(_) => (
                    None,
                    Some("位置记录无法读取，已回到默认位置。".into()),
                    true,
                ),
            },
            Err(error) if error.kind() == std::io::ErrorKind::NotFound => (None, None, false),
            Err(_) => (
                None,
                Some("位置记录暂不可用，已回到默认位置。".into()),
                true,
            ),
        };
        let edge = saved.as_ref().and_then(|value| value.edge);
        Self {
            path,
            damaged: AtomicBool::new(damaged),
            inner: Mutex::new(Inner {
                saved,
                snapshot: PlacementSnapshot {
                    edge,
                    mode: resting_mode(edge),
                    error,
                    ..Default::default()
                },
                expanded: false,
                drag: None,
                pending: None,
                generation: 0,
                monitors: Vec::new(),
            }),
            worker: Mutex::new(None),
        }
    }

    fn save(&self, value: &SavedPlacement) -> Result<(), String> {
        let parent = self.path.parent().ok_or("位置目录不可用")?;
        fs::create_dir_all(parent).map_err(|_| "无法创建位置目录")?;
        let destination = fs::canonicalize(parent)
            .map_err(|_| "无法读取位置目录")?
            .join(self.path.file_name().ok_or("位置文件名不可用")?);
        if self.damaged.load(Ordering::Acquire) && destination.exists() {
            let stamp = std::time::SystemTime::now()
                .duration_since(std::time::UNIX_EPOCH)
                .map_err(|_| "系统时间不可用")?
                .as_nanos();
            fs::copy(
                &destination,
                destination.with_extension(format!("backup-{stamp}.json")),
            )
            .map_err(|_| "无法保留原位置记录，本次位置未保存")?;
        }
        let bytes = serde_json::to_vec_pretty(value).map_err(|_| "无法编码位置记录")?;
        let temporary = destination.with_extension("pending");
        let mut file = fs::File::create(&temporary).map_err(|_| "无法保存位置记录")?;
        file.write_all(&bytes)
            .and_then(|()| file.sync_all())
            .map_err(|_| "位置写入失败")?;
        drop(file);
        fs::rename(&temporary, &destination).map_err(|_| "无法替换位置记录")?;
        self.damaged.store(false, Ordering::Release);
        Ok(())
    }

    pub fn shutdown(&self) {
        if let Ok(mut inner) = self.inner.lock() {
            if let Some(drag) = inner.drag.take() {
                drag.stop.store(true, Ordering::Release);
            }
            if let Some((_, reply)) = inner.pending.take() {
                let _ = reply.send(Err("灵动岛正在退出".into()));
            }
        }
        if let Some(worker) = self.worker.lock().ok().and_then(|mut value| value.take()) {
            let _ = worker.join();
        }
    }
}

impl Drop for AssistantPlacement {
    fn drop(&mut self) {
        self.shutdown();
    }
}

fn decode(bytes: &[u8]) -> Result<SavedPlacement, ()> {
    if bytes.len() > 4096 {
        return Err(());
    }
    let value: SavedPlacement = serde_json::from_slice(bytes).map_err(|_| ())?;
    if value.schema_version != 1
        || value.monitor.is_empty()
        || value.monitor.len() > 256
        || value.monitor.chars().any(char::is_control)
        || !value.center_x.is_finite()
        || !value.center_y.is_finite()
        || value.center_x.abs() > 1_000_000.0
        || value.center_y.abs() > 1_000_000.0
    {
        return Err(());
    }
    Ok(value)
}

fn resting_mode(edge: Option<Edge>) -> &'static str {
    if edge.is_some() {
        "orb"
    } else {
        "compact"
    }
}

#[tauri::command]
pub fn assistant_placement_state(app: AppHandle) -> Result<Option<PlacementSnapshot>, String> {
    if !crate::assistant::is_integrated(&app) {
        return Ok(None);
    }
    app.state::<AssistantPlacement>()
        .inner
        .lock()
        .map(|value| Some(value.snapshot.clone()))
        .map_err(|_| "无法读取灵动岛位置".into())
}

pub fn is_dragging(app: &AppHandle) -> bool {
    app.try_state::<AssistantPlacement>()
        .is_some_and(|state| state.inner.lock().is_ok_and(|value| value.drag.is_some()))
}

pub fn is_resting(app: &AppHandle) -> bool {
    app.try_state::<AssistantPlacement>()
        .is_none_or(|state| state.inner.lock().is_ok_and(|value| !value.expanded))
}

fn emit(app: &AppHandle) -> Result<(), String> {
    let state = app.state::<AssistantPlacement>();
    let value = state
        .inner
        .lock()
        .map_err(|_| "无法读取灵动岛位置")?
        .snapshot
        .clone();
    app.emit_to("main", EVENT, value)
        .map_err(|error| format!("无法通知灵动岛位置：{error}"))
}

fn require_main(window: &WebviewWindow) -> Result<(), String> {
    if window.label() != "main" || !crate::assistant::is_integrated(window.app_handle()) {
        return Err("仅元枢主灵动岛支持此操作".into());
    }
    Ok(())
}

#[tauri::command]
pub fn assistant_open_controls(window: WebviewWindow) -> Result<(), String> {
    require_main(&window)?;
    if is_dragging(window.app_handle()) {
        return Err("请先结束拖动".into());
    }
    crate::assistant_surface::show_controls(window.app_handle())
}

pub fn request_mode(window: &WebviewWindow, mode: IslandMode, reply: ModeReply) {
    let state = window.app_handle().state::<AssistantPlacement>();
    let Ok(mut inner) = state.inner.lock() else {
        let _ = reply.send(Err("无法调整灵动岛位置".into()));
        return;
    };
    if inner.drag.is_some() {
        if let Some((_, previous)) = inner.pending.replace((mode, reply)) {
            let _ = previous.send(Err("显示模式已更新".into()));
        }
        return;
    }
    drop(inner);
    let _ = reply.send(apply_mode(window, mode));
}

pub fn apply_mode(window: &WebviewWindow, mode: IslandMode) -> Result<(), String> {
    let app = window.app_handle();
    let state = app.state::<AssistantPlacement>();
    let saved = {
        let inner = state.inner.lock().map_err(|_| "无法读取灵动岛位置")?;
        if inner.drag.is_some() {
            return Err("拖动期间暂不调整尺寸".into());
        }
        inner.saved.clone()
    };
    let monitors = monitors(window)?;
    let monitor = resolve_monitor(saved.as_ref(), &monitors)?;
    let frame = anchored_frame(saved.as_ref(), &monitor.area, mode == IslandMode::Expanded);
    if mode != IslandMode::Expanded && crate::assistant_surface::is_interactive(app) {
        crate::assistant_surface::release_controls(app)?;
    }
    crate::window::apply_assistant_frame(window, frame)?;
    state
        .inner
        .lock()
        .map_err(|_| "无法更新灵动岛位置")?
        .expanded = mode == IslandMode::Expanded;
    crate::window::set_assistant_input(window, crate::assistant_surface::is_interactive(app))?;
    state
        .inner
        .lock()
        .map_err(|_| "无法更新显示器布局")?
        .monitors = monitors;
    Ok(())
}

/// Called by the existing presence timer on the UI thread. A display unplug,
/// work-area or DPI change must not strand an idle capsule off screen. This
/// never persists a new anchor and leaves an active OS move loop untouched.
pub fn refresh_display_layout(app: &AppHandle) {
    let Some(window) = app.get_webview_window("main") else {
        return;
    };
    let Ok(available) = monitors(&window) else {
        return;
    };
    let state = app.state::<AssistantPlacement>();
    let mode = {
        let Ok(inner) = state.inner.lock() else {
            return;
        };
        if inner.drag.is_some() || inner.monitors == available {
            return;
        }
        if inner.expanded {
            IslandMode::Expanded
        } else {
            IslandMode::Compact
        }
    };
    // On a transient display-driver failure, retry at the next presence tick.
    let _ = apply_mode(&window, mode);
}

#[tauri::command]
pub fn assistant_begin_drag(window: WebviewWindow) -> Result<(), String> {
    require_main(&window)?;
    let app = window.app_handle();
    let state = app.state::<AssistantPlacement>();
    let start = current_frame(&window)?;
    let expanded = state
        .inner
        .lock()
        .map_err(|_| "无法读取灵动岛位置")?
        .expanded;
    if expanded && !crate::assistant_surface::is_interactive(app) {
        return Err("请先打开操作面板再拖动".into());
    }
    if !pointer_allows_drag(&window, start, expanded)? {
        return Err("请按住灵动岛或面板标题再拖动".into());
    }
    let stop = Arc::new(AtomicBool::new(false));
    let generation = {
        let mut inner = state.inner.lock().map_err(|_| "无法开始拖动")?;
        if inner.drag.is_some() {
            return Err("灵动岛正在拖动".into());
        }
        inner.generation += 1;
        inner.drag = Some(DragSession {
            start,
            expanded,
            stop: stop.clone(),
        });
        inner.snapshot.dragging = true;
        inner.snapshot.error = None;
        inner.snapshot.revision += 1;
        inner.generation
    };
    if let Err(error) = emit(app).and_then(|()| {
        window
            .start_dragging()
            .map_err(|error| format!("无法拖动灵动岛：{error}"))
    }) {
        finish_drag(&window, generation, true);
        return Err(error);
    }
    if let Some(worker) = state.worker.lock().ok().and_then(|mut value| value.take()) {
        let _ = worker.join();
    }
    let worker_window = window.clone();
    let worker = thread::Builder::new()
        .name("assistant-user-drag".into())
        .spawn(move || {
            use windows::Win32::UI::Input::KeyboardAndMouse::{
                GetAsyncKeyState, VK_ESCAPE, VK_LBUTTON,
            };
            while !stop.load(Ordering::Acquire) {
                let escape = unsafe { GetAsyncKeyState(VK_ESCAPE.0 as i32) } < 0;
                let pressed = unsafe { GetAsyncKeyState(VK_LBUTTON.0 as i32) } < 0;
                if escape || !pressed {
                    // Let Windows finish the move loop before reading its final rect.
                    thread::sleep(Duration::from_millis(45));
                    if !stop.load(Ordering::Acquire) {
                        let finish_window = worker_window.clone();
                        let _ = worker_window.run_on_main_thread(move || {
                            finish_drag(&finish_window, generation, escape)
                        });
                    }
                    return;
                }
                thread::sleep(Duration::from_millis(20));
            }
        })
        .map_err(|error| format!("无法观察拖动完成：{error}"));
    match worker {
        Ok(worker) => {
            *state.worker.lock().map_err(|_| "无法记录拖动状态")? = Some(worker);
            Ok(())
        }
        Err(error) => {
            finish_drag(&window, generation, true);
            Err(error)
        }
    }
}

fn finish_drag(window: &WebviewWindow, generation: u64, cancelled: bool) {
    let app = window.app_handle();
    let state = app.state::<AssistantPlacement>();
    let (drag, pending) = {
        let Ok(mut inner) = state.inner.lock() else {
            return;
        };
        if inner.generation != generation {
            return;
        }
        let Some(drag) = inner.drag.take() else {
            return;
        };
        drag.stop.store(true, Ordering::Release);
        (drag, inner.pending.take())
    };
    let mut error = None;
    if !cancelled {
        match current_frame(window).and_then(|frame| {
            if frame.x == drag.start.x && frame.y == drag.start.y {
                return Ok(None);
            }
            let available = monitors(window)?;
            let monitor = nearest_monitor(frame, &available).ok_or("没有可用显示器")?;
            Ok(Some(placement_from_drag(frame, monitor)))
        }) {
            Ok(Some(saved)) => {
                if let Err(message) = state.save(&saved) {
                    error = Some(format!("{message}，本次位置仅临时保留。"));
                }
                if let Ok(mut inner) = state.inner.lock() {
                    inner.saved = Some(saved);
                }
            }
            Ok(None) => {}
            Err(message) => error = Some(message),
        }
    }
    let mode = pending
        .as_ref()
        .map(|value| value.0)
        .unwrap_or(if drag.expanded {
            IslandMode::Expanded
        } else {
            IslandMode::Compact
        });
    let result = apply_mode(window, mode);
    if let Err(message) = &result {
        error = Some(message.clone());
    }
    // Windows/WebView can temporarily activate even a NOACTIVATE surface
    // during its system move loop. A resting drag must return that focus just
    // like closing the manual controls, without changing a user's new window.
    if !crate::assistant_surface::is_interactive(app) {
        if let Err(message) = crate::assistant_surface::release_controls(app) {
            error = Some(message);
        }
    }
    if let Ok(mut inner) = state.inner.lock() {
        let edge = inner.saved.as_ref().and_then(|saved| saved.edge);
        inner.snapshot.edge = edge;
        inner.snapshot.mode = resting_mode(edge);
        inner.snapshot.dragging = false;
        inner.snapshot.error = error;
        inner.snapshot.revision += 1;
    }
    let _ = emit(app);
    if let Some((_, reply)) = pending {
        let _ = reply.send(result);
    }
}

fn current_frame(window: &WebviewWindow) -> Result<PhysicalWindowFrame, String> {
    use windows::Win32::{Foundation::RECT, UI::WindowsAndMessaging::GetWindowRect};
    let hwnd = window.hwnd().map_err(|_| "无法读取灵动岛窗口")?;
    let mut rect = RECT::default();
    unsafe { GetWindowRect(hwnd, &mut rect) }.map_err(|_| "无法读取灵动岛位置")?;
    Ok(PhysicalWindowFrame {
        x: rect.left,
        y: rect.top,
        width: (rect.right - rect.left).max(1) as u32,
        height: (rect.bottom - rect.top).max(1) as u32,
    })
}

fn pointer_allows_drag(
    window: &WebviewWindow,
    frame: PhysicalWindowFrame,
    expanded: bool,
) -> Result<bool, String> {
    use windows::Win32::{
        Foundation::POINT,
        UI::{
            Input::KeyboardAndMouse::{GetAsyncKeyState, VK_LBUTTON},
            WindowsAndMessaging::{GetAncestor, GetCursorPos, WindowFromPoint, GA_ROOT},
        },
    };
    if unsafe { GetAsyncKeyState(VK_LBUTTON.0 as i32) } >= 0 {
        return Ok(false);
    }
    let hwnd = window.hwnd().map_err(|_| "无法读取灵动岛窗口")?;
    let mut point = POINT::default();
    unsafe { GetCursorPos(&mut point) }.map_err(|_| "无法读取拖动位置")?;
    let hit = unsafe { WindowFromPoint(point) };
    if hit != hwnd && unsafe { GetAncestor(hit, GA_ROOT) } != hwnd {
        return Ok(false);
    }
    let scale = window.scale_factor().map_err(|_| "无法读取显示比例")?;
    let x = f64::from(point.x - frame.x) / scale;
    let y = f64::from(point.y - frame.y) / scale;
    let width = f64::from(frame.width) / scale;
    let height = f64::from(frame.height) / scale;
    Ok(x >= 0.0
        && y >= 0.0
        && x < width
        && y < height
        && (!expanded || (y <= 64.0 && x < width - 88.0)))
}

fn monitors(window: &WebviewWindow) -> Result<Vec<Monitor>, String> {
    let primary = window.primary_monitor().map_err(|_| "无法读取显示器")?;
    let mut available = window.available_monitors().map_err(|_| "无法读取显示器")?;
    if let Some(primary) = primary {
        available.sort_by_key(|monitor| monitor.position() != primary.position());
    }
    Ok(available
        .into_iter()
        .filter_map(|monitor| {
            let area = monitor.work_area();
            if area.size.width == 0 || area.size.height == 0 {
                return None;
            }
            Some(Monitor {
                id: monitor.name().cloned().unwrap_or_else(|| {
                    format!("display-{}-{}", monitor.position().x, monitor.position().y)
                }),
                area: WorkAreaGeometry {
                    origin_x: area.position.x,
                    origin_y: area.position.y,
                    width: area.size.width,
                    height: area.size.height,
                    scale_factor: monitor.scale_factor(),
                },
            })
        })
        .collect())
}

fn resolve_monitor<'a>(
    saved: Option<&SavedPlacement>,
    monitors: &'a [Monitor],
) -> Result<&'a Monitor, String> {
    saved
        .and_then(|saved| monitors.iter().find(|monitor| monitor.id == saved.monitor))
        .or_else(|| monitors.first())
        .ok_or_else(|| "没有可用显示器".into())
}

fn nearest_monitor(frame: PhysicalWindowFrame, monitors: &[Monitor]) -> Option<&Monitor> {
    let x = f64::from(frame.x) + f64::from(frame.width) / 2.0;
    let y = f64::from(frame.y) + f64::from(frame.height) / 2.0;
    monitors.iter().min_by(|left, right| {
        let distance = |monitor: &Monitor| {
            let area = monitor.area;
            let dx = x - x.clamp(
                f64::from(area.origin_x),
                f64::from(area.origin_x) + f64::from(area.width),
            );
            let dy = y - y.clamp(
                f64::from(area.origin_y),
                f64::from(area.origin_y) + f64::from(area.height),
            );
            dx * dx + dy * dy
        };
        distance(left).total_cmp(&distance(right))
    })
}

fn placement_from_drag(frame: PhysicalWindowFrame, monitor: &Monitor) -> SavedPlacement {
    let area = monitor.area;
    let distances = [
        (
            Edge::Left,
            (f64::from(frame.x) - f64::from(area.origin_x)).max(0.0),
        ),
        (
            Edge::Right,
            (f64::from(area.origin_x) + f64::from(area.width)
                - f64::from(frame.x)
                - f64::from(frame.width))
            .max(0.0),
        ),
        (
            Edge::Top,
            (f64::from(frame.y) - f64::from(area.origin_y)).max(0.0),
        ),
        (
            Edge::Bottom,
            (f64::from(area.origin_y) + f64::from(area.height)
                - f64::from(frame.y)
                - f64::from(frame.height))
            .max(0.0),
        ),
    ];
    let edge = distances
        .into_iter()
        .min_by(|left, right| left.1.total_cmp(&right.1))
        .filter(|(_, distance)| *distance <= SNAP_DISTANCE * area.scale_factor)
        .map(|(edge, _)| edge);
    SavedPlacement {
        schema_version: 1,
        monitor: monitor.id.clone(),
        center_x: (f64::from(frame.x) + f64::from(frame.width) / 2.0 - f64::from(area.origin_x))
            / area.scale_factor,
        center_y: (f64::from(frame.y) + f64::from(frame.height) / 2.0 - f64::from(area.origin_y))
            / area.scale_factor,
        edge,
    }
}

fn anchored_frame(
    saved: Option<&SavedPlacement>,
    area: &WorkAreaGeometry,
    expanded: bool,
) -> PhysicalWindowFrame {
    if saved.is_none() {
        return crate::window::default_assistant_frame(*area, expanded);
    }
    let scale = area.scale_factor;
    let edge = saved.and_then(|value| value.edge);
    let (rest_width, rest_height) = if edge.is_some() {
        (ORB_DIAMETER, ORB_DIAMETER)
    } else {
        (224.0, 28.0)
    };
    let (logical_width, logical_height) = if expanded {
        (480.0, 330.0)
    } else {
        (rest_width, rest_height)
    };
    let width = ((logical_width * scale).round() as u32)
        .max(1)
        .min(area.width);
    let height = ((logical_height * scale).round() as u32)
        .max(1)
        .min(area.height);
    let gap = (EDGE_GAP * scale).round();
    let center_x = saved
        .map(|saved| saved.center_x * scale)
        .unwrap_or(f64::from(area.width) - (12.0 + rest_width / 2.0) * scale);
    let center_y = saved
        .map(|saved| saved.center_y * scale)
        .unwrap_or(f64::from(area.height) / 2.0);
    let rest_center_x = match edge {
        Some(Edge::Left) => gap + rest_width * scale / 2.0,
        Some(Edge::Right) => f64::from(area.width) - gap - rest_width * scale / 2.0,
        _ => center_x,
    };
    let rest_center_y = match edge {
        Some(Edge::Top) => gap + rest_height * scale / 2.0,
        Some(Edge::Bottom) => f64::from(area.height) - gap - rest_height * scale / 2.0,
        _ => center_y,
    };
    let max_x = f64::from(area.width.saturating_sub(width));
    let max_y = f64::from(area.height.saturating_sub(height));
    let x = (rest_center_x - f64::from(width) / 2.0)
        .clamp(gap.min(max_x), (max_x - gap).max(gap.min(max_x)));
    let y = (rest_center_y - f64::from(height) / 2.0)
        .clamp(gap.min(max_y), (max_y - gap).max(gap.min(max_y)));
    let mut frame = PhysicalWindowFrame {
        x: area.origin_x.saturating_add(x.round() as i32),
        y: area.origin_y.saturating_add(y.round() as i32),
        width,
        height,
    };
    // Clip the resting orb with the native viewport rather than placing its
    // hidden two thirds outside the work area (which could cover an adjacent
    // monitor or taskbar). Keep its old along-edge center and expanded anchor.
    if !expanded {
        let peek = ((ORB_DIAMETER * ORB_VISIBLE_FRACTION * scale).round() as u32).max(1);
        match edge {
            Some(Edge::Left | Edge::Right) => {
                frame.width = peek.min(area.width);
                frame.x = if edge == Some(Edge::Left) {
                    area.origin_x
                } else {
                    area.origin_x
                        .saturating_add(area.width.saturating_sub(frame.width) as i32)
                };
            }
            Some(Edge::Top | Edge::Bottom) => {
                frame.height = peek.min(area.height);
                frame.y = if edge == Some(Edge::Top) {
                    area.origin_y
                } else {
                    area.origin_y
                        .saturating_add(area.height.saturating_sub(frame.height) as i32)
                };
            }
            None => {}
        }
    }
    frame
}

#[cfg(test)]
mod tests {
    use super::*;

    fn monitor(scale: f64) -> Monitor {
        Monitor {
            id: "test-secondary".into(),
            area: WorkAreaGeometry {
                origin_x: -1920,
                origin_y: 100,
                width: 1920,
                height: 1080,
                scale_factor: scale,
            },
        }
    }
    fn saved(edge: Option<Edge>) -> SavedPlacement {
        SavedPlacement {
            schema_version: 1,
            monitor: "test-secondary".into(),
            center_x: 500.0,
            center_y: 350.0,
            edge,
        }
    }
    fn temp_root() -> PathBuf {
        let suffix = std::time::SystemTime::now()
            .duration_since(std::time::UNIX_EPOCH)
            .unwrap()
            .as_nanos();
        let root =
            std::env::temp_dir().join(format!("yuanshu-placement-{}-{suffix}", std::process::id()));
        fs::create_dir_all(&root).unwrap();
        root
    }

    #[test]
    fn placement_schema_rejects_extra_fields_future_versions_and_out_of_range_coordinates() {
        let valid = serde_json::to_value(saved(Some(Edge::Right))).unwrap();
        assert_eq!(
            decode(&serde_json::to_vec(&valid).unwrap()).unwrap(),
            saved(Some(Edge::Right))
        );
        for (key, value) in [
            ("schemaVersion", serde_json::json!(2)),
            ("centerX", serde_json::json!(1e99)),
            ("edge", serde_json::json!("diagonal")),
            ("unknown", serde_json::json!(true)),
        ] {
            let mut invalid = valid.clone();
            invalid[key] = value;
            assert!(decode(&serde_json::to_vec(&invalid).unwrap()).is_err());
        }
        assert!(decode(&vec![b' '; 4097]).is_err());
    }

    #[test]
    fn four_edges_snap_at_all_supported_scales_and_keep_one_third_peek_inside_work_area() {
        for scale in [1.0, 1.25, 1.5] {
            let monitor = monitor(scale);
            let area = monitor.area;
            let width = (224.0 * scale).round() as u32;
            let height = (28.0 * scale).round() as u32;
            for edge in [Edge::Left, Edge::Right, Edge::Top, Edge::Bottom] {
                let mut dragged = PhysicalWindowFrame {
                    x: area.origin_x + 600,
                    y: area.origin_y + 400,
                    width,
                    height,
                };
                match edge {
                    Edge::Left => dragged.x = area.origin_x + (24.0 * scale).round() as i32,
                    Edge::Right => {
                        dragged.x = area.origin_x + area.width as i32
                            - width as i32
                            - (24.0 * scale).round() as i32
                    }
                    Edge::Top => dragged.y = area.origin_y + (24.0 * scale).round() as i32,
                    Edge::Bottom => {
                        dragged.y = area.origin_y + area.height as i32
                            - height as i32
                            - (24.0 * scale).round() as i32
                    }
                }
                let placement = placement_from_drag(dragged, &monitor);
                assert_eq!(placement.edge, Some(edge));
                let sphere = anchored_frame(Some(&placement), &area, false);
                let diameter = (44.0 * scale).round() as u32;
                let peek = (44.0 / 3.0 * scale).round() as u32;
                assert_eq!(
                    (sphere.width, sphere.height),
                    if matches!(edge, Edge::Left | Edge::Right) {
                        (peek, diameter)
                    } else {
                        (diameter, peek)
                    }
                );
                assert!(sphere.x >= area.origin_x && sphere.y >= area.origin_y);
                assert!(sphere.x + sphere.width as i32 <= area.origin_x + area.width as i32);
                assert!(sphere.y + sphere.height as i32 <= area.origin_y + area.height as i32);
                match edge {
                    Edge::Left => assert_eq!(sphere.x, area.origin_x),
                    Edge::Right => assert_eq!(
                        sphere.x + sphere.width as i32,
                        area.origin_x + area.width as i32
                    ),
                    Edge::Top => assert_eq!(sphere.y, area.origin_y),
                    Edge::Bottom => assert_eq!(
                        sphere.y + sphere.height as i32,
                        area.origin_y + area.height as i32
                    ),
                }
            }
        }
    }

    #[test]
    fn edge_peek_preserves_saved_along_edge_centers_and_expands_fully_inside_each_monitor() {
        for scale in [1.0, 1.25, 1.5] {
            for origin in [(0, 0), (-1920, 100), (1200, -800)] {
                let area = WorkAreaGeometry {
                    origin_x: origin.0,
                    origin_y: origin.1,
                    ..monitor(scale).area
                };
                for edge in [Edge::Left, Edge::Right, Edge::Top, Edge::Bottom] {
                    let value = saved(Some(edge));
                    let before = value.clone();
                    let peek = anchored_frame(Some(&value), &area, false);
                    let center = if matches!(edge, Edge::Left | Edge::Right) {
                        f64::from(peek.y - area.origin_y) + f64::from(peek.height) / 2.0
                    } else {
                        f64::from(peek.x - area.origin_x) + f64::from(peek.width) / 2.0
                    };
                    let expected = if matches!(edge, Edge::Left | Edge::Right) {
                        value.center_y * scale
                    } else {
                        value.center_x * scale
                    };
                    assert!((center - expected).abs() <= 1.0);
                    let expanded = anchored_frame(Some(&value), &area, true);
                    assert_eq!(
                        (expanded.width, expanded.height),
                        (
                            (480.0 * scale).round() as u32,
                            (330.0 * scale).round() as u32
                        )
                    );
                    assert!(expanded.x >= area.origin_x && expanded.y >= area.origin_y);
                    assert!(
                        expanded.x + expanded.width as i32 <= area.origin_x + area.width as i32
                    );
                    assert!(
                        expanded.y + expanded.height as i32 <= area.origin_y + area.height as i32
                    );
                    assert_eq!(anchored_frame(Some(&value), &area, false), peek);
                    assert_eq!(value, before);
                }
            }
        }
    }

    #[test]
    fn dragging_a_sphere_away_from_edges_restores_capsule_and_uses_24_logical_pixel_threshold() {
        let monitor = monitor(1.25);
        let inside = PhysicalWindowFrame {
            x: -1200,
            y: 480,
            width: 18,
            height: 55,
        };
        let placement = placement_from_drag(inside, &monitor);
        assert_eq!(placement.edge, None);
        let capsule = anchored_frame(Some(&placement), &monitor.area, false);
        assert_eq!((capsule.width, capsule.height), (280, 35));
        assert!(
            (capsule.x + capsule.width as i32 / 2 - inside.x - inside.width as i32 / 2).abs() <= 1
        );
        let outside_threshold = PhysicalWindowFrame {
            x: monitor.area.origin_x + 31,
            ..inside
        };
        assert_eq!(placement_from_drag(outside_threshold, &monitor).edge, None);
    }

    #[test]
    fn dpi_restore_preserves_logical_center_and_missing_monitor_clamps_without_changing_saved_data()
    {
        let value = saved(None);
        for scale in [1.0, 1.25, 1.5] {
            let area = monitor(scale).area;
            let frame = anchored_frame(Some(&value), &area, false);
            assert!(
                (f64::from(frame.x - area.origin_x) + f64::from(frame.width) / 2.0
                    - value.center_x * scale)
                    .abs()
                    <= 1.0
            );
        }
        let available = [Monitor {
            id: "different-display".into(),
            area: WorkAreaGeometry {
                origin_x: 1200,
                origin_y: -800,
                width: 800,
                height: 600,
                scale_factor: 1.5,
            },
        }];
        let selected = resolve_monitor(Some(&value), &available).unwrap();
        assert_eq!(selected.id, "different-display");
        for expanded in [false, true] {
            let frame = anchored_frame(Some(&value), &selected.area, expanded);
            assert!(frame.x >= 1200 && frame.y >= -800);
            assert!(frame.x + frame.width as i32 <= 2000 && frame.y + frame.height as i32 <= -200);
        }
        assert_eq!(value, saved(None));
    }

    #[test]
    fn monitor_selection_follows_the_user_drag_across_negative_coordinate_displays() {
        let available = [
            monitor(1.25),
            Monitor {
                id: "primary".into(),
                area: WorkAreaGeometry {
                    origin_x: 0,
                    origin_y: 0,
                    width: 1920,
                    height: 1040,
                    scale_factor: 1.0,
                },
            },
        ];
        let negative = PhysicalWindowFrame {
            x: -900,
            y: 400,
            width: 224,
            height: 28,
        };
        let positive = PhysicalWindowFrame { x: 400, ..negative };
        assert_eq!(
            nearest_monitor(negative, &available).unwrap().id,
            "test-secondary"
        );
        assert_eq!(nearest_monitor(positive, &available).unwrap().id, "primary");
    }

    #[test]
    fn loading_or_automatic_expansion_never_creates_or_changes_saved_placement() {
        let root = temp_root();
        let path = root.join("placement.json");
        let store = AssistantPlacement::load(path.clone());
        assert!(!path.exists());
        let value = saved(Some(Edge::Left));
        store.save(&value).unwrap();
        let before = fs::read(&path).unwrap();
        let reloaded = AssistantPlacement::load(path.clone());
        assert_eq!(reloaded.inner.lock().unwrap().saved, Some(value.clone()));
        for expanded in [false, true, false] {
            let _ = anchored_frame(Some(&value), &monitor(1.5).area, expanded);
        }
        assert_eq!(fs::read(&path).unwrap(), before);
        fs::remove_file(path).unwrap();
        fs::remove_dir(root).unwrap();
    }

    #[test]
    fn corrupt_saved_placement_is_preserved_and_failed_save_keeps_original_file() {
        let root = temp_root();
        let path = root.join("placement.json");
        fs::write(&path, b"broken").unwrap();
        let store = AssistantPlacement::load(path.clone());
        assert!(store.inner.lock().unwrap().snapshot.error.is_some());
        assert_eq!(fs::read(&path).unwrap(), b"broken");
        fs::create_dir(path.with_extension("pending")).unwrap();
        assert!(store.save(&saved(None)).is_err());
        assert_eq!(fs::read(&path).unwrap(), b"broken");
        fs::remove_dir(path.with_extension("pending")).unwrap();
        for entry in fs::read_dir(&root).unwrap() {
            fs::remove_file(entry.unwrap().path()).unwrap();
        }
        fs::remove_dir(root).unwrap();
    }
}
