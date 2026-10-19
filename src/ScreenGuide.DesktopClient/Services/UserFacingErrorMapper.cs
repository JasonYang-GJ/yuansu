using ScreenGuide.DesktopProtocol;

namespace ScreenGuide.DesktopClient.Services;

public sealed record UserFacingError(string Message, string SuggestedAction, string TechnicalDetail);

public static class UserFacingErrorMapper
{
    public static UserFacingError Map(Exception exception)
    {
        if (exception is DesktopHostStartupException startup)
        {
            var action = startup.Code == "database_unavailable"
                ? "关闭程序，备份数据目录后查看日志；程序不会自动删除任务历史。"
                : "查看技术详情与日志目录后重新启动。";
            return new UserFacingError(
                startup.UserMessage,
                action,
                SensitiveDataSanitizer.DiagnosticCode(startup.Code, "host_startup_failed"));
        }

        if (exception is DesktopApiException api)
        {
            var action = api.Error.Code switch
            {
                "codex_not_found" => "安装并登录 Codex 后，在设置中重新检查。",
                "codex_version_incompatible" => "安装经过兼容验证的 Codex CLI 0.147.0。",
                "codex_login_required" => "打开 Codex 完成登录，然后重试任务。",
                "network_unavailable" => "检查网络连接后重试。",
                "project_missing" => "重新选择存在的 Git 项目目录。",
                "project_not_git" => "选择包含 Git 仓库的项目目录。",
                "project_not_authorized" => "在项目页面重新授权该目录。",
                "file_missing" => "重新选择这次要打开的文件。",
                "desktop_action_not_authorized" => "重新选择受控清单中的目标，并在确认窗口中明确同意本次操作。",
                "desktop_target_unavailable" => "切回目标软件，确认其中只有一个明确搜索框后重试。",
                "action_plan_expired" => "为防止误操作，元枢不会自动重放旧指令。",
                "input_invalid" => "检查输入内容；网站地址必须以 https:// 开头。",
                "database_unavailable" => "关闭程序并备份数据目录，再查看日志中的数据库错误。",
                "codex_exited" => "打开任务查看已保存的修改证据，不会自动重新执行。",
                _ => "检查当前状态后重试；需要排查时查看技术详情。"
            };
            return new UserFacingError(
                api.Error.UserMessage,
                action,
                SensitiveDataSanitizer.DiagnosticCode(api.Error.Code, "host_error"));
        }

        return exception switch
        {
            VoiceSubmissionUnconfirmedException => new UserFacingError(
                "这条请求尚未收到确认，不能确认是否已停止。",
                "请查看当前任务并重试停止；程序不会重发这条请求。",
                SensitiveDataSanitizer.ExceptionType(exception)),
            TimeoutException => new UserFacingError(
                "本机中枢没有响应。",
                "等待几秒后重试，程序会自动尝试恢复连接。",
                SensitiveDataSanitizer.ExceptionType(exception)),
            IOException => new UserFacingError(
                "本机中枢当前离线。",
                "保持窗口打开，服务恢复后会自动重新连接。",
                SensitiveDataSanitizer.ExceptionType(exception)),
            _ => new UserFacingError(
                "操作没有完成。",
                "查看技术详情或打开日志目录。",
                SensitiveDataSanitizer.ExceptionType(exception))
        };
    }
}
