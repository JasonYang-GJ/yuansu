import { useEffect, useState } from "react";
import { invoke } from "@tauri-apps/api/core";
import { getCurrentWindow } from "@tauri-apps/api/window";

export interface Preferences {
  schemaVersion: 1;
  volumeEnabled: boolean;
  mediaEnabled: boolean;
  coreEnabled: boolean;
  hideInFullscreen: boolean;
  idleCollapse: boolean;
}
export interface SettingsSnapshot { preferences: Preferences; warning: string | null }
export const defaultPreferences: Preferences = {
  schemaVersion: 1, volumeEnabled: true, mediaEnabled: true, coreEnabled: true,
  hideInFullscreen: true, idleCollapse: true,
};
export function isSettings(value: unknown): value is SettingsSnapshot {
  if (!value || typeof value !== "object" || !("preferences" in value)) return false;
  const p = value.preferences as Partial<Preferences> | null;
  return !!p && p.schemaVersion === 1 &&
    [p.volumeEnabled, p.mediaEnabled, p.coreEnabled, p.hideInFullscreen, p.idleCollapse]
      .every((item) => typeof item === "boolean");
}
export function useSettings() {
  const [snapshot, setSnapshot] = useState<SettingsSnapshot>({ preferences: defaultPreferences, warning: null });
  const [loaded, setLoaded] = useState(false);
  useEffect(() => {
    let disposed = false;
    let stop: (() => void) | undefined;
    let revision = 0;
    void getCurrentWindow().listen<unknown>("island-settings-changed", ({ payload }) => {
      revision += 1;
      if (!disposed && isSettings(payload)) { setSnapshot(payload); setLoaded(true); }
    }).then((unlisten) => {
      if (disposed) { unlisten(); return; }
      stop = unlisten;
      const requested = revision;
      return invoke<unknown>("get_island_settings").then((value) => {
        if (!disposed && requested === revision && isSettings(value)) setSnapshot(value);
        if (!disposed) setLoaded(true);
      });
    }).catch(() => {
      if (!disposed) {
        setSnapshot({ preferences: { ...defaultPreferences, coreEnabled: false, mediaEnabled: false, volumeEnabled: false }, warning: "无法连接本机设置服务，请从桌面程序打开面板。" });
        setLoaded(true);
      }
    });
    return () => { disposed = true; stop?.(); };
  }, []);
  return { ...snapshot, loaded };
}
