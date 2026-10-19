use std::{
    sync::{
        atomic::{AtomicBool, Ordering},
        mpsc::{self, Receiver, RecvTimeoutError, Sender},
        Arc, Mutex,
    },
    thread::{self, JoinHandle},
    time::{Duration, Instant, SystemTime, UNIX_EPOCH},
};

use serde::Serialize;
use tauri::{AppHandle, Emitter, State};
use windows::{
    Foundation::TypedEventHandler,
    Media::Control::{
        GlobalSystemMediaTransportControlsSession,
        GlobalSystemMediaTransportControlsSessionManager,
        GlobalSystemMediaTransportControlsSessionPlaybackStatus,
    },
    Win32::System::Com::{CoInitializeEx, CoUninitialize, COINIT_MULTITHREADED},
};

pub const MEDIA_EVENT_NAME: &str = "windows-media-changed";
const MEDIA_SOURCE: &str = "windows.media";
const MEDIA_KEY: &str = "current-session";
const MEDIA_EVENT_ID: &str = "windows.media.current-session";
const UNKNOWN_TITLE: &str = "未知媒体";
const UNKNOWN_ARTIST: &str = "未知艺术家";
const UNKNOWN_APP: &str = "unknown-app";
const TITLE_LIMIT: usize = 160;
const ARTIST_LIMIT: usize = 120;
const APP_ID_LIMIT: usize = 160;
const WORKER_WAIT: Duration = Duration::from_millis(250);
const MANAGER_HEALTH_CHECK: Duration = Duration::from_secs(5);
const RECOVERY_DELAYS: [Duration; 3] = [
    Duration::from_secs(1),
    Duration::from_secs(2),
    Duration::from_secs(5),
];

pub struct MediaProviderHandle {
    stop: Arc<AtomicBool>,
    current: Arc<Mutex<Option<MediaEventPayload>>>,
    worker: Mutex<Option<JoinHandle<()>>>,
}

impl Drop for MediaProviderHandle {
    fn drop(&mut self) {
        self.stop.store(true, Ordering::Release);
        if let Ok(mut worker) = self.worker.lock() {
            if let Some(worker) = worker.take() {
                let _ = worker.join();
            }
        }
    }
}

#[derive(Default)]
struct RecoveryBackoff {
    attempt: usize,
}

impl RecoveryBackoff {
    fn next_delay(&mut self) -> Duration {
        let index = self.attempt.min(RECOVERY_DELAYS.len() - 1);
        self.attempt = self.attempt.saturating_add(1);
        RECOVERY_DELAYS[index]
    }

    fn reset(&mut self) {
        self.attempt = 0;
    }
}

#[derive(Clone, Copy, Debug, PartialEq, Eq, Serialize)]
#[serde(rename_all = "lowercase")]
pub(crate) enum PlaybackState {
    Playing,
    Paused,
}

#[derive(Clone, Debug, PartialEq, Eq)]
struct MediaSnapshot {
    title: String,
    artist: String,
    playback_state: PlaybackState,
    app_id: String,
}

#[derive(Clone, Debug, PartialEq, Eq, Serialize)]
#[serde(
    tag = "kind",
    rename_all = "camelCase",
    rename_all_fields = "camelCase"
)]
pub(crate) enum MediaEventPayload {
    Update {
        title: String,
        artist: String,
        playback_state: PlaybackState,
        app_id: String,
        source: &'static str,
        key: &'static str,
        event_id: &'static str,
        timestamp_ms: u64,
    },
    Clear {
        source: &'static str,
        key: &'static str,
        event_id: &'static str,
        timestamp_ms: u64,
    },
}

impl MediaEventPayload {
    fn update(snapshot: MediaSnapshot, timestamp_ms: u64) -> Self {
        Self::Update {
            title: snapshot.title,
            artist: snapshot.artist,
            playback_state: snapshot.playback_state,
            app_id: snapshot.app_id,
            source: MEDIA_SOURCE,
            key: MEDIA_KEY,
            event_id: MEDIA_EVENT_ID,
            timestamp_ms,
        }
    }

    fn clear(timestamp_ms: u64) -> Self {
        Self::Clear {
            source: MEDIA_SOURCE,
            key: MEDIA_KEY,
            event_id: MEDIA_EVENT_ID,
            timestamp_ms,
        }
    }
}

#[derive(Clone, Copy, Debug, PartialEq, Eq)]
enum ProviderSignal {
    Refresh,
    Rebind,
}

struct ComApartment;

impl ComApartment {
    fn initialize() -> windows::core::Result<Self> {
        // SAFETY: this worker owns a multithreaded COM apartment and uninitializes it on exit.
        unsafe { CoInitializeEx(None, COINIT_MULTITHREADED) }.ok()?;
        Ok(Self)
    }
}

impl Drop for ComApartment {
    fn drop(&mut self) {
        // SAFETY: paired with the successful CoInitializeEx on this worker thread.
        unsafe { CoUninitialize() };
    }
}

struct SessionBinding {
    session: GlobalSystemMediaTransportControlsSession,
    media_properties_token: i64,
    playback_info_token: i64,
}

impl SessionBinding {
    fn bind(
        session: GlobalSystemMediaTransportControlsSession,
        sender: Sender<ProviderSignal>,
    ) -> windows::core::Result<Self> {
        let media_sender = sender.clone();
        let media_properties_token =
            session.MediaPropertiesChanged(&TypedEventHandler::new(move |_, _| {
                let _ = media_sender.send(ProviderSignal::Refresh);
                Ok(())
            }))?;

        let playback_info_token =
            match session.PlaybackInfoChanged(&TypedEventHandler::new(move |_, _| {
                let _ = sender.send(ProviderSignal::Refresh);
                Ok(())
            })) {
                Ok(token) => token,
                Err(error) => {
                    let _ = session.RemoveMediaPropertiesChanged(media_properties_token);
                    return Err(error);
                }
            };

        Ok(Self {
            session,
            media_properties_token,
            playback_info_token,
        })
    }

    fn read_snapshot(&self) -> windows::core::Result<Option<MediaSnapshot>> {
        let status = self.session.GetPlaybackInfo()?.PlaybackStatus()?;
        let playback_state = match status {
            GlobalSystemMediaTransportControlsSessionPlaybackStatus::Playing => {
                PlaybackState::Playing
            }
            GlobalSystemMediaTransportControlsSessionPlaybackStatus::Paused => {
                PlaybackState::Paused
            }
            _ => return Ok(None),
        };

        let app_id = sanitize_text(
            self.session.SourceAppUserModelId()?.to_string(),
            APP_ID_LIMIT,
            UNKNOWN_APP,
        );
        let properties = self.session.TryGetMediaPropertiesAsync()?.get()?;
        let title = sanitize_text(properties.Title()?.to_string(), TITLE_LIMIT, UNKNOWN_TITLE);
        let artist = sanitize_text(
            properties.Artist()?.to_string(),
            ARTIST_LIMIT,
            UNKNOWN_ARTIST,
        );

        Ok(Some(MediaSnapshot {
            title,
            artist,
            playback_state,
            app_id,
        }))
    }
}

impl Drop for SessionBinding {
    fn drop(&mut self) {
        let _ = self
            .session
            .RemoveMediaPropertiesChanged(self.media_properties_token);
        let _ = self
            .session
            .RemovePlaybackInfoChanged(self.playback_info_token);
    }
}

struct ManagerBinding {
    manager: GlobalSystemMediaTransportControlsSessionManager,
    current_session_token: i64,
    sessions_token: i64,
    _apartment: ComApartment,
}

impl ManagerBinding {
    fn initialize(sender: Sender<ProviderSignal>) -> windows::core::Result<Self> {
        let apartment = ComApartment::initialize()?;
        let manager = GlobalSystemMediaTransportControlsSessionManager::RequestAsync()?.get()?;

        let current_sender = sender.clone();
        let current_session_token =
            manager.CurrentSessionChanged(&TypedEventHandler::new(move |_, _| {
                let _ = current_sender.send(ProviderSignal::Rebind);
                Ok(())
            }))?;

        let sessions_token = match manager.SessionsChanged(&TypedEventHandler::new(move |_, _| {
            let _ = sender.send(ProviderSignal::Rebind);
            Ok(())
        })) {
            Ok(token) => token,
            Err(error) => {
                let _ = manager.RemoveCurrentSessionChanged(current_session_token);
                return Err(error);
            }
        };

        Ok(Self {
            manager,
            current_session_token,
            sessions_token,
            _apartment: apartment,
        })
    }

    fn bind_current(
        &self,
        sender: Sender<ProviderSignal>,
    ) -> windows::core::Result<Option<SessionBinding>> {
        match self.manager.GetCurrentSession() {
            Ok(session) => SessionBinding::bind(session, sender).map(Some),
            Err(_) => {
                // GetCurrentSession returns an error when there is no current session. A
                // successful sessions query distinguishes that normal state from a stale or
                // unavailable manager, which must be rebuilt instead of silently cleared.
                self.manager.GetSessions()?;
                Ok(None)
            }
        }
    }

    fn ensure_healthy(&self) -> windows::core::Result<()> {
        self.manager.GetSessions().map(|_| ())
    }
}

impl Drop for ManagerBinding {
    fn drop(&mut self) {
        let _ = self
            .manager
            .RemoveCurrentSessionChanged(self.current_session_token);
        let _ = self.manager.RemoveSessionsChanged(self.sessions_token);
    }
}

pub fn start(app: AppHandle) -> Result<MediaProviderHandle, String> {
    let stop = Arc::new(AtomicBool::new(false));
    let worker_stop = Arc::clone(&stop);
    let current = Arc::new(Mutex::new(None));
    let worker_current = Arc::clone(&current);
    let worker = thread::Builder::new()
        .name("windows-media-provider".into())
        .spawn(move || run_worker(app, worker_stop, worker_current))
        .map_err(|error| format!("无法启动 Windows 媒体 Provider：{error}"))?;
    Ok(MediaProviderHandle {
        stop,
        current,
        worker: Mutex::new(Some(worker)),
    })
}

#[tauri::command]
pub(crate) fn get_windows_media_state(
    provider: State<'_, MediaProviderHandle>,
) -> Option<MediaEventPayload> {
    provider.current.lock().ok()?.clone()
}

fn run_worker(
    app: AppHandle,
    stop: Arc<AtomicBool>,
    current: Arc<Mutex<Option<MediaEventPayload>>>,
) {
    let mut last_emitted = None;
    let mut recovery = RecoveryBackoff::default();

    while !stop.load(Ordering::Acquire) {
        if !crate::settings::preferences(&app).media_enabled {
            let _ = refresh_and_emit(&app, &current, None, &mut last_emitted);
            wait_for_stop(&stop, Duration::from_millis(250));
            continue;
        }
        let (sender, receiver) = mpsc::channel();
        let manager = match ManagerBinding::initialize(sender.clone()) {
            Ok(manager) => manager,
            Err(_) => {
                let delay = recovery.next_delay();
                wait_for_stop(&stop, delay);
                continue;
            }
        };
        let mut session = match manager.bind_current(sender.clone()) {
            Ok(session) => session,
            Err(_) => {
                drop(manager);
                let delay = recovery.next_delay();
                wait_for_stop(&stop, delay);
                continue;
            }
        };
        if refresh_and_emit(&app, &current, session.as_ref(), &mut last_emitted).is_err() {
            drop(session);
            drop(manager);
            let delay = recovery.next_delay();
            wait_for_stop(&stop, delay);
            continue;
        }
        recovery.reset();
        let mut last_health_check = Instant::now();

        while !stop.load(Ordering::Acquire) {
            if !crate::settings::preferences(&app).media_enabled {
                break;
            }
            match receiver.recv_timeout(WORKER_WAIT) {
                Ok(first) => {
                    let signal = coalesce_signals(&receiver, first);
                    if signal == ProviderSignal::Rebind {
                        let next_session = match manager.bind_current(sender.clone()) {
                            Ok(session) => session,
                            Err(_) => break,
                        };
                        session = next_session;
                    }
                    if refresh_and_emit(&app, &current, session.as_ref(), &mut last_emitted)
                        .is_err()
                    {
                        break;
                    }
                    recovery.reset();
                }
                Err(RecvTimeoutError::Timeout) => {}
                Err(RecvTimeoutError::Disconnected) => break,
            }

            if last_health_check.elapsed() >= MANAGER_HEALTH_CHECK {
                if manager.ensure_healthy().is_err() {
                    break;
                }
                last_health_check = Instant::now();
            }
        }

        drop(session);
        drop(manager);
        if !stop.load(Ordering::Acquire) {
            let delay = recovery.next_delay();
            wait_for_stop(&stop, delay);
        }
    }
}

fn coalesce_signals(receiver: &Receiver<ProviderSignal>, first: ProviderSignal) -> ProviderSignal {
    let mut result = first;
    while let Ok(signal) = receiver.try_recv() {
        if signal == ProviderSignal::Rebind {
            result = ProviderSignal::Rebind;
        }
    }
    result
}

fn refresh_and_emit(
    app: &AppHandle,
    current: &Mutex<Option<MediaEventPayload>>,
    session: Option<&SessionBinding>,
    last_emitted: &mut Option<Option<MediaSnapshot>>,
) -> windows::core::Result<()> {
    let session = if crate::settings::preferences(app).media_enabled {
        session
    } else {
        None
    };
    let snapshot = match session {
        Some(bound) => bound.read_snapshot()?,
        None => None,
    };
    if !snapshot_changed(last_emitted, &snapshot) {
        return Ok(());
    }

    let timestamp_ms = timestamp_ms();
    let payload = match snapshot.clone() {
        Some(snapshot) => MediaEventPayload::update(snapshot, timestamp_ms),
        None => MediaEventPayload::clear(timestamp_ms),
    };
    if let Ok(mut cached) = current.lock() {
        *cached = Some(payload.clone());
    }
    let _ = app.emit(MEDIA_EVENT_NAME, payload);
    *last_emitted = Some(snapshot);
    Ok(())
}

fn snapshot_changed(
    last_emitted: &Option<Option<MediaSnapshot>>,
    snapshot: &Option<MediaSnapshot>,
) -> bool {
    last_emitted.as_ref() != Some(snapshot)
}

fn timestamp_ms() -> u64 {
    SystemTime::now()
        .duration_since(UNIX_EPOCH)
        .unwrap_or_default()
        .as_millis()
        .min(u128::from(u64::MAX)) as u64
}

fn wait_for_stop(stop: &AtomicBool, duration: Duration) {
    let steps = duration.as_millis() / WORKER_WAIT.as_millis();
    for _ in 0..steps.max(1) {
        if stop.load(Ordering::Acquire) {
            break;
        }
        thread::sleep(WORKER_WAIT);
    }
}

fn sanitize_text(value: String, limit: usize, fallback: &str) -> String {
    let mut result = String::new();
    let mut pending_space = false;
    let mut length = 0;

    for character in value.chars() {
        if length >= limit {
            break;
        }
        if is_unsafe_format_character(character) {
            continue;
        }
        if character.is_control() || character.is_whitespace() {
            pending_space = !result.is_empty();
            continue;
        }
        if pending_space {
            if length + 1 >= limit {
                break;
            }
            result.push(' ');
            length += 1;
            pending_space = false;
        }
        result.push(character);
        length += 1;
    }

    if result.is_empty() {
        fallback.to_string()
    } else {
        result
    }
}

fn is_unsafe_format_character(character: char) -> bool {
    matches!(
        character,
        '\u{061c}'
            | '\u{200b}'
            | '\u{200e}'
            | '\u{200f}'
            | '\u{202a}'..='\u{202e}'
            | '\u{2060}'..='\u{2069}'
            | '\u{feff}'
    )
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn normalizes_unicode_controls_whitespace_and_missing_values() {
        assert_eq!(
            sanitize_text("  你好\n🎵\u{202e}  world\0".into(), 160, UNKNOWN_TITLE),
            "你好 🎵 world"
        );
        assert_eq!(
            sanitize_text("\r\n\0".into(), 160, UNKNOWN_TITLE),
            UNKNOWN_TITLE
        );
        assert_eq!(
            sanitize_text(String::new(), 120, UNKNOWN_ARTIST),
            UNKNOWN_ARTIST
        );
    }

    #[test]
    fn truncates_by_unicode_scalar_without_splitting_utf8() {
        assert_eq!(
            sanitize_text("甲乙丙🎵丁".into(), 4, UNKNOWN_TITLE),
            "甲乙丙🎵"
        );
    }

    #[test]
    fn payload_has_fixed_identity_flat_metadata_and_timestamp() {
        let payload = MediaEventPayload::update(
            MediaSnapshot {
                title: "标题".into(),
                artist: "艺术家".into(),
                playback_state: PlaybackState::Paused,
                app_id: "test.app".into(),
            },
            1_725_000_000_123,
        );
        assert_eq!(
            payload,
            MediaEventPayload::Update {
                title: "标题".into(),
                artist: "艺术家".into(),
                playback_state: PlaybackState::Paused,
                app_id: "test.app".into(),
                source: MEDIA_SOURCE,
                key: MEDIA_KEY,
                event_id: MEDIA_EVENT_ID,
                timestamp_ms: 1_725_000_000_123,
            }
        );
        assert_eq!(
            serde_json::to_value(payload).unwrap(),
            serde_json::json!({
                "kind": "update",
                "title": "标题",
                "artist": "艺术家",
                "playbackState": "paused",
                "appId": "test.app",
                "source": MEDIA_SOURCE,
                "key": MEDIA_KEY,
                "eventId": MEDIA_EVENT_ID,
                "timestampMs": 1_725_000_000_123_u64,
            })
        );
    }

    #[test]
    fn coalescing_never_loses_a_rebind_request() {
        let (sender, receiver) = mpsc::channel();
        sender.send(ProviderSignal::Refresh).unwrap();
        sender.send(ProviderSignal::Rebind).unwrap();
        sender.send(ProviderSignal::Refresh).unwrap();
        let first = receiver.recv().unwrap();
        assert_eq!(coalesce_signals(&receiver, first), ProviderSignal::Rebind);
    }

    #[test]
    fn duplicate_snapshots_are_suppressed_but_state_and_clear_changes_emit() {
        let playing = MediaSnapshot {
            title: "标题".into(),
            artist: "艺术家".into(),
            playback_state: PlaybackState::Playing,
            app_id: "test.app".into(),
        };
        let paused = MediaSnapshot {
            playback_state: PlaybackState::Paused,
            ..playing.clone()
        };

        assert!(snapshot_changed(&None, &None));
        assert!(!snapshot_changed(&Some(None), &None));
        assert!(!snapshot_changed(
            &Some(Some(playing.clone())),
            &Some(playing.clone())
        ));
        assert!(snapshot_changed(
            &Some(Some(playing.clone())),
            &Some(paused)
        ));
        assert!(snapshot_changed(&Some(Some(playing)), &None));
    }

    #[test]
    fn recovery_backoff_is_low_frequency_capped_and_resettable() {
        let mut recovery = RecoveryBackoff::default();

        assert_eq!(recovery.next_delay(), Duration::from_secs(1));
        assert_eq!(recovery.next_delay(), Duration::from_secs(2));
        assert_eq!(recovery.next_delay(), Duration::from_secs(5));
        assert_eq!(recovery.next_delay(), Duration::from_secs(5));

        recovery.reset();
        assert_eq!(recovery.next_delay(), Duration::from_secs(1));
    }

    #[test]
    fn provider_drop_waits_until_worker_observes_stop() {
        let stop = Arc::new(AtomicBool::new(false));
        let worker_stop = Arc::clone(&stop);
        let worker_finished = Arc::new(AtomicBool::new(false));
        let worker_finished_signal = Arc::clone(&worker_finished);
        let worker = thread::spawn(move || {
            while !worker_stop.load(Ordering::Acquire) {
                thread::sleep(Duration::from_millis(5));
            }
            worker_finished_signal.store(true, Ordering::Release);
        });

        let provider = MediaProviderHandle {
            stop,
            current: Arc::new(Mutex::new(None)),
            worker: Mutex::new(Some(worker)),
        };
        drop(provider);

        assert!(worker_finished.load(Ordering::Acquire));
    }

    #[test]
    fn manager_health_check_remains_low_frequency() {
        assert!(MANAGER_HEALTH_CHECK >= Duration::from_secs(5));
    }
}
