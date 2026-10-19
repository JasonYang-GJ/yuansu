//! The embedded component must never register a separate Windows startup entry.
//! The Yuanshu installer owns migration of an explicitly enabled legacy entry.
pub fn set_enabled(_enabled: bool) -> Result<(), String> {
    Err("灵动岛随元枢一起启动。请在元枢或 Windows 启动应用设置中管理登录启动。".into())
}

#[cfg(test)]
mod tests {
    #[test]
    fn embedded_component_refuses_both_startup_mutations() {
        assert!(super::set_enabled(true).is_err());
        assert!(super::set_enabled(false).is_err());
    }
}
