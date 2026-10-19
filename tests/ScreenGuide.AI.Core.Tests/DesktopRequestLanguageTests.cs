using Xunit;

namespace ScreenGuide.AI.Core.Tests;

public sealed class DesktopRequestLanguageTests
{
    private readonly DeterministicIntentPlanner _planner = new(TimeProvider.System);

    [Theory]
    [InlineData("打开网易云", "网易云")]
    [InlineData("打开我的网易云", "我的网易云")]
    [InlineData("打开我的网易云音乐", "我的网易云音乐")]
    [InlineData("给我打开网易云", "网易云")]
    [InlineData("麻烦你打开我的网易云", "我的网易云")]
    [InlineData("帮我把我的网易云打开", "我的网易云")]
    [InlineData("打开我的抖音", "我的抖音")]
    [InlineData("打开抖音软件", "抖音软件")]
    [InlineData("打开我的 Blender", "我的 Blender")]
    [InlineData("打开我的微信", "我的微信")]
    [InlineData("打开我电脑上的新星创作工具", "我电脑上的新星创作工具")]
    [InlineData("打开抖音创作工具", "抖音创作工具")]
    public void ExplicitApplicationRequestsReachLocalActionInsteadOfChat(string text, string name)
    {
        var plan = _planner.Plan(text, new IntentPlanningContext());
        Assert.Equal(UniversalIntentKind.OpenApplication, plan.Kind);
        Assert.Equal(IntentPlanReadiness.Ready, plan.Readiness);
        Assert.Equal(name, plan.Target);
        Assert.True(plan.RequiresConfirmation);
    }

    [Theory]
    [InlineData("给我打开我的网易云 --admin")]
    [InlineData(@"帮我把C:\tools\cloudmusic.exe打开")]
    [InlineData("麻烦你打开网易云;calc")]
    public void NaturalApplicationSyntaxDoesNotBypassCommandSafety(string text)
    {
        Assert.Equal(IntentPlanReadiness.Unsupported,
            _planner.Plan(text, new IntentPlanningContext()).Readiness);
    }

    [Theory]
    [InlineData("你能打开网易云吗")]
    [InlineData("为什么打不开我的网易云")]
    [InlineData("不要打开网易云")]
    public void DiscussionAndNegationDoNotGrantApplicationAuthority(string text)
    {
        Assert.Equal(UniversalIntentKind.Conversation,
            _planner.Plan(text, new IntentPlanningContext()).Kind);
    }

    [Theory]
    [InlineData("打开抖音;calc")]
    [InlineData("打开我的抖音 --admin")]
    public void WebsiteNameInsideUnsafeApplicationTextDoesNotAuthorizeWebsiteFallback(string text)
    {
        Assert.Equal(IntentPlanReadiness.Unsupported,
            _planner.Plan(text, new IntentPlanningContext()).Readiness);
    }

    [Theory]
    [InlineData("打开桌面上的预算.xlsx")]
    [InlineData("帮我打开桌面上的年度计划")]
    [InlineData("请打开桌面文件“学习记录.md”")]
    public void NamedDesktopFileWaitsForFileResolutionInsteadOfLaunchingAnApplication(string text)
    {
        var plan = _planner.Plan(text, new IntentPlanningContext());
        Assert.Equal(UniversalIntentKind.OpenFile, plan.Kind);
        Assert.Equal(IntentPlanReadiness.NeedsContext, plan.Readiness);
    }

    [Theory]
    [InlineData("在里面搜索 Taylor Swift", "网易云音乐", "cloudmusic", "Taylor Swift")]
    [InlineData("请在这个软件里面帮我搜一下周杰伦", "网易云音乐", "cloudmusic", "周杰伦")]
    public void ExplicitSearchPreservesQueryAndUsesOnlyMatchingForeground(string text, string title, string process, string query)
    {
        var plan = _planner.Plan(text, new IntentPlanningContext(
            ForegroundApplication: new ForegroundApplicationContext(42, title, process)));
        Assert.Equal(UniversalIntentKind.SearchForeground, plan.Kind);
        Assert.Equal(IntentPlanReadiness.Ready, plan.Readiness);
        Assert.Equal(query, plan.Target);
    }

    [Fact]
    public void NamedSearchBindsNamedApplicationRatherThanUnrelatedForeground()
    {
        var plan = _planner.Plan("在网易云音乐里搜索周杰伦", new IntentPlanningContext(
            ForegroundApplication: new ForegroundApplicationContext(42, "抖音", "douyin")));
        Assert.Equal(UniversalIntentKind.ApplicationSearch, plan.Kind);
        Assert.Equal(IntentPlanReadiness.Ready, plan.Readiness);
        Assert.Equal("网易云音乐", plan.ApplicationSearch!.ApplicationName);
        Assert.Equal("周杰伦", plan.ApplicationSearch.Query);
    }
}
