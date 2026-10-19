using ScreenGuide.DesktopClient.Services;

namespace ScreenGuide.DesktopClient.Tests;

public sealed class RuntimeDataProfileTests
{
    [Theory]
    [InlineData(null, null, null, null, true)]
    [InlineData(@"E:\old", null, @"E:\old", null, true)]
    [InlineData(@"E:\old", null, null, @"E:\old", true)]
    [InlineData(@"E:\test", null, @"E:\old", null, false)]
    [InlineData(@"E:\old", "isolated-pipe", @"E:\old", null, false)]
    [InlineData(null, "isolated-pipe", null, null, false)]
    public void InstalledProfileSupersedesInheritedLegacyPathsButNotExplicitIsolation(
        string? processData, string? processPipe, string? userData, string? machineData, bool expected) =>
        Assert.Equal(expected, RuntimeDataProfile.ShouldApplyProfile(processData, processPipe, userData, machineData));

    [Fact]
    public void PreservesExistingLocationsWithoutReadingOrMovingTheirContents()
    {
        var profile = new RuntimeDataProfile(1, @"E:\existing\runtime", "ScreenGuide.Existing",
            @"E:\existing\models", new() { ["BLENDER_USER_CONFIG"] = @"E:\Blender\config" });
        var environment = profile.ValidateAndGetEnvironment();
        Assert.Equal(@"E:\existing\runtime", environment["SCREEN_GUIDE_DATA_DIRECTORY"]);
        Assert.Equal("ScreenGuide.Existing", environment["SCREEN_GUIDE_PIPE_NAME"]);
        Assert.Equal(@"E:\existing\models", environment["SCREEN_GUIDE_VOICE_MODEL_DIRECTORY"]);
        Assert.Equal(4, environment.Count);
    }

    [Theory]
    [InlineData("relative")]
    [InlineData(@"\\server\share")]
    [InlineData(@"\\?\C:\data")]
    [InlineData("")]
    public void RejectsNonLocalOrAmbiguousDataPaths(string path) =>
        Assert.Throws<InvalidDataException>(() => new RuntimeDataProfile(1, path, "test").ValidateAndGetEnvironment());

    [Fact]
    public void RejectsInvalidSchemaAndPipeNames()
    {
        Assert.Throws<InvalidDataException>(() => new RuntimeDataProfile(2, @"C:\data", "test").ValidateAndGetEnvironment());
        Assert.Throws<InvalidDataException>(() => new RuntimeDataProfile(1, @"C:\data", @"pipe\other").ValidateAndGetEnvironment());
    }

    [Theory]
    [InlineData("SCREEN_GUIDE_DESKTOP_HOST_PATH")]
    [InlineData("OPENAI_API_KEY")]
    [InlineData("PATH")]
    public void CannotSmuggleSecretsOrExecutionOverrides(string name) =>
        Assert.Throws<InvalidDataException>(() => new RuntimeDataProfile(1, @"C:\data", "test", null,
            new() { [name] = @"C:\untrusted" }).ValidateAndGetEnvironment());

    [Fact]
    public void BuiltInExecutableDoesNotDependOnAnotherCheckout() =>
        Assert.Equal(@"C:\Program Files\Yuanshu\island\yuanshu-island.exe",
            BuiltInIsland.ResolveExecutable(@"C:\Program Files\Yuanshu"));
}
