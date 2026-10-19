using System.Text.Json;

namespace ScreenGuide.DesktopClient.Services;

/// <summary>Local installation-independent locations. No secrets or executable routes.</summary>
public sealed record RuntimeDataProfile(
    int Version,
    string DataDirectory,
    string PipeName,
    string? VoiceModelDirectory = null,
    Dictionary<string, string>? ApplicationEnvironment = null,
    string? TemporaryDirectory = null)
{
    public static string CurrentUserPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "Yuanshu", "runtime-profile.json");

    public static void ApplyForCurrentProcess()
    {
        // Explicit development/test launches must never inherit the user's real data.
        if (!ShouldApplyProfile(Environment.GetEnvironmentVariable("SCREEN_GUIDE_DATA_DIRECTORY"),
                Environment.GetEnvironmentVariable("SCREEN_GUIDE_PIPE_NAME"),
                Environment.GetEnvironmentVariable("SCREEN_GUIDE_DATA_DIRECTORY", EnvironmentVariableTarget.User),
                Environment.GetEnvironmentVariable("SCREEN_GUIDE_DATA_DIRECTORY", EnvironmentVariableTarget.Machine))) return;
        if (!File.Exists(CurrentUserPath)) return;
        var profile = JsonSerializer.Deserialize<RuntimeDataProfile>(File.ReadAllText(CurrentUserPath),
            new JsonSerializerOptions(JsonSerializerDefaults.Web))
            ?? throw new InvalidDataException("元枢数据位置配置为空，请恢复原配置后重试。");
        var values = profile.ValidateAndGetEnvironment();
        if (!Directory.Exists(profile.DataDirectory))
            throw new DirectoryNotFoundException("原元枢数据目录不可用；请连接原磁盘或恢复数据位置配置。没有新建空数据库。");
        if (profile.TemporaryDirectory is { Length: > 0 }) Directory.CreateDirectory(profile.TemporaryDirectory);
        foreach (var (name, value) in values)
            if (name is "TEMP" or "TMP" or "SCREEN_GUIDE_DATA_DIRECTORY"
                || string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(name))
                || string.Equals(Environment.GetEnvironmentVariable(name),
                    Environment.GetEnvironmentVariable(name, EnvironmentVariableTarget.User), StringComparison.OrdinalIgnoreCase)
                || string.Equals(Environment.GetEnvironmentVariable(name),
                    Environment.GetEnvironmentVariable(name, EnvironmentVariableTarget.Machine), StringComparison.OrdinalIgnoreCase))
                Environment.SetEnvironmentVariable(name, value);
    }

    public static bool ShouldApplyProfile(string? processData, string? processPipe, string? userData, string? machineData) =>
        string.IsNullOrWhiteSpace(processPipe)
        && (string.IsNullOrWhiteSpace(processData)
            || string.Equals(processData, userData, StringComparison.OrdinalIgnoreCase)
            || string.Equals(processData, machineData, StringComparison.OrdinalIgnoreCase));

    public IReadOnlyDictionary<string, string> ValidateAndGetEnvironment()
    {
        if (Version != 1) throw new InvalidDataException("不支持的元枢数据位置配置版本。");
        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        ValidatePath(DataDirectory);
        if (string.IsNullOrWhiteSpace(PipeName) || PipeName.Length > 160
            || PipeName.Any(c => !(char.IsAsciiLetterOrDigit(c) || c is '.' or '_' or '-')))
            throw new InvalidDataException("元枢本机连接名称无效。");
        values["SCREEN_GUIDE_DATA_DIRECTORY"] = Path.GetFullPath(DataDirectory);
        values["SCREEN_GUIDE_PIPE_NAME"] = PipeName;
        if (!string.IsNullOrWhiteSpace(VoiceModelDirectory))
        {
            ValidatePath(VoiceModelDirectory);
            values["SCREEN_GUIDE_VOICE_MODEL_DIRECTORY"] = Path.GetFullPath(VoiceModelDirectory);
        }
        if (!string.IsNullOrWhiteSpace(TemporaryDirectory))
        {
            ValidatePath(TemporaryDirectory);
            values["TEMP"] = Path.GetFullPath(TemporaryDirectory);
            values["TMP"] = Path.GetFullPath(TemporaryDirectory);
        }
        foreach (var (name, value) in ApplicationEnvironment ?? [])
        {
            if (name is not ("BLENDER_USER_CONFIG" or "BLENDER_USER_SCRIPTS" or "BLENDER_USER_EXTENSIONS"))
                throw new InvalidDataException("数据位置配置包含不支持的应用环境设置。");
            ValidatePath(value);
            values[name] = Path.GetFullPath(value);
        }
        return values;
    }

    private static void ValidatePath(string value)
    {
        if (string.IsNullOrWhiteSpace(value) || !Path.IsPathFullyQualified(value)
            || value.StartsWith(@"\\", StringComparison.Ordinal)
            || value.Any(char.IsControl))
            throw new InvalidDataException("元枢数据位置必须是本机磁盘上的完整路径。");
    }
}
