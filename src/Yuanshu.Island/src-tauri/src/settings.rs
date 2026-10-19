use std::{fs, io::Write, path::PathBuf, sync::Mutex};

use serde::{Deserialize, Serialize};
use tauri::{AppHandle, Emitter, Manager};

pub const SETTINGS_EVENT: &str = "island-settings-changed";

// An opt-in isolated profile for side-by-side preview/acceptance. It can only select
// another pipe under the SAME current Windows user; it cannot redirect to a host/path.
pub fn profile() -> Result<Option<String>, String> {
    if let Some(value) =
        std::env::args().find_map(|argument| argument.strip_prefix("--profile=").map(str::to_owned))
    {
        return if valid_profile(&value) {
            Ok(Some(value))
        } else {
            Err("隔离配置名称无效。".into())
        };
    }
    match std::env::var("YUANSHU_ISLAND_PROFILE") {
        Ok(value) if valid_profile(&value) => Ok(Some(value)),
        Err(std::env::VarError::NotPresent) => Ok(None),
        _ => Err("隔离配置名称无效。".into()),
    }
}
fn valid_profile(value: &str) -> bool {
    !value.is_empty()
        && value.len() <= 40
        && value
            .bytes()
            .all(|b| b.is_ascii_lowercase() || b.is_ascii_digit() || b == b'-')
}

#[derive(Clone, Debug, PartialEq, Serialize, Deserialize)]
#[serde(rename_all = "camelCase", deny_unknown_fields)]
pub struct Preferences {
    pub schema_version: u32,
    pub volume_enabled: bool,
    pub media_enabled: bool,
    pub core_enabled: bool,
    pub hide_in_fullscreen: bool,
    pub idle_collapse: bool,
}

impl Default for Preferences {
    fn default() -> Self {
        Self {
            schema_version: 1,
            volume_enabled: true,
            media_enabled: true,
            core_enabled: true,
            hide_in_fullscreen: true,
            idle_collapse: true,
        }
    }
}

#[derive(Clone, Serialize)]
#[serde(rename_all = "camelCase")]
pub struct SettingsSnapshot {
    pub preferences: Preferences,
    pub warning: Option<String>,
}

pub struct SettingsStore {
    path: PathBuf,
    current: Mutex<SettingsSnapshot>,
}

impl SettingsStore {
    pub fn load(path: PathBuf) -> Self {
        let (preferences, warning) = match fs::read(&path) {
            Ok(bytes) => match decode(&bytes) {
                Ok(value) => (value, None),
                Err(_) => (
                    safe_defaults(),
                    Some(
                        "设置文件无法读取。已暂停数据来源；原文件保留，保存设置时会先备份。".into(),
                    ),
                ),
            },
            Err(error) if error.kind() == std::io::ErrorKind::NotFound => {
                (Preferences::default(), None)
            }
            Err(_) => (
                safe_defaults(),
                Some("无法读取设置。已暂停数据来源；请检查当前用户对配置目录的访问权限。".into()),
            ),
        };
        Self {
            path,
            current: Mutex::new(SettingsSnapshot {
                preferences,
                warning,
            }),
        }
    }

    pub fn snapshot(&self) -> SettingsSnapshot {
        self.current
            .lock()
            .map(|value| value.clone())
            .unwrap_or(SettingsSnapshot {
                preferences: safe_defaults(),
                warning: Some("设置暂时不可用，请退出后重新打开。".into()),
            })
    }

    fn save(&self, preferences: Preferences) -> Result<SettingsSnapshot, String> {
        if preferences.schema_version != 1 {
            return Err("设置版本不受支持，请使用匹配的程序版本。".into());
        }
        let mut guard = self
            .current
            .lock()
            .map_err(|_| "设置暂时不可用，请重试。")?;
        let parent = self.path.parent().ok_or("配置目录不可用。")?;
        fs::create_dir_all(parent).map_err(|_| "无法创建配置目录，请检查当前用户权限。")?;
        // Packaged Windows launchers may redirect AppData to another drive. Resolve
        // the existing parent once so both sides of the atomic rename use the same
        // filesystem path, including when the destination does not yet exist.
        let destination = fs::canonicalize(parent)
            .map_err(|_| "无法访问配置目录，请检查当前用户权限。")?
            .join(self.path.file_name().ok_or("设置文件名不可用。")?);
        if guard.warning.is_some() && self.path.exists() {
            // Never overwrite a damaged/newer settings file without preserving it.
            let stamp = std::time::SystemTime::now()
                .duration_since(std::time::UNIX_EPOCH)
                .map_err(|_| "系统时间不可用。")?
                .as_nanos();
            let backup = self.path.with_extension(format!("backup-{stamp}.json"));
            fs::copy(&self.path, backup).map_err(|_| "无法备份原设置，未进行覆盖。")?;
        }
        let bytes = serde_json::to_vec_pretty(&preferences).map_err(|_| "无法编码设置。")?;
        let temporary = destination.with_extension("pending");
        let mut file =
            fs::File::create(&temporary).map_err(|_| "无法保存设置，请检查配置目录权限。")?;
        file.write_all(&bytes)
            .and_then(|()| file.sync_all())
            .map_err(|_| "设置写入失败，原设置仍然有效。")?;
        drop(file);
        fs::rename(&temporary, &destination).map_err(|error| {
            format!(
                "无法替换设置文件，原设置仍然有效。（系统错误 {}）",
                error.raw_os_error().unwrap_or_default()
            )
        })?;
        *guard = SettingsSnapshot {
            preferences,
            warning: None,
        };
        Ok(guard.clone())
    }
}

fn safe_defaults() -> Preferences {
    Preferences {
        volume_enabled: false,
        media_enabled: false,
        core_enabled: false,
        ..Preferences::default()
    }
}

fn decode(bytes: &[u8]) -> Result<Preferences, String> {
    if bytes.len() > 16 * 1024 {
        return Err("settings_too_large".into());
    }
    let value: Preferences = serde_json::from_slice(bytes).map_err(|_| "settings_invalid")?;
    if value.schema_version != 1 {
        return Err("settings_version_unsupported".into());
    }
    Ok(value)
}

pub fn preferences(app: &AppHandle) -> Preferences {
    app.try_state::<SettingsStore>()
        .map(|store| store.snapshot().preferences)
        .unwrap_or_else(safe_defaults)
}

#[tauri::command]
pub fn get_island_settings(store: tauri::State<'_, SettingsStore>) -> SettingsSnapshot {
    store.snapshot()
}

#[tauri::command]
pub fn save_island_settings(
    app: AppHandle,
    store: tauri::State<'_, SettingsStore>,
    preferences: Preferences,
) -> Result<SettingsSnapshot, String> {
    let saved = store.save(preferences)?;
    let _ = app.emit(SETTINGS_EVENT, &saved);
    Ok(saved)
}

#[derive(Serialize)]
#[serde(rename_all = "camelCase")]
pub struct StartupStatus {
    enabled: bool,
    managed_by_parent: bool,
}

#[tauri::command]
pub fn get_island_autostart() -> StartupStatus {
    StartupStatus {
        enabled: false,
        managed_by_parent: true,
    }
}

#[tauri::command]
pub fn set_island_autostart(enabled: bool) -> Result<StartupStatus, String> {
    crate::embedded_startup::set_enabled(enabled)?;
    Ok(get_island_autostart())
}

#[tauri::command]
pub fn open_island_panel(app: AppHandle) -> Result<(), String> {
    let window = app
        .get_webview_window("panel")
        .ok_or("设置窗口不可用，请重新打开程序。")?;
    window
        .show()
        .and_then(|()| window.unminimize())
        .and_then(|()| window.set_focus())
        .map_err(|_| "无法打开面板，请从托盘重试。".into())
}

#[tauri::command]
pub fn set_island_visible(app: AppHandle, visible: bool) -> Result<(), String> {
    if visible {
        crate::presence::request_show(&app, false)
    } else {
        crate::presence::request_hide(&app)
    }
}

#[cfg(test)]
mod tests {
    use super::*;
    fn path(name: &str) -> PathBuf {
        std::env::temp_dir()
            .join(format!(
                "island-settings-{}-{}-{name}",
                std::process::id(),
                std::time::SystemTime::now()
                    .duration_since(std::time::UNIX_EPOCH)
                    .unwrap()
                    .as_nanos()
            ))
            .join("settings.json")
    }
    #[test]
    fn preferences_survive_restart_and_repeated_atomic_replacement() {
        let path = path("roundtrip");
        let store = SettingsStore::load(path.clone());
        let mut prefs = Preferences {
            core_enabled: false,
            ..Preferences::default()
        };
        store.save(prefs.clone()).unwrap();
        prefs.media_enabled = false;
        store.save(prefs.clone()).unwrap();
        assert_eq!(
            SettingsStore::load(path.clone()).snapshot().preferences,
            prefs
        );
        fs::remove_dir_all(path.parent().unwrap()).unwrap();
    }
    #[test]
    fn appdata_roundtrip_handles_packaged_launcher_redirection() {
        let Some(appdata) = std::env::var_os("APPDATA") else {
            return;
        };
        let unique = path("appdata");
        let root = PathBuf::from(appdata)
            .join("com.yuanshu.island")
            .join("test-profiles")
            .join(unique.parent().unwrap().file_name().unwrap());
        let path = root.join("settings.json");
        let store = SettingsStore::load(path.clone());
        let preferences = Preferences {
            core_enabled: false,
            ..Preferences::default()
        };
        store.save(preferences.clone()).unwrap();
        store.save(preferences.clone()).unwrap();
        assert_eq!(
            SettingsStore::load(path.clone()).snapshot().preferences,
            preferences
        );
        // Only remove the two exact files/empty directory created by this test.
        fs::remove_file(path).unwrap();
        fs::remove_dir(root).unwrap();
    }
    #[test]
    fn corrupt_settings_disable_sources_and_are_preserved_on_explicit_save() {
        let path = path("corrupt");
        fs::create_dir_all(path.parent().unwrap()).unwrap();
        fs::write(&path, b"broken").unwrap();
        let store = SettingsStore::load(path.clone());
        assert!(store.snapshot().warning.is_some());
        assert!(!store.snapshot().preferences.core_enabled);
        assert_eq!(fs::read(&path).unwrap(), b"broken");
        store.save(Preferences::default()).unwrap();
        let backup = fs::read_dir(path.parent().unwrap())
            .unwrap()
            .flatten()
            .find(|entry| entry.file_name().to_string_lossy().contains("backup-"))
            .unwrap();
        assert_eq!(fs::read(backup.path()).unwrap(), b"broken");
        fs::remove_dir_all(path.parent().unwrap()).unwrap();
    }
    #[test]
    fn failed_save_does_not_change_runtime_preferences() {
        let path = path("failure");
        fs::create_dir_all(path.parent().unwrap()).unwrap();
        fs::create_dir(&path).unwrap();
        let store = SettingsStore::load(path.clone());
        let before = store.snapshot().preferences;
        assert!(store.save(Preferences::default()).is_err());
        assert_eq!(store.snapshot().preferences, before);
        fs::remove_dir_all(path.parent().unwrap()).unwrap();
    }
    #[test]
    fn rejects_unknown_fields_and_future_versions() {
        let mut value = serde_json::to_value(Preferences::default()).unwrap();
        value["schemaVersion"] = 2.into();
        assert!(decode(&serde_json::to_vec(&value).unwrap()).is_err());
        value["schemaVersion"] = 1.into();
        value["token"] = "synthetic".into();
        assert!(decode(&serde_json::to_vec(&value).unwrap()).is_err());
    }
}
