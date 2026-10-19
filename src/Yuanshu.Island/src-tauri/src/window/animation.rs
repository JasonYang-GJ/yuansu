use std::{
    sync::{
        atomic::{AtomicU64, Ordering},
        mpsc, Arc,
    },
    thread,
    time::{Duration, Instant},
};

use tauri::{Manager, WebviewWindow};

use super::{
    geometry::{
        interpolate_frame, logical_frame_from_physical, physical_frame_for, IslandMode,
        LogicalWindowFrame, MonitorGeometry, COMPACT_FRAME,
    },
    monitor_geometry,
    native_window::apply_frame,
};

const ANIMATION_DURATION: Duration = Duration::from_millis(280);
const FRAME_INTERVAL: Duration = Duration::from_millis(16);

#[derive(Clone, Default)]
pub struct AnimationController {
    generation: Arc<AtomicU64>,
}

impl AnimationController {
    fn begin(&self) -> u64 {
        self.generation.fetch_add(1, Ordering::AcqRel) + 1
    }

    fn is_current(&self, generation: u64) -> bool {
        self.generation.load(Ordering::Acquire) == generation
    }
}

pub(super) fn set_island_mode(
    mode: IslandMode,
    window: WebviewWindow,
    controller: AnimationController,
) -> Result<(), String> {
    if crate::assistant::is_integrated(window.app_handle()) {
        // The assistant waits for a painted transcript before submission.
        // A slow resize would acknowledge text outside the still-short window.
        controller.begin();
        return crate::assistant_placement::apply_mode(&window, mode);
    }
    animate_to(window, controller, mode)
}

fn animate_to(
    window: WebviewWindow,
    controller: AnimationController,
    mode: IslandMode,
) -> Result<(), String> {
    // Cancel queued frames before observing the window. Animation frames are
    // applied on the main thread, so a queued old frame either runs before this
    // command reads the window or sees the new generation and is discarded.
    let generation = controller.begin();
    let monitor = monitor_geometry(&window)?;
    let current = window
        .inner_size()
        .map_err(|error| format!("无法读取灵动岛窗口尺寸：{error}"))?;
    let current_position = window
        .outer_position()
        .map_err(|error| format!("无法读取灵动岛窗口位置：{error}"))?;
    let from =
        logical_frame_from_physical(current.width, current.height, current_position.y, monitor);
    let target = mode.assistant_frame(crate::assistant::is_integrated(window.app_handle()));

    thread::Builder::new()
        .name("island-window-animation".into())
        .spawn(move || run_animation(window, controller, generation, from, target, monitor))
        .map(|_| ())
        .map_err(|error| format!("无法启动窗口动画：{error}"))
}

fn run_animation(
    window: WebviewWindow,
    controller: AnimationController,
    generation: u64,
    from: LogicalWindowFrame,
    target: LogicalWindowFrame,
    monitor: MonitorGeometry,
) {
    let started_at = Instant::now();

    loop {
        if !controller.is_current(generation) {
            return;
        }

        let elapsed = started_at.elapsed();
        let progress = (elapsed.as_secs_f64() / ANIMATION_DURATION.as_secs_f64()).min(1.0);
        let logical_frame = interpolate_frame(from, target, progress);
        let frame = physical_frame_for(logical_frame, monitor);

        match apply_frame_on_main_thread(&window, &controller, generation, frame) {
            Ok(true) => {}
            Ok(false) | Err(_) => return,
        }

        if progress >= 1.0 {
            return;
        }

        let remaining = ANIMATION_DURATION.saturating_sub(elapsed);
        thread::sleep(FRAME_INTERVAL.min(remaining));
    }
}

fn apply_frame_on_main_thread(
    window: &WebviewWindow,
    controller: &AnimationController,
    generation: u64,
    frame: super::geometry::PhysicalWindowFrame,
) -> Result<bool, String> {
    let (sender, receiver) = mpsc::sync_channel(1);
    let scheduled_window = window.clone();
    let scheduled_controller = controller.clone();

    window
        .run_on_main_thread(move || {
            let result = if scheduled_controller.is_current(generation) {
                apply_frame(&scheduled_window, frame).map(|_| true)
            } else {
                Ok(false)
            };
            let _ = sender.send(result);
        })
        .map_err(|error| format!("无法将灵动岛动画帧调度到主线程：{error}"))?;

    receiver
        .recv()
        .map_err(|error| format!("灵动岛动画主线程未返回帧结果：{error}"))?
}

pub(super) fn reset_to_compact(
    window: &WebviewWindow,
    controller: &AnimationController,
) -> Result<(), String> {
    controller.begin();
    let monitor = monitor_geometry(window)?;
    apply_frame(window, physical_frame_for(COMPACT_FRAME, monitor))
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn newer_generation_cancels_every_older_queued_frame() {
        let controller = AnimationController::default();
        let first = controller.begin();
        let second = controller.begin();

        assert!(!controller.is_current(first));
        assert!(controller.is_current(second));
    }
}
