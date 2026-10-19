use tauri::{Manager, WebviewWindow};

use super::geometry::PhysicalWindowFrame;

#[cfg(windows)]
#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub(crate) struct ForegroundTarget {
    hwnd: usize,
    process: u32,
    thread: u32,
}

#[cfg(windows)]
pub(super) fn owns_foreground(window: &WebviewWindow) -> bool {
    use windows::Win32::UI::WindowsAndMessaging::{GetAncestor, GetForegroundWindow, GA_ROOT};
    let Ok(own) = window.hwnd() else {
        return false;
    };
    let foreground = unsafe { GetForegroundWindow() };
    foreground == own || unsafe { GetAncestor(foreground, GA_ROOT) } == own
}

#[cfg(windows)]
pub(super) fn capture_foreground(window: &WebviewWindow) -> Option<ForegroundTarget> {
    use windows::Win32::UI::WindowsAndMessaging::GetForegroundWindow;
    let own = window.hwnd().ok()?;
    let current = unsafe { GetForegroundWindow() };
    external_foreground_target(own, current)
}

fn external_foreground_target(
    own: windows::Win32::Foundation::HWND,
    current: windows::Win32::Foundation::HWND,
) -> Option<ForegroundTarget> {
    use windows::Win32::UI::WindowsAndMessaging::{GetAncestor, GetWindowThreadProcessId, GA_ROOT};
    if current.0.is_null() || current == own || unsafe { GetAncestor(current, GA_ROOT) } == own {
        return None;
    }
    let mut process = 0;
    let thread = unsafe { GetWindowThreadProcessId(current, Some(&mut process)) };
    (thread != 0 && process != 0).then_some(ForegroundTarget {
        hwnd: current.0 as usize,
        process,
        thread,
    })
}

/// Observes only the integrated island's own native activation messages.
/// Install on the UI thread before showing the window; the subclass owns its
/// AppHandle until WM_NCDESTROY and never changes foreground inside a callback.
pub(super) fn install_assistant_focus_observer(window: &WebviewWindow) -> Result<(), String> {
    use windows::Win32::UI::Shell::SetWindowSubclass;
    let hwnd = window.hwnd().map_err(|_| "无法读取灵动岛窗口")?;
    let data = Box::into_raw(Box::new(window.app_handle().clone()));
    if !unsafe {
        SetWindowSubclass(
            hwnd,
            Some(assistant_focus_observer),
            0x59534647,
            data as usize,
        )
    }
    .as_bool()
    {
        // The callback never received ownership when installation failed.
        unsafe { drop(Box::from_raw(data)) };
        return Err("无法记录灵动岛操作前的窗口焦点".into());
    }
    Ok(())
}

unsafe extern "system" fn assistant_focus_observer(
    hwnd: windows::Win32::Foundation::HWND,
    message: u32,
    wparam: windows::Win32::Foundation::WPARAM,
    lparam: windows::Win32::Foundation::LPARAM,
    subclass_id: usize,
    data: usize,
) -> windows::Win32::Foundation::LRESULT {
    use windows::Win32::{
        Foundation::HWND,
        UI::{
            Shell::{DefSubclassProc, RemoveWindowSubclass},
            WindowsAndMessaging::{
                GetForegroundWindow, WM_ACTIVATE, WM_MOUSEACTIVATE, WM_NCDESTROY,
            },
        },
    };
    if message == WM_NCDESTROY {
        let _ = RemoveWindowSubclass(hwnd, Some(assistant_focus_observer), subclass_id);
        let result = DefSubclassProc(hwnd, message, wparam, lparam);
        drop(Box::from_raw(data as *mut tauri::AppHandle));
        return result;
    }
    let previous = if message == WM_MOUSEACTIVATE {
        external_foreground_target(hwnd, GetForegroundWindow())
    } else if message == WM_ACTIVATE && wparam.0 & 0xffff != 0 {
        external_foreground_target(hwnd, HWND(lparam.0 as *mut _))
    } else {
        None
    };
    if previous.is_some() {
        let app = &*(data as *const tauri::AppHandle);
        crate::assistant_surface::remember_foreground(app, previous);
    }
    DefSubclassProc(hwnd, message, wparam, lparam)
}

#[cfg(windows)]
pub(super) fn restore_foreground(window: &WebviewWindow, target: ForegroundTarget) -> bool {
    use windows::Win32::{
        Foundation::HWND,
        UI::WindowsAndMessaging::{
            GetForegroundWindow, GetWindowThreadProcessId, IsIconic, IsWindowVisible,
            SetForegroundWindow,
        },
    };
    let Ok(own) = window.hwnd() else {
        return false;
    };
    let current = unsafe { GetForegroundWindow() };
    let previous = HWND(target.hwnd as *mut _);
    let mut process = 0;
    let thread = unsafe { GetWindowThreadProcessId(previous, Some(&mut process)) };
    if !should_restore_foreground(
        own.0 as usize,
        current.0 as usize,
        target,
        ForegroundTarget {
            hwnd: target.hwnd,
            process,
            thread,
        },
        unsafe { IsWindowVisible(previous) }.as_bool(),
        unsafe { IsIconic(previous) }.as_bool(),
    ) {
        return false;
    }
    unsafe { SetForegroundWindow(previous) }.as_bool()
}

#[cfg(windows)]
fn should_restore_foreground(
    own: usize,
    current: usize,
    recorded: ForegroundTarget,
    observed: ForegroundTarget,
    visible: bool,
    minimized: bool,
) -> bool {
    current == own
        && recorded.hwnd != own
        && recorded.hwnd != 0
        && recorded.process != 0
        && recorded.thread != 0
        && recorded == observed
        && visible
        && !minimized
}

#[cfg(windows)]
pub(super) fn set_assistant_input(
    window: &WebviewWindow,
    hit: bool,
    interactive: bool,
) -> Result<(), String> {
    use windows::Win32::UI::WindowsAndMessaging::{
        GetWindowLongPtrW, SetWindowLongPtrW, GWL_EXSTYLE, WS_EX_LAYERED, WS_EX_NOACTIVATE,
        WS_EX_TRANSPARENT,
    };
    let hwnd = window
        .hwnd()
        .map_err(|error| format!("无法读取灵动岛窗口：{error}"))?;
    if !interactive {
        // Tao may call ShowWindow while applying flag changes. Set NOACTIVATE
        // first so releasing controls after blur cannot take focus back.
        let style = unsafe { GetWindowLongPtrW(hwnd, GWL_EXSTYLE) };
        unsafe { SetWindowLongPtrW(hwnd, GWL_EXSTYLE, style | WS_EX_NOACTIVATE.0 as isize) };
    }
    window
        .set_focusable(interactive)
        .map_err(|error| format!("无法切换灵动岛焦点状态：{error}"))?;
    window
        .set_ignore_cursor_events(!hit)
        .map_err(|error| format!("无法切换灵动岛鼠标穿透：{error}"))?;
    let style = unsafe { GetWindowLongPtrW(hwnd, GWL_EXSTYLE) } as u32;
    if !input_style_matches(
        style,
        hit,
        interactive,
        WS_EX_TRANSPARENT.0,
        WS_EX_LAYERED.0,
        WS_EX_NOACTIVATE.0,
    ) {
        return Err("灵动岛鼠标穿透尚未生效".into());
    }
    Ok(())
}

#[cfg(windows)]
fn input_style_matches(
    style: u32,
    hit: bool,
    interactive: bool,
    transparent: u32,
    layered: u32,
    no_activate: u32,
) -> bool {
    if hit {
        style & transparent == 0 && (style & no_activate == 0) == interactive
    } else {
        style & (transparent | layered | no_activate) == transparent | layered | no_activate
    }
}

#[cfg(windows)]
pub(super) fn apply_frame(
    window: &WebviewWindow,
    frame: PhysicalWindowFrame,
) -> Result<(), String> {
    use windows::Win32::UI::WindowsAndMessaging::{SetWindowPos, SWP_NOACTIVATE, SWP_NOZORDER};

    let frame = Win32WindowFrame::try_from(frame)?;
    let hwnd = window
        .hwnd()
        .map_err(|error| format!("无法读取灵动岛原生窗口句柄：{error}"))?;

    // SAFETY: `hwnd` comes from the live Tauri window, both extents were
    // checked to fit Win32's signed integer range, and SWP_NOZORDER preserves
    // the window's existing topmost state while SWP_NOACTIVATE avoids stealing
    // focus. SetWindowPos commits position and size in one native operation, so
    // high-frequency observers cannot see the half-applied two-call state.
    unsafe {
        SetWindowPos(
            hwnd,
            None,
            frame.x,
            frame.y,
            frame.width,
            frame.height,
            SWP_NOZORDER | SWP_NOACTIVATE,
        )
    }
    .map_err(|error| format!("无法原子调整灵动岛窗口：{error}"))
}

#[cfg(not(windows))]
pub(super) fn apply_frame(
    window: &WebviewWindow,
    frame: PhysicalWindowFrame,
) -> Result<(), String> {
    use tauri::{PhysicalPosition, PhysicalSize};

    window
        .set_size(PhysicalSize::new(frame.width, frame.height))
        .map_err(|error| format!("无法调整灵动岛窗口尺寸：{error}"))?;
    window
        .set_position(PhysicalPosition::new(frame.x, frame.y))
        .map_err(|error| format!("无法定位灵动岛窗口：{error}"))?;
    Ok(())
}

#[cfg(windows)]
#[derive(Clone, Copy, Debug, PartialEq, Eq)]
struct Win32WindowFrame {
    x: i32,
    y: i32,
    width: i32,
    height: i32,
}

#[cfg(windows)]
impl TryFrom<PhysicalWindowFrame> for Win32WindowFrame {
    type Error = String;

    fn try_from(frame: PhysicalWindowFrame) -> Result<Self, Self::Error> {
        Ok(Self {
            x: frame.x,
            y: frame.y,
            width: i32::try_from(frame.width)
                .map_err(|_| format!("灵动岛窗口宽度超出 Win32 范围：{}", frame.width))?,
            height: i32::try_from(frame.height)
                .map_err(|_| format!("灵动岛窗口高度超出 Win32 范围：{}", frame.height))?,
        })
    }
}

#[cfg(all(test, windows))]
mod tests {
    use super::*;

    #[test]
    fn restoring_manual_focus_never_overrides_a_new_foreground_or_a_reused_handle() {
        let target = ForegroundTarget {
            hwnd: 100,
            process: 20,
            thread: 30,
        };
        assert!(should_restore_foreground(
            200, 200, target, target, true, false
        ));
        assert!(!should_restore_foreground(
            200, 300, target, target, true, false
        ));
        assert!(!should_restore_foreground(
            200,
            200,
            target,
            ForegroundTarget {
                process: 21,
                ..target
            },
            true,
            false
        ));
        assert!(!should_restore_foreground(
            200,
            200,
            target,
            ForegroundTarget {
                thread: 31,
                ..target
            },
            true,
            false
        ));
        assert!(!should_restore_foreground(
            200, 200, target, target, false, false
        ));
        assert!(!should_restore_foreground(
            200, 200, target, target, true, true
        ));
        assert!(!should_restore_foreground(
            100, 100, target, target, true, false
        ));
    }

    #[test]
    fn display_input_requires_all_three_native_flags_and_controls_require_hit_testing() {
        let (transparent, layered, no_activate) = (0x20, 0x80000, 0x8000000);
        let display = transparent | layered | no_activate;
        assert!(input_style_matches(
            display,
            false,
            false,
            transparent,
            layered,
            no_activate
        ));
        for flag in [transparent, layered, no_activate] {
            assert!(!input_style_matches(
                display & !flag,
                false,
                false,
                transparent,
                layered,
                no_activate
            ));
        }
        assert!(input_style_matches(
            layered,
            true,
            true,
            transparent,
            layered,
            no_activate
        ));
        assert!(!input_style_matches(
            display,
            true,
            true,
            transparent,
            layered,
            no_activate
        ));
        assert!(input_style_matches(
            no_activate,
            true,
            false,
            transparent,
            layered,
            no_activate
        ));
        assert!(!input_style_matches(
            0,
            true,
            false,
            transparent,
            layered,
            no_activate
        ));
    }

    #[test]
    fn converts_valid_physical_frame_to_win32_arguments_without_losing_coordinates() {
        let frame = Win32WindowFrame::try_from(PhysicalWindowFrame {
            x: -938,
            y: 120,
            width: 630,
            height: 378,
        })
        .expect("the island frame must fit Win32 coordinates");

        assert_eq!(
            frame,
            Win32WindowFrame {
                x: -938,
                y: 120,
                width: 630,
                height: 378,
            }
        );
    }

    #[test]
    fn rejects_unsigned_extents_that_win32_cannot_represent() {
        let too_large = i32::MAX as u32 + 1;

        assert!(Win32WindowFrame::try_from(PhysicalWindowFrame {
            x: 0,
            y: 0,
            width: too_large,
            height: 1,
        })
        .is_err());
        assert!(Win32WindowFrame::try_from(PhysicalWindowFrame {
            x: 0,
            y: 0,
            width: 1,
            height: too_large,
        })
        .is_err());
    }
}
