use std::{
    sync::{
        atomic::{AtomicBool, Ordering},
        mpsc, Mutex,
    },
    thread::{self, JoinHandle},
    time::Duration,
};

use tauri::{AppHandle, Emitter, Manager};

use crate::window;

pub const FULLSCREEN_PRESENCE_EVENT: &str = "fullscreen-presence-changed";
const DETECTION_INTERVAL: Duration = Duration::from_millis(750);

pub struct FullscreenPresenceHandle {
    stop: mpsc::Sender<()>,
    worker: Mutex<Option<JoinHandle<()>>>,
}

impl Drop for FullscreenPresenceHandle {
    fn drop(&mut self) {
        let _ = self.stop.send(());
        if let Some(worker) = self.worker.get_mut().ok().and_then(Option::take) {
            let _ = worker.join();
        }
    }
}

pub struct WindowVisibilityState {
    user_visible: AtomicBool,
    fullscreen: AtomicBool,
}

impl Default for WindowVisibilityState {
    fn default() -> Self {
        Self {
            user_visible: AtomicBool::new(true),
            fullscreen: AtomicBool::new(false),
        }
    }
}

#[derive(Clone, Copy, Debug, PartialEq, Eq)]
struct ScreenRect {
    left: i32,
    top: i32,
    right: i32,
    bottom: i32,
}

#[derive(Clone, Copy, Debug, PartialEq, Eq)]
struct ForegroundSnapshot {
    visible: bool,
    minimized: bool,
    own_window: bool,
    excluded_shell_window: bool,
    maximized: bool,
    captioned_resizable: bool,
    window: ScreenRect,
    monitor: ScreenRect,
    work_area: ScreenRect,
}

fn is_fullscreen_foreground(snapshot: ForegroundSnapshot) -> bool {
    const EDGE_TOLERANCE_PX: i32 = 2;

    if !snapshot.visible
        || snapshot.minimized
        || snapshot.own_window
        || snapshot.excluded_shell_window
    {
        return false;
    }

    let rect_matches = |left: ScreenRect, right: ScreenRect| {
        [
            (left.left, right.left),
            (left.top, right.top),
            (left.right, right.right),
            (left.bottom, right.bottom),
        ]
        .into_iter()
        .all(|(left_edge, right_edge)| {
            i64::from(left_edge).abs_diff(i64::from(right_edge)) <= EDGE_TOLERANCE_PX as u64
        })
    };
    let ordinary_maximized = snapshot.maximized
        && snapshot.captioned_resizable
        && (rect_matches(snapshot.window, snapshot.work_area)
            || snapshot.work_area == snapshot.monitor);
    !ordinary_maximized && rect_matches(snapshot.window, snapshot.monitor)
}

fn should_show_window(user_visible: bool, fullscreen: bool) -> bool {
    user_visible && !fullscreen
}

#[tauri::command]
pub fn get_fullscreen_presence(state: tauri::State<'_, WindowVisibilityState>) -> bool {
    state.fullscreen.load(Ordering::Acquire)
}

pub fn request_show(app: &AppHandle, focus: bool) -> Result<(), String> {
    let state = app.state::<WindowVisibilityState>();
    state.user_visible.store(true, Ordering::Release);
    if state.fullscreen.load(Ordering::Acquire) {
        return Ok(());
    }
    if focus {
        window::show_main_window(app)
    } else {
        window::restore_main_window_without_focus(app)
    }
}

pub fn request_hide(app: &AppHandle) -> Result<(), String> {
    app.state::<WindowVisibilityState>()
        .user_visible
        .store(false, Ordering::Release);
    let window = app
        .get_webview_window("main")
        .ok_or_else(|| "找不到主灵动岛窗口".to_string())?;
    window
        .hide()
        .map_err(|error| format!("无法隐藏灵动岛：{error}"))
}

pub fn show_initial_window(app: &AppHandle) -> Result<(), String> {
    let state = app.state::<WindowVisibilityState>();
    if should_show_window(
        state.user_visible.load(Ordering::Acquire),
        state.fullscreen.load(Ordering::Acquire),
    ) {
        window::restore_main_window_without_focus(app)?;
    }
    Ok(())
}

pub fn start(app: AppHandle) -> Result<FullscreenPresenceHandle, String> {
    let own_hwnd = app
        .get_webview_window("main")
        .ok_or_else(|| "找不到主灵动岛窗口".to_string())?
        .hwnd()
        .map_err(|error| format!("无法读取灵动岛原生窗口句柄：{error}"))?;
    let own_hwnd_value = own_hwnd.0 as usize;
    let initial = fullscreen_hides_assistant(
        crate::assistant::is_integrated(&app),
        crate::settings::preferences(&app).hide_in_fullscreen,
        detect_fullscreen(own_hwnd_value),
    );
    app.state::<WindowVisibilityState>()
        .fullscreen
        .store(initial, Ordering::Release);

    let (stop, stop_receiver) = mpsc::channel();
    let worker_app = app.clone();
    let worker = thread::Builder::new()
        .name("fullscreen-presence".into())
        .spawn(move || run_worker(worker_app, own_hwnd_value, initial, stop_receiver))
        .map_err(|error| format!("无法启动 Windows 全屏 Presence 观察器：{error}"))?;

    Ok(FullscreenPresenceHandle {
        stop,
        worker: Mutex::new(Some(worker)),
    })
}

fn run_worker(app: AppHandle, own_hwnd: usize, mut current: bool, stop: mpsc::Receiver<()>) {
    loop {
        match stop.recv_timeout(DETECTION_INTERVAL) {
            Ok(()) | Err(mpsc::RecvTimeoutError::Disconnected) => return,
            Err(mpsc::RecvTimeoutError::Timeout) => {}
        }

        if crate::assistant::is_integrated(&app) {
            let placement_app = app.clone();
            let _ = app.run_on_main_thread(move || {
                crate::assistant_placement::refresh_display_layout(&placement_app);
            });
        }

        let next = fullscreen_hides_assistant(
            crate::assistant::is_integrated(&app),
            crate::settings::preferences(&app).hide_in_fullscreen,
            detect_fullscreen(own_hwnd),
        );
        if next == current {
            continue;
        }
        current = next;
        let state = app.state::<WindowVisibilityState>();
        state.fullscreen.store(next, Ordering::Release);
        apply_visibility(&app, &state);
        let _ = app.emit_to("main", FULLSCREEN_PRESENCE_EVENT, next);
    }
}

fn fullscreen_hides_assistant(
    integrated: bool,
    hide_in_fullscreen: bool,
    fullscreen: bool,
) -> bool {
    !integrated && hide_in_fullscreen && fullscreen
}

fn apply_visibility(app: &AppHandle, state: &WindowVisibilityState) {
    let show = should_show_window(
        state.user_visible.load(Ordering::Acquire),
        state.fullscreen.load(Ordering::Acquire),
    );
    let Some(window) = app.get_webview_window("main") else {
        return;
    };
    if show {
        let _ = window::restore_main_window_without_focus(app);
    } else {
        let _ = window.hide();
    }
}

#[cfg(windows)]
fn detect_fullscreen(own_hwnd: usize) -> bool {
    collect_foreground_snapshot(own_hwnd).is_some_and(is_fullscreen_foreground)
}

#[cfg(windows)]
fn collect_foreground_snapshot(own_hwnd: usize) -> Option<ForegroundSnapshot> {
    use windows::Win32::{
        Foundation::RECT,
        Graphics::{
            Dwm::{DwmGetWindowAttribute, DWMWA_EXTENDED_FRAME_BOUNDS},
            Gdi::{GetMonitorInfoW, MonitorFromWindow, MONITORINFO, MONITOR_DEFAULTTONEAREST},
        },
        UI::WindowsAndMessaging::{
            GetClassNameW, GetForegroundWindow, GetWindowLongPtrW, GetWindowRect, IsIconic,
            IsWindowVisible, IsZoomed, GWL_STYLE, WS_CAPTION, WS_THICKFRAME,
        },
    };

    let hwnd = unsafe { GetForegroundWindow() };
    if hwnd.0.is_null() {
        return None;
    }

    let mut class_buffer = [0_u16; 256];
    let class_length = unsafe { GetClassNameW(hwnd, &mut class_buffer) }.max(0) as usize;
    let class_name = String::from_utf16_lossy(&class_buffer[..class_length]);
    let excluded_shell_window = matches!(
        class_name.as_str(),
        "Progman" | "WorkerW" | "Shell_TrayWnd" | "Shell_SecondaryTrayWnd"
    );

    let monitor = unsafe { MonitorFromWindow(hwnd, MONITOR_DEFAULTTONEAREST) };
    if monitor.is_invalid() {
        return None;
    }
    let mut monitor_info = MONITORINFO {
        cbSize: std::mem::size_of::<MONITORINFO>() as u32,
        ..Default::default()
    };
    if !unsafe { GetMonitorInfoW(monitor, &mut monitor_info) }.as_bool() {
        return None;
    }

    let mut frame = RECT::default();
    if unsafe {
        DwmGetWindowAttribute(
            hwnd,
            DWMWA_EXTENDED_FRAME_BOUNDS,
            (&mut frame as *mut RECT).cast(),
            std::mem::size_of::<RECT>() as u32,
        )
    }
    .is_err()
        && unsafe { GetWindowRect(hwnd, &mut frame) }.is_err()
    {
        return None;
    }

    let style = unsafe { GetWindowLongPtrW(hwnd, GWL_STYLE) } as u32;
    let captioned_resizable = style & WS_CAPTION.0 != 0 && style & WS_THICKFRAME.0 != 0;

    Some(ForegroundSnapshot {
        visible: unsafe { IsWindowVisible(hwnd) }.as_bool(),
        minimized: unsafe { IsIconic(hwnd) }.as_bool(),
        own_window: hwnd.0 as usize == own_hwnd,
        excluded_shell_window,
        maximized: unsafe { IsZoomed(hwnd) }.as_bool(),
        captioned_resizable,
        window: frame.into(),
        monitor: monitor_info.rcMonitor.into(),
        work_area: monitor_info.rcWork.into(),
    })
}

#[cfg(windows)]
impl From<windows::Win32::Foundation::RECT> for ScreenRect {
    fn from(value: windows::Win32::Foundation::RECT) -> Self {
        Self {
            left: value.left,
            top: value.top,
            right: value.right,
            bottom: value.bottom,
        }
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    fn fullscreen_snapshot() -> ForegroundSnapshot {
        ForegroundSnapshot {
            visible: true,
            minimized: false,
            own_window: false,
            excluded_shell_window: false,
            maximized: false,
            captioned_resizable: false,
            window: ScreenRect {
                left: 0,
                top: 0,
                right: 1920,
                bottom: 1080,
            },
            monitor: ScreenRect {
                left: 0,
                top: 0,
                right: 1920,
                bottom: 1080,
            },
            work_area: ScreenRect {
                left: 0,
                top: 0,
                right: 1920,
                bottom: 1040,
            },
        }
    }

    #[test]
    fn accepts_foreground_borderless_window_covering_its_monitor() {
        assert!(is_fullscreen_foreground(fullscreen_snapshot()));
    }

    #[test]
    fn rejects_ordinary_maximized_captioned_window() {
        let mut snapshot = fullscreen_snapshot();
        snapshot.maximized = true;
        snapshot.captioned_resizable = true;
        snapshot.window.bottom = snapshot.work_area.bottom;
        assert!(!is_fullscreen_foreground(snapshot));
    }

    #[test]
    fn tolerates_two_pixels_of_dwm_extended_frame_rounding() {
        let mut snapshot = fullscreen_snapshot();
        snapshot.window = ScreenRect {
            left: -2,
            top: 1,
            right: 1922,
            bottom: 1080,
        };
        assert!(is_fullscreen_foreground(snapshot));
    }

    #[test]
    fn rejects_non_covering_or_ineligible_windows() {
        for mutate in [
            |value: &mut ForegroundSnapshot| value.visible = false,
            |value: &mut ForegroundSnapshot| value.minimized = true,
            |value: &mut ForegroundSnapshot| value.own_window = true,
            |value: &mut ForegroundSnapshot| value.excluded_shell_window = true,
            |value: &mut ForegroundSnapshot| value.window.right = 1600,
        ] {
            let mut snapshot = fullscreen_snapshot();
            mutate(&mut snapshot);
            assert!(!is_fullscreen_foreground(snapshot));
        }
    }

    #[test]
    fn fullscreen_suppression_never_overwrites_manual_visibility_intent() {
        assert!(should_show_window(true, false));
        assert!(!should_show_window(true, true));
        assert!(!should_show_window(false, true));
        assert!(!should_show_window(false, false));
    }

    #[test]
    fn integrated_microphone_surface_is_never_hidden_by_fullscreen_policy() {
        assert!(!fullscreen_hides_assistant(true, true, true));
        assert!(!fullscreen_hides_assistant(true, false, true));
        assert!(fullscreen_hides_assistant(false, true, true));
        assert!(!fullscreen_hides_assistant(false, false, true));
    }
}
