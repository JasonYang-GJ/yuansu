using ScreenGuide.DesktopClient.Services;

namespace ScreenGuide.DesktopClient.Tests;

public sealed class VoiceCommandSegmentsTests
{
    [Theory]
    [InlineData("打开网易云，然后搜索", "最近周杰伦的新歌", "打开网易云，然后搜索最近周杰伦的新歌")]
    [InlineData("打开我的抖音", "然后搜索猫咪视频", "打开我的抖音然后搜索猫咪视频")]
    [InlineData("桌面上的网易云然后搜索", "周杰伦最新的歌", "桌面上的网易云然后搜索周杰伦最新的歌")]
    [InlineData("就是请帮我打开微信", "搜索文件传输助手", "就是请帮我打开微信搜索文件传输助手")]
    [InlineData("呃我见到桌面上的网易云然后搜索", "最近周杰伦的新歌", "呃我见到桌面上的网易云然后搜索最近周杰伦的新歌")]
    public void AShortConnectedClauseKeepsTheRecognizedPrefixVerbatim(string prefix, string suffix, string expected)
    {
        var clock = new ManualClock();
        var segments = new VoiceCommandSegments(clock);
        Assert.Equal(prefix, segments.AcceptFinal(prefix, 1, 1));
        clock.Advance(TimeSpan.FromMilliseconds(600));
        var preview = segments.ObservePartial(suffix[..2], 1, 1);
        if (prefix.EndsWith("搜索", StringComparison.Ordinal)) Assert.True(preview.ContinuesPhrase);
        clock.Advance(TimeSpan.FromSeconds(3));
        Assert.Equal(expected, segments.AcceptFinal(suffix, 1, 1));
    }

    [Fact]
    public void ACompleteOpeningIsAvailableImmediatelyAndAContinuationDoesNotInterruptIt()
    {
        var segments = new VoiceCommandSegments();
        Assert.Equal("打开我的抖音", segments.AcceptFinal("打开我的抖音", 1, 1));
        Assert.Equal(new VoiceSegmentPreview("打开我的抖音然后搜索", true),
            segments.ObservePartial("然后搜索", 1, 1));
        Assert.Equal("打开我的抖音然后搜索", segments.AcceptFinal("然后搜索", 1, 1));
        Assert.Equal("打开我的抖音然后搜索猫咪视频", segments.AcceptFinal("猫咪视频", 1, 1));
        Assert.Equal("然后搜索狗狗", segments.AcceptFinal("然后搜索狗狗", 1, 1));
    }

    [Theory]
    [InlineData("打开微信")]
    [InlineData("不要搜索")]
    [InlineData("算了")]
    [InlineData("请问天气怎么样")]
    [InlineData("换个话题")]
    [InlineData("先休息")]
    [InlineData("删除下载目录")]
    [InlineData("不是周杰伦")]
    [InlineData("停")]
    [InlineData("停一下")]
    [InlineData("等一下")]
    [InlineData("你说错了")]
    public void ANewInstructionOrRevocationDoesNotBecomeAnOldSearchQuery(string replacement)
    {
        var segments = new VoiceCommandSegments();
        segments.AcceptFinal("打开网易云然后搜索", 1, 1);
        Assert.False(segments.ObservePartial(replacement, 1, 1).ContinuesPhrase);
        Assert.Equal(replacement, segments.AcceptFinal(replacement, 1, 1));
    }

    [Fact]
    public void AnUnrelatedPartialCanStillGrowIntoTheExplicitConnectedSearchClause()
    {
        var segments = new VoiceCommandSegments();
        segments.AcceptFinal("打开网易云", 1, 1);
        Assert.True(segments.ObservePartial("然", 1, 1).ContinuesPhrase);
        Assert.True(segments.ObservePartial("然后搜索", 1, 1).ContinuesPhrase);
        Assert.Equal("打开网易云然后搜索周杰伦", segments.AcceptFinal("然后搜索周杰伦", 1, 1));
    }

    [Fact]
    public void BareMusicWordsCannotAcquireAnOpenAction()
    {
        var segments = new VoiceCommandSegments();
        segments.AcceptFinal("打开网易云", 1, 1);
        Assert.Equal("最近周杰伦的新歌", segments.AcceptFinal("最近周杰伦的新歌", 1, 1));
        segments.AcceptFinal("我看到网易云", 1, 1);
        Assert.Equal("搜索周杰伦", segments.AcceptFinal("搜索周杰伦", 1, 1));
    }

    [Fact]
    public void SilenceAndAnOverlongNextPhraseEndTheConnection()
    {
        var clock = new ManualClock();
        var segments = new VoiceCommandSegments(clock);
        segments.AcceptFinal("打开网易云然后搜索", 1, 1);
        clock.Advance(TimeSpan.FromSeconds(2.01));
        Assert.Equal("最近周杰伦的新歌", segments.AcceptFinal("最近周杰伦的新歌", 1, 1));
        segments.AcceptFinal("打开网易云然后搜索", 1, 1);
        clock.Advance(TimeSpan.FromMilliseconds(500));
        Assert.True(segments.ObservePartial("周杰伦", 1, 1).ContinuesPhrase);
        clock.Advance(TimeSpan.FromSeconds(12));
        Assert.Equal("周杰伦新歌", segments.AcceptFinal("周杰伦新歌", 1, 1));
    }

    [Fact]
    public void StoppingNewInputAndRecognitionReplacementRevokeThePrefix()
    {
        var segments = new VoiceCommandSegments();
        segments.AcceptFinal("打开网易云然后搜索", 1, 1);
        segments.Reset();
        Assert.Equal("周杰伦", segments.AcceptFinal("周杰伦", 1, 1));
        segments.AcceptFinal("打开网易云然后搜索", 1, 1);
        Assert.Equal("周杰伦", segments.AcceptFinal("周杰伦", 2, 1));
        segments.AcceptFinal("打开网易云然后搜索", 2, 1);
        Assert.Equal("周杰伦", segments.AcceptFinal("周杰伦", 2, 2));
    }

    [Fact]
    public void ACompletedSearchCannotLendItsTargetToALaterBareQuery()
    {
        var segments = new VoiceCommandSegments();
        segments.AcceptFinal("打开网易云搜索周杰伦", 1, 1);
        Assert.Equal("最近林俊杰的新歌", segments.AcceptFinal("最近林俊杰的新歌", 1, 1));
    }

    private sealed class ManualClock : TimeProvider
    {
        private DateTimeOffset _now = DateTimeOffset.Parse("2026-10-11T00:00:00Z");
        public override DateTimeOffset GetUtcNow() => _now;
        public void Advance(TimeSpan by) => _now += by;
    }
}
