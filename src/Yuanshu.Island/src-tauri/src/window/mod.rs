mod animation;
mod geometry;
mod native_window;

pub use animation::AnimationController;
pub(crate) use geometry::{IslandMode, PhysicalWindowFrame, WorkAreaGeometry};
pub(crate) use native_window::ForegroundTarget;

use tauri::{App, AppHandle, Emitter, Manager, State, WebviewWindow};

use self::{
    geometry::{
        physical_assistant_frame_for, physical_frame_for, LogicalWindowFrame, MonitorGeometry,
        COMPACT_FRAME,
    },
    native_window::apply_frame,
};

const MODE_RESET_EVENT: &str = "island-mode-reset";
const COMPACT_MODE_NAME: &str = "compact";

pub struct StartupState {
    pub(crate) low_presence: bool,
}

impl StartupState {
    pub const fn new(low_presence: bool) -> Self {
        Self { low_presence }
    }
}

#[tauri::command]
pub fn get_startup_low_presence(state: State<'_, StartupState>) -> bool {
    state.low_presence
}

#[tauri::command]
pub async fn set_island_mode(
    mode: String,
    window: WebviewWindow,
    controller: State<'_, AnimationController>,
) -> Result<(), String> {
    let mode = IslandMode::try_from(mode.as_str())?;
    let controller = controller.inner().clone();
    let (sender, receiver) = std::sync::mpsc::channel();
    let scheduled = window.clone();
    window
        .run_on_main_thread(move || {
            if crate::assistant::is_integrated(scheduled.app_handle()) {
                crate::assistant_placement::request_mode(&scheduled, mode, sender);
            } else {
                let _ = sender.send(animation::set_island_mode(mode, scheduled, controller));
            }
        })
        .map_err(|error| format!("无法调整灵动岛：{error}"))?;
    tauri::async_runtime::spawn_blocking(move || {
        receiver
            .recv()
            .map_err(|_| "灵动岛显示请求已中断".to_string())
    })
    .await
    .map_err(|_| "灵动岛显示请求已中断".to_string())??
}

pub fn initialize_main_window(app: &App, low_presence: bool) -> Result<(), String> {
    let window = app
        .get_webview_window("main")
        .ok_or_else(|| "找不到主灵动岛窗口".to_string())?;

    // The config already contains these flags. Reasserting them here keeps the
    // runtime contract explicit if the config is edited later.
    window
        .set_decorations(false)
        .map_err(|error| format!("无法移除系统标题栏：{error}"))?;
    window
        .set_resizable(false)
        .map_err(|error| format!("无法锁定窗口尺寸：{error}"))?;
    window
        .set_maximizable(false)
        .map_err(|error| format!("无法禁用最大化：{error}"))?;
    window
        .set_minimizable(false)
        .map_err(|error| format!("无法禁用最小化：{error}"))?;
    window
        .set_skip_taskbar(true)
        .map_err(|error| format!("无法从任务栏隐藏灵动岛：{error}"))?;
    window
        .set_always_on_top(true)
        .map_err(|error| format!("无法将灵动岛置顶：{error}"))?;

    // Shadow support differs across Windows versions; the config requests no
    // shadow, while a runtime failure here must not prevent the app from opening.
    let _ = window.set_shadow(false);

    let initial_frame = if crate::assistant::is_integrated(app.handle()) {
        native_window::install_assistant_focus_observer(&window)?;
        set_assistant_input(&window, false)?;
        geometry::ASSISTANT_COMPACT_FRAME
    } else if low_presence {
        geometry::TUCKED_FRAME
    } else {
        COMPACT_FRAME
    };
    if crate::assistant::is_integrated(app.handle()) {
        crate::assistant_placement::apply_mode(&window, IslandMode::Compact)?;
    } else {
        place_at_logical_frame(&window, initial_frame)?;
    }
    Ok(())
}

pub fn show_main_window(app: &AppHandle) -> Result<(), String> {
    if crate::assistant::is_integrated(app) {
        return crate::assistant_surface::show_controls(app);
    }
    let window = app
        .get_webview_window("main")
        .ok_or_else(|| "找不到主灵动岛窗口".to_string())?;

    let controller = app.state::<AnimationController>();
    animation::reset_to_compact(&window, controller.inner())?;
    window
        .set_always_on_top(true)
        .map_err(|error| format!("无法将灵动岛置顶：{error}"))?;
    window
        .emit(MODE_RESET_EVENT, COMPACT_MODE_NAME)
        .map_err(|error| format!("无法通知前端复位灵动岛模式：{error}"))?;
    window
        .show()
        .map_err(|error| format!("无法显示灵动岛：{error}"))?;
    window
        .set_focus()
        .map_err(|error| format!("无法聚焦灵动岛：{error}"))?;
    Ok(())
}

/// Called on the UI thread only; native style verification precedes publishing
/// any input-state acknowledgement to the frontend.
pub(crate) fn set_assistant_input(window: &WebviewWindow, interactive: bool) -> Result<(), String> {
    let hit = interactive || crate::assistant_placement::is_resting(window.app_handle());
    native_window::set_assistant_input(window, hit, interactive)
}

pub(crate) fn apply_assistant_frame(
    window: &WebviewWindow,
    frame: PhysicalWindowFrame,
) -> Result<(), String> {
    apply_frame(window, frame)
}

pub(crate) fn default_assistant_frame(
    area: WorkAreaGeometry,
    expanded: bool,
) -> PhysicalWindowFrame {
    physical_assistant_frame_for(
        if expanded {
            geometry::ASSISTANT_EXPANDED_FRAME
        } else {
            geometry::ASSISTANT_COMPACT_FRAME
        },
        area,
    )
}

pub(crate) fn expand_assistant(window: &WebviewWindow) -> Result<(), String> {
    let controller = window.app_handle().state::<AnimationController>();
    animation::set_island_mode(
        IslandMode::Expanded,
        window.clone(),
        controller.inner().clone(),
    )
}

pub(crate) fn capture_assistant_foreground(window: &WebviewWindow) -> Option<ForegroundTarget> {
    native_window::capture_foreground(window)
}

pub(crate) fn assistant_owns_foreground(app: &AppHandle) -> bool {
    app.get_webview_window("main")
        .is_some_and(|window| native_window::owns_foreground(&window))
}

pub(crate) fn restore_assistant_foreground(
    window: &WebviewWindow,
    target: ForegroundTarget,
) -> bool {
    native_window::restore_foreground(window, target)
}

pub fn restore_main_window_without_focus(app: &AppHandle) -> Result<(), String> {
    let window = app
        .get_webview_window("main")
        .ok_or_else(|| "找不到主灵动岛窗口".to_string())?;
    window
        .set_always_on_top(true)
        .map_err(|error| format!("无法将灵动岛置顶：{error}"))?;
    window
        .show()
        .map_err(|error| format!("无法显示灵动岛：{error}"))
}

fn place_at_logical_frame(
    window: &WebviewWindow,
    logical_frame: LogicalWindowFrame,
) -> Result<(), String> {
    let monitor = monitor_geometry(window)?;
    apply_frame(window, physical_frame_for(logical_frame, monitor))
}

fn monitor_geometry(window: &WebviewWindow) -> Result<MonitorGeometry, String> {
    let monitor = window
        .primary_monitor()
        .map_err(|error| format!("无法读取主显示器：{error}"))?
        .ok_or_else(|| "系统没有返回可用的主显示器".to_string())?;
    let position = monitor.position();
    let size = monitor.size();

    Ok(MonitorGeometry::new(
        position.x,
        position.y,
        size.width,
        monitor.scale_factor(),
    ))
}
