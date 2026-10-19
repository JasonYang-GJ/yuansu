namespace ScreenGuide.Skills.Windows.Tests;

public sealed class DouyinSearchPageReaderTests
{
    [Theory]
    [InlineData(0, null, "sliderVideo video_123", true, true)]
    [InlineData(0x800, null, "sliderVideo video_123", true, false)]
    [InlineData(0, "true", "sliderVideo video_123", true, false)]
    [InlineData(0, null, "sliderVideo video_123 loading-skeleton", true, false)]
    [InlineData(0x8000, null, "sliderVideo video_123", true, false)]
    [InlineData(0x10000, null, "sliderVideo video_123", true, false)]
    [InlineData(0x10000, null, "sliderVideo video_123", false, true)]
    public void NumericMediaIdentityNeverMakesBusyOrHiddenFirstPlayerReady(int state, string? busy, string classes, bool first, bool expected) =>
        Assert.Equal(expected, DouyinSearchPageReader.IsReadyMediaNode(state, busy, classes, first));

    [Theory]
    [InlineData("https://www.douyin.com/jingxuan/search/%E7%A4%BA%E4%BE%8B?ignored=fake", "示例")]
    [InlineData("https://www.douyin.com/jingxuan/search/example", "example")]
    [InlineData("https://other.example/jingxuan/search/example", null)]
    [InlineData("https://www.douyin.com.example/jingxuan/search/example", null)]
    [InlineData("https://fake@www.douyin.com/jingxuan/search/example", null)]
    [InlineData("http://www.douyin.com/jingxuan/search/example", null)]
    [InlineData("https://www.douyin.com:444/jingxuan/search/example", null)]
    [InlineData("https://www.douyin.com/?search=example", null)]
    [InlineData("https://www.douyin.com/jingxuan/search/", null)]
    [InlineData("https://www.douyin.com/jingxuan/search/example/other", null)]
    [InlineData("https://www.douyin.com/jingxuan/search/%0Aexample", null)]
    public void QueryComesFromExactPublicSearchRouteOnly(string uri, string? expected) =>
        Assert.Equal(expected, DouyinSearchPageReader.QueryFromPublicDocumentUri(uri));

    [Theory]
    [InlineData("sliderVideo video_12345 visible", "douyin-video:12345")]
    [InlineData("sliderVideo video_12345 video_67890", "")]
    [InlineData("sliderVideo video_name", "")]
    [InlineData("sliderVideo 12345", "")]
    public void VideoIdentityRequiresOneNumericMediaMarker(string classes, string expected) =>
        Assert.Equal(expected, DouyinSearchPageReader.VideoIdentity(classes));

    [Theory]
    [InlineData("initial", true)]
    [InlineData("empty-before", true)]
    [InlineData("same-query", true)]
    [InlineData("new-first-result", true)]
    [InlineData("old-first-with-added-results", false)]
    [InlineData("route-only", false)]
    [InlineData("busy", false)]
    [InlineData("wrong-query", false)]
    [InlineData("unreadable-before", false)]
    public void ResultPageRequiresBoundQueryAndRejectsOldFirstMediaDuringNavigation(string scenario, bool expected)
    {
        var oldVideo = new SearchResultItem("douyin-video:123", "视频 123", new object());
        var newVideo = new SearchResultItem("douyin-video:456", "视频 456", new object());
        var before = new DouyinSearchPageObservation(true, true, "旧查询", "native-page", false, [oldVideo]);
        var after = new DouyinSearchPageObservation(true, true, "示例", "native-new-page", false, [newVideo]);
        switch (scenario)
        {
            case "initial": before = before with { PageExists = false, Videos = [] }; break;
            case "empty-before": before = before with { Videos = [] }; break;
            case "same-query": before = after; break;
            case "old-first-with-added-results": after = after with { Videos = [oldVideo, newVideo] }; break;
            case "route-only": after = after with { Videos = [oldVideo] }; break;
            case "busy": after = after with { Busy = true }; break;
            case "wrong-query": after = after with { Query = "别的查询" }; break;
            case "unreadable-before": before = before with { Readable = false }; break;
        }
        Assert.Equal(expected, DouyinSearchPageReader.IsVerifiedResultForQuery(before, after, "示例"));
    }
}
