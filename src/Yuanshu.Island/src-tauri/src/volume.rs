use std::{
    sync::{
        atomic::{AtomicBool, Ordering},
        mpsc::{self, Receiver, RecvTimeoutError, Sender},
        Arc,
    },
    thread,
    time::{Duration, Instant, SystemTime, UNIX_EPOCH},
};

use serde::Serialize;
use tauri::{AppHandle, Emitter};
use windows::{
    core::{implement, PCWSTR},
    Win32::{
        Foundation::PROPERTYKEY,
        Media::Audio::Endpoints::{
            IAudioEndpointVolume, IAudioEndpointVolumeCallback, IAudioEndpointVolumeCallback_Impl,
        },
        Media::Audio::{
            eMultimedia, eRender, EDataFlow, ERole, IMMDevice, IMMDeviceEnumerator,
            IMMNotificationClient, IMMNotificationClient_Impl, MMDeviceEnumerator,
            AUDIO_VOLUME_NOTIFICATION_DATA,
        },
        System::Com::{
            CoCreateInstance, CoInitializeEx, CoTaskMemFree, CoUninitialize, CLSCTX_ALL,
            COINIT_MULTITHREADED,
        },
    },
};

pub const VOLUME_EVENT_NAME: &str = "windows-volume-changed";
const VOLUME_SOURCE: &str = "windows.volume";
const VOLUME_KEY: &str = "default-playback";
const VOLUME_EVENT_ID: &str = "windows.volume.default-playback";
const WORKER_POLL_INTERVAL: Duration = Duration::from_millis(250);
const DEVICE_HEALTH_INTERVAL: Duration = Duration::from_secs(2);
const STARTUP_TIMEOUT: Duration = Duration::from_secs(5);

pub struct VolumeProviderHandle {
    stop: Arc<AtomicBool>,
}

impl Drop for VolumeProviderHandle {
    fn drop(&mut self) {
        self.stop.store(true, Ordering::Release);
    }
}

#[derive(Clone, Copy, Debug, PartialEq)]
struct VolumeReading {
    scalar: f32,
    muted: bool,
}

impl VolumeReading {
    fn new(scalar: f32, muted: bool) -> Option<Self> {
        scalar.is_finite().then_some(Self {
            scalar: scalar.clamp(0.0, 1.0),
            muted,
        })
    }

    fn percent(self) -> u8 {
        (self.scalar * 100.0).round() as u8
    }
}

#[derive(Debug)]
enum ProviderSignal {
    Volume(VolumeReading),
    RebindDefaultDevice,
}

#[derive(Clone, Debug, PartialEq, Eq, Serialize)]
#[serde(rename_all = "camelCase")]
struct VolumeEventPayload {
    volume: u8,
    muted: bool,
    source: &'static str,
    key: &'static str,
    event_id: &'static str,
    timestamp_ms: u64,
}

impl VolumeEventPayload {
    fn from_reading(reading: VolumeReading, timestamp_ms: u64) -> Self {
        Self {
            volume: reading.percent(),
            muted: reading.muted,
            source: VOLUME_SOURCE,
            key: VOLUME_KEY,
            event_id: VOLUME_EVENT_ID,
            timestamp_ms,
        }
    }
}

#[implement(IAudioEndpointVolumeCallback)]
struct EndpointVolumeCallback {
    sender: Sender<ProviderSignal>,
}

impl IAudioEndpointVolumeCallback_Impl for EndpointVolumeCallback_Impl {
    fn OnNotify(
        &self,
        notification: *mut AUDIO_VOLUME_NOTIFICATION_DATA,
    ) -> windows::core::Result<()> {
        if notification.is_null() {
            return Ok(());
        }

        // SAFETY: Core Audio owns this notification for the duration of OnNotify.
        // Only the two fixed-size master fields are copied before returning.
        let notification = unsafe { &*notification };
        if let Some(reading) =
            VolumeReading::new(notification.fMasterVolume, notification.bMuted.as_bool())
        {
            let _ = self.sender.send(ProviderSignal::Volume(reading));
        }
        Ok(())
    }
}

#[implement(IMMNotificationClient)]
struct DeviceNotificationCallback {
    sender: Sender<ProviderSignal>,
}

impl IMMNotificationClient_Impl for DeviceNotificationCallback_Impl {
    fn OnDeviceStateChanged(
        &self,
        _device_id: &PCWSTR,
        _new_state: windows::Win32::Media::Audio::DEVICE_STATE,
    ) -> windows::core::Result<()> {
        Ok(())
    }

    fn OnDeviceAdded(&self, _device_id: &PCWSTR) -> windows::core::Result<()> {
        Ok(())
    }

    fn OnDeviceRemoved(&self, _device_id: &PCWSTR) -> windows::core::Result<()> {
        Ok(())
    }

    fn OnDefaultDeviceChanged(
        &self,
        flow: EDataFlow,
        role: ERole,
        _default_device_id: &PCWSTR,
    ) -> windows::core::Result<()> {
        if flow == eRender && role == eMultimedia {
            let _ = self.sender.send(ProviderSignal::RebindDefaultDevice);
        }
        Ok(())
    }

    fn OnPropertyValueChanged(
        &self,
        _device_id: &PCWSTR,
        _key: &PROPERTYKEY,
    ) -> windows::core::Result<()> {
        Ok(())
    }
}

struct ComApartment;

impl ComApartment {
    fn initialize() -> Result<Self, String> {
        // SAFETY: this worker is dedicated to Core Audio and uninitializes COM on the same thread.
        unsafe { CoInitializeEx(None, COINIT_MULTITHREADED) }
            .ok()
            .map_err(|error| format!("无法初始化 Core Audio COM 线程：{error}"))?;
        Ok(Self)
    }
}

impl Drop for ComApartment {
    fn drop(&mut self) {
        // SAFETY: paired with the successful CoInitializeEx on this same worker thread.
        unsafe { CoUninitialize() };
    }
}

struct AudioSession {
    _apartment: ComApartment,
    enumerator: IMMDeviceEnumerator,
    notification_callback: IMMNotificationClient,
}

impl AudioSession {
    fn initialize(sender: Sender<ProviderSignal>) -> Result<Self, String> {
        let apartment = ComApartment::initialize()?;
        // SAFETY: COM is initialized on this worker; MMDeviceEnumerator is an in-process COM class.
        let enumerator = unsafe {
            CoCreateInstance::<_, IMMDeviceEnumerator>(&MMDeviceEnumerator, None, CLSCTX_ALL)
        }
        .map_err(|error| format!("无法创建 Windows 音频设备枚举器：{error}"))?;
        let notification_callback: IMMNotificationClient =
            DeviceNotificationCallback { sender }.into();

        // SAFETY: both COM interfaces stay alive in AudioSession until explicit unregistration.
        unsafe { enumerator.RegisterEndpointNotificationCallback(&notification_callback) }
            .map_err(|error| format!("无法监听 Windows 默认播放设备切换：{error}"))?;

        Ok(Self {
            _apartment: apartment,
            enumerator,
            notification_callback,
        })
    }
}

impl Drop for AudioSession {
    fn drop(&mut self) {
        // SAFETY: the callback was registered on this enumerator and both remain alive here.
        let _ = unsafe {
            self.enumerator
                .UnregisterEndpointNotificationCallback(&self.notification_callback)
        };
    }
}

struct BoundEndpoint {
    device_id: String,
    volume: IAudioEndpointVolume,
    callback: IAudioEndpointVolumeCallback,
}

impl BoundEndpoint {
    fn bind(
        enumerator: &IMMDeviceEnumerator,
        sender: Sender<ProviderSignal>,
    ) -> Result<Self, String> {
        // SAFETY: the enumerator is live on its initialized COM worker thread.
        let device = unsafe { enumerator.GetDefaultAudioEndpoint(eRender, eMultimedia) }
            .map_err(|error| format!("无法获取 Windows 默认播放设备：{error}"))?;
        let device_id = read_device_id(&device)?;
        // SAFETY: activation requests the documented endpoint master-volume interface.
        let volume: IAudioEndpointVolume = unsafe { device.Activate(CLSCTX_ALL, None) }
            .map_err(|error| format!("无法绑定默认播放设备主音量：{error}"))?;
        let callback: IAudioEndpointVolumeCallback = EndpointVolumeCallback { sender }.into();

        // SAFETY: callback and endpoint are retained together until explicit unregistration.
        unsafe { volume.RegisterControlChangeNotify(&callback) }
            .map_err(|error| format!("无法注册默认播放设备音量回调：{error}"))?;

        Ok(Self {
            device_id,
            volume,
            callback,
        })
    }

    fn current_reading(&self) -> Result<VolumeReading, String> {
        // SAFETY: the endpoint remains bound and all out parameters are managed by windows-rs.
        let scalar = unsafe { self.volume.GetMasterVolumeLevelScalar() }
            .map_err(|error| format!("无法读取默认播放设备音量：{error}"))?;
        // SAFETY: same live endpoint as above.
        let muted = unsafe { self.volume.GetMute() }
            .map_err(|error| format!("无法读取默认播放设备静音状态：{error}"))?
            .as_bool();
        VolumeReading::new(scalar, muted)
            .ok_or_else(|| "Windows 返回了无效的默认播放设备音量".to_string())
    }

    fn still_default(&self, enumerator: &IMMDeviceEnumerator) -> Result<bool, String> {
        // SAFETY: this read-only probe runs on the owning COM worker thread.
        let current: IMMDevice =
            unsafe { enumerator.GetDefaultAudioEndpoint(eRender, eMultimedia) }
                .map_err(|error| format!("无法复核 Windows 默认播放设备：{error}"))?;
        Ok(self.device_id == read_device_id(&current)?)
    }
}

fn read_device_id(device: &IMMDevice) -> Result<String, String> {
    // SAFETY: IMMDevice::GetId allocates a null-terminated string with CoTaskMemAlloc.
    let raw_id =
        unsafe { device.GetId() }.map_err(|error| format!("无法读取默认播放设备 ID：{error}"))?;
    // SAFETY: raw_id remains valid until the paired CoTaskMemFree immediately below.
    let converted = unsafe { raw_id.to_string() };
    // SAFETY: this is the exact pointer returned by IMMDevice::GetId.
    unsafe { CoTaskMemFree(Some(raw_id.0.cast())) };
    converted.map_err(|error| format!("默认播放设备 ID 不是有效的 UTF-16：{error}"))
}

impl Drop for BoundEndpoint {
    fn drop(&mut self) {
        // SAFETY: this is the exact callback registered on this live endpoint.
        let _ = unsafe { self.volume.UnregisterControlChangeNotify(&self.callback) };
    }
}

pub fn start(app: AppHandle) -> Result<VolumeProviderHandle, String> {
    let stop = Arc::new(AtomicBool::new(false));
    let worker_stop = Arc::clone(&stop);
    let (startup_sender, startup_receiver) = mpsc::sync_channel(1);

    thread::Builder::new()
        .name("windows-volume-provider".into())
        .spawn(move || run_worker(app, worker_stop, startup_sender))
        .map_err(|error| format!("无法启动 Windows 音量 Provider：{error}"))?;

    startup_receiver
        .recv_timeout(STARTUP_TIMEOUT)
        .map_err(|error| format!("Windows 音量 Provider 启动未完成：{error}"))??;

    Ok(VolumeProviderHandle { stop })
}

fn run_worker(
    app: AppHandle,
    stop: Arc<AtomicBool>,
    startup_sender: mpsc::SyncSender<Result<(), String>>,
) {
    let (signal_sender, signal_receiver) = mpsc::channel();
    let session = match AudioSession::initialize(signal_sender.clone()) {
        Ok(session) => session,
        Err(error) => {
            let _ = startup_sender.send(Err(error));
            return;
        }
    };

    let mut endpoint = if crate::settings::preferences(&app).volume_enabled {
        BoundEndpoint::bind(&session.enumerator, signal_sender.clone()).ok()
    } else {
        None
    };
    let _ = startup_sender.send(Ok(()));
    let mut emit_after_rebind = false;
    let mut last_health_check = Instant::now();

    while !stop.load(Ordering::Acquire) {
        if !crate::settings::preferences(&app).volume_enabled {
            endpoint = None;
            while signal_receiver.try_recv().is_ok() {}
            thread::sleep(WORKER_POLL_INTERVAL);
            emit_after_rebind = true;
            continue;
        }
        match signal_receiver.recv_timeout(WORKER_POLL_INTERVAL) {
            Ok(ProviderSignal::Volume(reading)) => {
                emit_latest_volume(&app, &signal_receiver, reading, &mut emit_after_rebind);
            }
            Ok(ProviderSignal::RebindDefaultDevice) => {
                endpoint = None;
                emit_after_rebind = true;
            }
            Err(RecvTimeoutError::Timeout) => {}
            Err(RecvTimeoutError::Disconnected) => break,
        }

        if emit_after_rebind && endpoint.is_some() {
            endpoint = None;
        }

        if emit_after_rebind && endpoint.is_none() {
            if let Ok(bound) = BoundEndpoint::bind(&session.enumerator, signal_sender.clone()) {
                if let Ok(reading) = bound.current_reading() {
                    emit_volume(&app, reading);
                }
                endpoint = Some(bound);
                emit_after_rebind = false;
            }
        }

        if last_health_check.elapsed() >= DEVICE_HEALTH_INTERVAL {
            let still_default = endpoint
                .as_ref()
                .map(|bound| bound.still_default(&session.enumerator))
                .transpose();
            match still_default {
                Ok(Some(true)) => {}
                Ok(Some(false)) | Err(_) => {
                    endpoint = None;
                    emit_after_rebind = true;
                }
                Ok(None) => {
                    if let Ok(bound) =
                        BoundEndpoint::bind(&session.enumerator, signal_sender.clone())
                    {
                        if emit_after_rebind {
                            if let Ok(reading) = bound.current_reading() {
                                emit_volume(&app, reading);
                            }
                        }
                        endpoint = Some(bound);
                        emit_after_rebind = false;
                    }
                }
            }
            last_health_check = Instant::now();
        }
    }
}

fn emit_latest_volume(
    app: &AppHandle,
    receiver: &Receiver<ProviderSignal>,
    first: VolumeReading,
    rebind_requested: &mut bool,
) {
    let mut latest = first;
    while let Ok(signal) = receiver.try_recv() {
        match signal {
            ProviderSignal::Volume(reading) => latest = reading,
            ProviderSignal::RebindDefaultDevice => *rebind_requested = true,
        }
    }
    emit_volume(app, latest);
}

fn emit_volume(app: &AppHandle, reading: VolumeReading) {
    if !crate::settings::preferences(app).volume_enabled {
        return;
    }
    let timestamp_ms = SystemTime::now()
        .duration_since(UNIX_EPOCH)
        .unwrap_or_default()
        .as_millis()
        .min(u128::from(u64::MAX)) as u64;
    let payload = VolumeEventPayload::from_reading(reading, timestamp_ms);
    let _ = app.emit(VOLUME_EVENT_NAME, payload);
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn normalizes_windows_scalar_to_zero_through_one_hundred() {
        assert_eq!(VolumeReading::new(-0.2, false).unwrap().percent(), 0);
        assert_eq!(VolumeReading::new(0.504, false).unwrap().percent(), 50);
        assert_eq!(VolumeReading::new(0.505, false).unwrap().percent(), 51);
        assert_eq!(VolumeReading::new(1.4, false).unwrap().percent(), 100);
        assert!(VolumeReading::new(f32::NAN, false).is_none());
    }

    #[test]
    fn payload_has_stable_identity_mute_and_timestamp() {
        let payload = VolumeEventPayload::from_reading(
            VolumeReading::new(0.42, true).unwrap(),
            1_725_000_000_123,
        );

        assert_eq!(
            payload,
            VolumeEventPayload {
                volume: 42,
                muted: true,
                source: "windows.volume",
                key: "default-playback",
                event_id: "windows.volume.default-playback",
                timestamp_ms: 1_725_000_000_123,
            }
        );
    }
}
