using ScreenGuide.DesktopClient.Services;

namespace ScreenGuide.DesktopClient.Tests;

public sealed class DesktopLaunchOptionsTests
{
    [Theory]
    [InlineData("", true, false, false)]
    [InlineData("--assistant", true, true, false)]
    [InlineData("--assistant --paused", true, false, false)]
    [InlineData("--show", false, false, false)]
    [InlineData("--background", false, false, true)]
    [InlineData("--background --assistant", false, false, true)]
    [InlineData("--background --show", false, false, true)]
    [InlineData("--ASSISTANT --PAUSED", true, false, false)]
    public void StartupHasOneVisibleEntryAndBackgroundNeverListens(
        string args, bool assistant, bool listening, bool background)
    {
        var options = DesktopLaunchOptions.Parse(args.Split(' ', StringSplitOptions.RemoveEmptyEntries));
        Assert.Equal(assistant, options.ShowAssistant);
        Assert.Equal(listening, options.StartListening);
        Assert.Equal(background, options.Background);
    }

    [Theory]
    [InlineData("--island", true, false)]
    [InlineData("--island --paused", true, false)]
    [InlineData("--island --background", true, false)]
    [InlineData("--show --island", true, false)]
    [InlineData("", true, false)]
    [InlineData("--assistant", true, true)]
    public void IslandIsBuiltInAndOnlyExplicitVoiceEntryListens(string args, bool island, bool listening)
    {
        var options = DesktopLaunchOptions.Parse(args.Split(' ', StringSplitOptions.RemoveEmptyEntries));
        Assert.Equal(island, options.UseIsland);
        Assert.Equal(listening, options.StartListening);
    }
}
