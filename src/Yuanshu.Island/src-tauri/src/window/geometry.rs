#[derive(Clone, Copy, Debug, PartialEq)]
pub struct LogicalWindowFrame {
    pub width: f64,
    pub height: f64,
    pub top_gap: f64,
}

impl LogicalWindowFrame {
    pub const fn new(width: f64, height: f64, top_gap: f64) -> Self {
        Self {
            width,
            height,
            top_gap,
        }
    }
}

pub const COMPACT_FRAME: LogicalWindowFrame = LogicalWindowFrame::new(296.0, 54.0, 4.0);
pub const EXPANDED_FRAME: LogicalWindowFrame = LogicalWindowFrame::new(420.0, 252.0, 4.0);
pub const ASSISTANT_COMPACT_FRAME: LogicalWindowFrame = LogicalWindowFrame::new(224.0, 28.0, 0.0);
pub const ASSISTANT_EXPANDED_FRAME: LogicalWindowFrame = LogicalWindowFrame::new(480.0, 330.0, 0.0);
const ASSISTANT_RIGHT_GAP: f64 = 12.0;
pub const ORB_FRAME: LogicalWindowFrame = LogicalWindowFrame::new(44.0, 44.0, 0.0);
pub const TUCKED_FRAME: LogicalWindowFrame = LogicalWindowFrame::new(44.0, 18.0, 0.0);

#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub enum IslandMode {
    Compact,
    Expanded,
    Orb,
    Tucked,
}

impl IslandMode {
    pub const fn assistant_frame(self, integrated: bool) -> LogicalWindowFrame {
        if integrated {
            match self {
                Self::Expanded => ASSISTANT_EXPANDED_FRAME,
                _ => ASSISTANT_COMPACT_FRAME,
            }
        } else {
            self.logical_frame()
        }
    }

    pub const fn logical_frame(self) -> LogicalWindowFrame {
        match self {
            Self::Compact => COMPACT_FRAME,
            Self::Expanded => EXPANDED_FRAME,
            Self::Orb => ORB_FRAME,
            Self::Tucked => TUCKED_FRAME,
        }
    }
}

impl TryFrom<&str> for IslandMode {
    type Error = String;

    fn try_from(value: &str) -> Result<Self, Self::Error> {
        match value {
            "compact" => Ok(Self::Compact),
            "expanded" => Ok(Self::Expanded),
            "orb" => Ok(Self::Orb),
            "tucked" => Ok(Self::Tucked),
            _ => Err(format!(
                "不支持的灵动岛模式“{value}”；只接受 compact、expanded、orb 或 tucked"
            )),
        }
    }
}

#[derive(Clone, Copy, Debug, PartialEq)]
pub struct MonitorGeometry {
    pub origin_x: i32,
    pub origin_y: i32,
    pub width: u32,
    pub scale_factor: f64,
}

impl MonitorGeometry {
    pub const fn new(origin_x: i32, origin_y: i32, width: u32, scale_factor: f64) -> Self {
        Self {
            origin_x,
            origin_y,
            width,
            scale_factor,
        }
    }
}

#[derive(Clone, Copy, Debug, PartialEq)]
pub struct WorkAreaGeometry {
    pub origin_x: i32,
    pub origin_y: i32,
    pub width: u32,
    pub height: u32,
    pub scale_factor: f64,
}

#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub struct PhysicalWindowFrame {
    pub x: i32,
    pub y: i32,
    pub width: u32,
    pub height: u32,
}

pub fn physical_frame_for(
    logical_frame: LogicalWindowFrame,
    monitor: MonitorGeometry,
) -> PhysicalWindowFrame {
    let scale = valid_scale_factor(monitor.scale_factor);
    let width = logical_to_physical(logical_frame.width, scale);
    let height = logical_to_physical(logical_frame.height, scale);
    let centered_offset = (i64::from(monitor.width) - i64::from(width)) / 2;

    PhysicalWindowFrame {
        x: saturating_i64_to_i32(i64::from(monitor.origin_x) + centered_offset),
        y: saturating_i64_to_i32(
            i64::from(monitor.origin_y) + (logical_frame.top_gap * scale).round() as i64,
        ),
        width,
        height,
    }
}

/// The assistant has a stable right-side anchor, clear of browser chrome.
/// Both sizes share the work area's vertical center and right inset. The
/// independent island continues using the original top-center calculation.
pub fn physical_assistant_frame_for(
    logical_frame: LogicalWindowFrame,
    work_area: WorkAreaGeometry,
) -> PhysicalWindowFrame {
    let scale = valid_scale_factor(work_area.scale_factor);
    let width = logical_to_physical(logical_frame.width, scale).min(work_area.width.max(1));
    let height = logical_to_physical(logical_frame.height, scale).min(work_area.height.max(1));
    let remaining_width = work_area.width.saturating_sub(width);
    let right_gap = logical_to_physical(ASSISTANT_RIGHT_GAP, scale).min(remaining_width);
    PhysicalWindowFrame {
        x: saturating_i64_to_i32(
            i64::from(work_area.origin_x) + i64::from(remaining_width - right_gap),
        ),
        y: saturating_i64_to_i32(
            i64::from(work_area.origin_y) + i64::from(work_area.height.saturating_sub(height) / 2),
        ),
        width,
        height,
    }
}

pub fn logical_frame_from_physical(
    width: u32,
    height: u32,
    y: i32,
    monitor: MonitorGeometry,
) -> LogicalWindowFrame {
    let scale = valid_scale_factor(monitor.scale_factor);
    LogicalWindowFrame::new(
        f64::from(width) / scale,
        f64::from(height) / scale,
        (f64::from(y) - f64::from(monitor.origin_y)) / scale,
    )
}

pub fn interpolate_frame(
    from: LogicalWindowFrame,
    to: LogicalWindowFrame,
    progress: f64,
) -> LogicalWindowFrame {
    let eased = ease_out_cubic(progress);
    LogicalWindowFrame::new(
        from.width + (to.width - from.width) * eased,
        from.height + (to.height - from.height) * eased,
        from.top_gap + (to.top_gap - from.top_gap) * eased,
    )
}

fn ease_out_cubic(progress: f64) -> f64 {
    let clamped = progress.clamp(0.0, 1.0);
    1.0 - (1.0 - clamped).powi(3)
}

fn valid_scale_factor(scale_factor: f64) -> f64 {
    if scale_factor.is_finite() && scale_factor > 0.0 {
        scale_factor
    } else {
        1.0
    }
}

fn logical_to_physical(value: f64, scale_factor: f64) -> u32 {
    let physical = (value.max(1.0) * scale_factor).round();
    physical.clamp(1.0, f64::from(u32::MAX)) as u32
}

fn saturating_i64_to_i32(value: i64) -> i32 {
    value.clamp(i64::from(i32::MIN), i64::from(i32::MAX)) as i32
}

#[cfg(test)]
mod tests {
    use super::*;

    const FULL_HD_WIDTH: u32 = 1920;

    fn monitor(scale_factor: f64) -> MonitorGeometry {
        MonitorGeometry::new(0, 0, FULL_HD_WIDTH, scale_factor)
    }

    #[test]
    fn parses_only_the_four_supported_modes() {
        assert_eq!(IslandMode::try_from("compact"), Ok(IslandMode::Compact));
        assert_eq!(IslandMode::try_from("expanded"), Ok(IslandMode::Expanded));
        assert_eq!(IslandMode::try_from("orb"), Ok(IslandMode::Orb));
        assert_eq!(IslandMode::try_from("tucked"), Ok(IslandMode::Tucked));
        assert!(IslandMode::try_from("Compact").is_err());
        assert!(IslandMode::try_from("hidden").is_err());
        assert!(IslandMode::try_from("").is_err());
    }

    #[test]
    fn integrated_frames_preserve_readable_microphone_surface_and_standalone_sizes() {
        assert_eq!(
            IslandMode::Expanded.assistant_frame(true),
            ASSISTANT_EXPANDED_FRAME
        );
        assert_eq!(IslandMode::Expanded.assistant_frame(false), EXPANDED_FRAME);
        assert_eq!(
            IslandMode::Compact.assistant_frame(true),
            ASSISTANT_COMPACT_FRAME
        );
        assert_eq!(
            IslandMode::Orb.assistant_frame(true),
            ASSISTANT_COMPACT_FRAME
        );
        assert_eq!(
            IslandMode::Tucked.assistant_frame(true),
            ASSISTANT_COMPACT_FRAME
        );
        assert_eq!(IslandMode::Orb.assistant_frame(false), ORB_FRAME);
        let frame = physical_assistant_frame_for(
            ASSISTANT_EXPANDED_FRAME,
            WorkAreaGeometry {
                origin_x: 0,
                origin_y: 40,
                width: 1920,
                height: 1040,
                scale_factor: 1.25,
            },
        );
        assert_eq!(frame.width, 600);
        assert_eq!(frame.height, 413);
        assert_eq!(frame.x, 1305);
        assert_eq!(frame.y, 353);
    }

    #[test]
    fn assistant_sizes_share_the_right_center_anchor_without_covering_top_chrome() {
        for (scale, gap, compact_size, expanded_size) in [
            (1.0, 12, (224, 28), (480, 330)),
            (1.25, 15, (280, 35), (600, 413)),
            (1.5, 18, (336, 42), (720, 495)),
        ] {
            for (x, y, width, height) in [
                (0, 0, 1920, 1040),
                (-1920, 40, 1880, 1000),
                (2160, -1200, 1920, 1040),
            ] {
                let area = WorkAreaGeometry {
                    origin_x: x,
                    origin_y: y,
                    width,
                    height,
                    scale_factor: scale,
                };
                for (logical, expected) in [
                    (ASSISTANT_COMPACT_FRAME, compact_size),
                    (ASSISTANT_EXPANDED_FRAME, expanded_size),
                ] {
                    let frame = physical_assistant_frame_for(logical, area);
                    assert_eq!((frame.width, frame.height), expected);
                    assert_eq!(frame.x + frame.width as i32, x + width as i32 - gap);
                    let center_error =
                        (2 * (frame.y - y) + frame.height as i32 - height as i32).abs();
                    assert!(
                        center_error <= 1,
                        "half-pixel centers must round without drifting"
                    );
                    assert!(
                        frame.y >= y + (96.0 * scale).round() as i32,
                        "browser tabs and address bar must remain clear"
                    );
                    assert!(frame.x >= x && frame.y >= y);
                    assert!(frame.x + frame.width as i32 <= x + width as i32);
                    assert!(frame.y + frame.height as i32 <= y + height as i32);
                }
            }
        }
    }

    #[test]
    fn assistant_uses_work_area_edges_and_clamps_a_constrained_work_area() {
        let area = WorkAreaGeometry {
            origin_x: -860,
            origin_y: 75,
            width: 460,
            height: 300,
            scale_factor: 1.5,
        };
        let frame = physical_assistant_frame_for(ASSISTANT_EXPANDED_FRAME, area);
        assert_eq!(
            frame,
            PhysicalWindowFrame {
                x: -860,
                y: 75,
                width: 460,
                height: 300
            }
        );
        let compact = physical_assistant_frame_for(ASSISTANT_COMPACT_FRAME, area);
        assert_eq!(
            compact,
            PhysicalWindowFrame {
                x: -754,
                y: 204,
                width: 336,
                height: 42
            }
        );
    }

    #[test]
    fn all_modes_are_centered_at_100_125_and_150_percent() {
        let cases = [
            (
                1.0,
                [
                    PhysicalWindowFrame {
                        x: 812,
                        y: 4,
                        width: 296,
                        height: 54,
                    },
                    PhysicalWindowFrame {
                        x: 750,
                        y: 4,
                        width: 420,
                        height: 252,
                    },
                    PhysicalWindowFrame {
                        x: 938,
                        y: 0,
                        width: 44,
                        height: 44,
                    },
                    PhysicalWindowFrame {
                        x: 938,
                        y: 0,
                        width: 44,
                        height: 18,
                    },
                ],
            ),
            (
                1.25,
                [
                    PhysicalWindowFrame {
                        x: 775,
                        y: 5,
                        width: 370,
                        height: 68,
                    },
                    PhysicalWindowFrame {
                        x: 697,
                        y: 5,
                        width: 525,
                        height: 315,
                    },
                    PhysicalWindowFrame {
                        x: 932,
                        y: 0,
                        width: 55,
                        height: 55,
                    },
                    PhysicalWindowFrame {
                        x: 932,
                        y: 0,
                        width: 55,
                        height: 23,
                    },
                ],
            ),
            (
                1.5,
                [
                    PhysicalWindowFrame {
                        x: 738,
                        y: 6,
                        width: 444,
                        height: 81,
                    },
                    PhysicalWindowFrame {
                        x: 645,
                        y: 6,
                        width: 630,
                        height: 378,
                    },
                    PhysicalWindowFrame {
                        x: 927,
                        y: 0,
                        width: 66,
                        height: 66,
                    },
                    PhysicalWindowFrame {
                        x: 927,
                        y: 0,
                        width: 66,
                        height: 27,
                    },
                ],
            ),
        ];
        let modes = [
            IslandMode::Compact,
            IslandMode::Expanded,
            IslandMode::Orb,
            IslandMode::Tucked,
        ];

        for (scale_factor, expected_frames) in cases {
            for (mode, expected) in modes.into_iter().zip(expected_frames) {
                assert_eq!(
                    physical_frame_for(mode.logical_frame(), monitor(scale_factor)),
                    expected,
                    "unexpected {mode:?} frame at {scale_factor}x"
                );
            }
        }
    }

    #[test]
    fn tucked_mode_uses_the_monitor_top_even_with_a_nonzero_origin() {
        for scale_factor in [1.0, 1.25, 1.5] {
            let monitor = MonitorGeometry::new(-1920, 120, FULL_HD_WIDTH, scale_factor);
            let frame = physical_frame_for(TUCKED_FRAME, monitor);
            assert_eq!(frame.y, 120);
        }
    }

    #[test]
    fn interpolation_has_exact_reversible_endpoints_and_moves_the_top_gap() {
        for mode in [IslandMode::Expanded, IslandMode::Orb, IslandMode::Tucked] {
            let target = mode.logical_frame();
            assert_eq!(interpolate_frame(COMPACT_FRAME, target, 0.0), COMPACT_FRAME);
            assert_eq!(interpolate_frame(COMPACT_FRAME, target, 1.0), target);
            assert_eq!(interpolate_frame(target, COMPACT_FRAME, 1.0), COMPACT_FRAME);
        }

        let midpoint = interpolate_frame(COMPACT_FRAME, TUCKED_FRAME, 0.5);
        assert!((midpoint.width - 75.5).abs() < f64::EPSILON);
        assert!((midpoint.height - 22.5).abs() < f64::EPSILON);
        assert!((midpoint.top_gap - 0.5).abs() < f64::EPSILON);
    }

    #[test]
    fn interrupted_physical_frame_round_trips_without_a_logical_jump() {
        let monitor = MonitorGeometry::new(0, 120, FULL_HD_WIDTH, 1.25);
        let interrupted = LogicalWindowFrame::new(123.2, 77.6, 2.4);
        let physical = physical_frame_for(interrupted, monitor);
        let restored =
            logical_frame_from_physical(physical.width, physical.height, physical.y, monitor);

        assert!((restored.width - interrupted.width).abs() < f64::EPSILON);
        assert!((restored.height - interrupted.height).abs() < f64::EPSILON);
        assert!((restored.top_gap - interrupted.top_gap).abs() < f64::EPSILON);
    }
}
