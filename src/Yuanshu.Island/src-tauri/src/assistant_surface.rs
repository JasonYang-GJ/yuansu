//! Local presentation/input state only. It cannot start listening or execute a
//! Host command; those controls continue through the authenticated bridge.
use std::{
    sync::{mpsc, Mutex},
    thread::{self, JoinHandle},
    time::Duration,
};

use serde::Serialize;
use tauri::{AppHandle, Emitter, Manager, WebviewWindow};

const SURFACE_EVENT: &str = "assistant-surface-changed";
const HOTKEY_ID: i32 = 0x5953;

#[derive(Clone, Copy, Debug, Default, PartialEq, Eq, Serialize)]
#[serde(rename_all = "camelCase")]
pub struct SurfaceSnapshot {
    interactive: bool,
    shortcut_available: bool,
    revision: u64,
}

impl SurfaceSnapshot {
    fn change(&mut self, interactive: bool, shortcut_available: bool) -> bool {
        if self.interactive == interactive && self.shortcut_available == shortcut_available {
            return false;
        }
        self.interactive = interactive;
        self.shortcut_available = shortcut_available;
        self.revision += 1;
        true
    }
}

#[derive(Default)]
pub struct AssistantSurface(
    Mutex<SurfaceSnapshot>,
    Mutex<Option<crate::window::ForegroundTarget>>,
);

fn snapshot(app: &AppHandle) -> Result<SurfaceSnapshot, String> {
    app.state::<AssistantSurface>()
        .0
        .lock()
        .map(|value| *value)
        .map_err(|_| "无法读取灵动岛操作状态".into())
}

pub(crate) fn is_interactive(app: &AppHandle) -> bool {
    snapshot(app).is_ok_and(|value| value.interactive)
}

/// WM_ACTIVATE carries the previous window even when WebView focus changed
/// before the JavaScript click command arrived. Never erase that observation
/// just because a later read already sees our own window.
pub(crate) fn remember_foreground(
    app: &AppHandle,
    observed: Option<crate::window::ForegroundTarget>,
) {
    let interactive = is_interactive(app);
    if let Ok(mut previous) = app.state::<AssistantSurface>().1.lock() {
        *previous = return_target(*previous, observed, interactive);
    }
}

fn return_target<T: Copy>(
    previous: Option<T>,
    observed: Option<T>,
    interactive: bool,
) -> Option<T> {
    if interactive {
        previous
    } else {
        observed.or(previous)
    }
}

fn publish(
    app: &AppHandle,
    interactive: Option<bool>,
    shortcut: Option<bool>,
) -> Result<SurfaceSnapshot, String> {
    let (changed, value) = {
        let state = app.state::<AssistantSurface>();
        let mut value = state.0.lock().map_err(|_| "无法更新灵动岛操作状态")?;
        let next_interactive = interactive.unwrap_or(value.interactive);
        let next_shortcut = shortcut.unwrap_or(value.shortcut_available);
        let changed = value.change(next_interactive, next_shortcut);
        (changed, *value)
    };
    if changed {
        app.emit_to("main", SURFACE_EVENT, value)
            .map_err(|error| format!("无法通知灵动岛操作状态：{error}"))?;
    }
    Ok(value)
}

#[tauri::command]
pub fn assistant_surface_state(app: AppHandle) -> Result<Option<SurfaceSnapshot>, String> {
    if !crate::assistant::is_integrated(&app) {
        return Ok(None);
    }
    snapshot(&app).map(Some)
}

#[tauri::command]
pub fn assistant_release_controls(window: WebviewWindow) -> Result<SurfaceSnapshot, String> {
    if window.label() != "main" {
        return Err("仅主灵动岛可释放操作面板".into());
    }
    release_controls(window.app_handle())
}

pub fn release_controls(app: &AppHandle) -> Result<SurfaceSnapshot, String> {
    if !crate::assistant::is_integrated(app) {
        return Err("灵动岛尚未连接元枢，无法更新助手展示状态".into());
    }
    let window = app.get_webview_window("main").ok_or("找不到主灵动岛窗口")?;
    crate::window::set_assistant_input(&window, false)?;
    let state = publish(app, Some(false), None)?;
    // Take the target before restoring: the resulting blur is idempotent and
    // a user's click on another app can never be undone by this restoration.
    let previous = app
        .state::<AssistantSurface>()
        .1
        .lock()
        .ok()
        .and_then(|mut target| target.take());
    if let Some(previous) = previous {
        let _ = crate::window::restore_assistant_foreground(&window, previous);
    }
    Ok(state)
}

pub fn show_controls(app: &AppHandle) -> Result<(), String> {
    if !crate::assistant::is_integrated(app) {
        return Err("灵动岛尚未连接元枢，无法打开助手操作面板".into());
    }
    let window = app.get_webview_window("main").ok_or("找不到主灵动岛窗口")?;
    if !snapshot(app)?.interactive {
        let previous = crate::window::capture_assistant_foreground(&window);
        remember_foreground(app, previous);
    }
    crate::window::expand_assistant(&window)?;
    crate::window::set_assistant_input(&window, true)?;
    let result = (|| {
        publish(app, Some(true), None)?;
        window
            .emit("island-mode-reset", "expanded")
            .map_err(|error| format!("无法展开灵动岛操作面板：{error}"))?;
        window
            .show()
            .map_err(|error| format!("无法显示灵动岛：{error}"))?;
        window
            .set_focus()
            .map_err(|error| format!("无法聚焦灵动岛：{error}"))
    })();
    if result.is_err() {
        let _ = release_controls(app);
    }
    result
}

pub struct ShortcutWorker {
    stop: mpsc::Sender<()>,
    worker: Mutex<Option<JoinHandle<()>>>,
}

impl ShortcutWorker {
    pub fn stop(&self) {
        let _ = self.stop.send(());
        if let Some(worker) = self.worker.lock().ok().and_then(|mut value| value.take()) {
            let _ = worker.join();
        }
    }
}

impl Drop for ShortcutWorker {
    fn drop(&mut self) {
        self.stop();
    }
}

pub fn start_shortcut(app: AppHandle) -> Result<ShortcutWorker, String> {
    let (stop, stop_receiver) = mpsc::channel();
    let (ready, ready_receiver) = mpsc::sync_channel(1);
    let worker_app = app.clone();
    let worker = thread::Builder::new()
        .name("assistant-surface-hotkey".into())
        .spawn(move || run_shortcut(worker_app, stop_receiver, ready))
        .map_err(|error| format!("无法创建灵动岛快捷键线程：{error}"))?;
    let handle = ShortcutWorker {
        stop,
        worker: Mutex::new(Some(worker)),
    };
    let available = match ready_receiver.recv_timeout(Duration::from_secs(2)) {
        Ok(value) => value,
        Err(_) => {
            handle.stop();
            false
        }
    };
    // A competing app may own the shortcut. The tray remains the reliable
    // recovery entrance, so unavailable shortcuts never abort startup.
    publish(&app, None, Some(available))?;
    Ok(handle)
}

fn run_shortcut(app: AppHandle, stop: mpsc::Receiver<()>, ready: mpsc::SyncSender<bool>) {
    use windows::Win32::UI::{
        Input::KeyboardAndMouse::{
            RegisterHotKey, UnregisterHotKey, MOD_ALT, MOD_CONTROL, MOD_NOREPEAT, VK_SPACE,
        },
        WindowsAndMessaging::{PeekMessageW, MSG, PM_NOREMOVE, PM_REMOVE, WM_HOTKEY},
    };
    let mut message = MSG::default();
    // Explicitly create this thread's message queue before registration.
    unsafe {
        let _ = PeekMessageW(&mut message, None, 0, 0, PM_NOREMOVE);
    }
    let registered = unsafe {
        RegisterHotKey(
            None,
            HOTKEY_ID,
            MOD_CONTROL | MOD_ALT | MOD_NOREPEAT,
            VK_SPACE.0 as u32,
        )
    }
    .is_ok();
    let _ = ready.send(registered);
    if !registered {
        return;
    }
    loop {
        while unsafe { PeekMessageW(&mut message, None, 0, 0, PM_REMOVE) }.as_bool() {
            if message.message == WM_HOTKEY && message.wParam.0 == HOTKEY_ID as usize {
                let queued_app = app.clone();
                let _ = app.run_on_main_thread(move || {
                    let _ = crate::presence::request_show(&queued_app, true);
                });
            }
        }
        match stop.recv_timeout(Duration::from_millis(30)) {
            Ok(()) | Err(mpsc::RecvTimeoutError::Disconnected) => break,
            Err(mpsc::RecvTimeoutError::Timeout) => {}
        }
    }
    unsafe {
        let _ = UnregisterHotKey(None, HOTKEY_ID);
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn early_native_activation_survives_late_click_and_duplicate_manual_activation() {
        let before_click = return_target(None, Some(100), false);
        assert_eq!(before_click, Some(100));
        // WebView already focused the island before show_controls reads it.
        let after_click = return_target(before_click, None, false);
        assert_eq!(after_click, Some(100));
        assert_eq!(return_target(after_click, Some(200), true), Some(100));
        // A later deliberate activation from another application records it.
        assert_eq!(return_target(None, Some(200), false), Some(200));
        assert_eq!(return_target::<usize>(None, None, false), None);
    }

    #[test]
    fn surface_revision_is_monotonic_and_duplicate_changes_are_idempotent() {
        let mut state = SurfaceSnapshot::default();
        assert!(!state.change(false, false));
        assert!(state.change(false, true));
        assert_eq!(state.revision, 1);
        assert!(state.change(true, true));
        assert_eq!(state.revision, 2);
        assert!(!state.change(true, true));
        assert!(state.change(false, true));
        assert_eq!(state.revision, 3);
        assert!(!state.interactive);
    }

    #[test]
    fn shortcut_failure_does_not_enable_interaction_or_create_a_revision() {
        let mut state = SurfaceSnapshot::default();
        assert!(!state.change(false, false));
        assert_eq!(
            serde_json::to_value(state).unwrap(),
            serde_json::json!({
                "interactive": false, "shortcutAvailable": false, "revision": 0
            })
        );
    }
}
