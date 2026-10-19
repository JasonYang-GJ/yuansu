namespace ScreenGuide.DesktopClient.Services;

public static class BuiltInIsland
{
    public static string ResolveExecutable(string baseDirectory, string? developmentOverride = null) =>
        string.IsNullOrWhiteSpace(developmentOverride)
            ? Path.Combine(baseDirectory, "island", "yuanshu-island.exe")
            : Path.GetFullPath(developmentOverride);
}
