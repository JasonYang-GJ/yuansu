namespace ScreenGuide.DesktopClient.Services;

public sealed record DesktopLaunchOptions(bool ShowAssistant, bool StartListening, bool Background, bool UseIsland = true)
{
    public static DesktopLaunchOptions Parse(IEnumerable<string> arguments)
    {
        var args = new HashSet<string>(arguments, StringComparer.OrdinalIgnoreCase);
        // Background/autostart must never grant a microphone session, even with conflicting flags.
        if (args.Contains("--background")) return new(false, false, true);
        if (args.Contains("--show") && !args.Contains("--assistant")) return new(false, false, false);
        // Installation, ordinary double-click and autostart never grant microphone consent.
        // The previous explicit voice entry remains compatible; --island is now redundant.
        return new(true, args.Contains("--assistant") && !args.Contains("--paused"), false);
    }
}
