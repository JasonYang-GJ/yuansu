using Xunit;

namespace ScreenGuide.AI.Core.Tests;

public sealed class DesktopFilePlanningTests
{
    private readonly DeterministicIntentPlanner _planner = new(TimeProvider.System);

    [Theory]
    [InlineData("找桌面上的年度计划", "年度计划")]
    [InlineData("帮我找一下我桌面上的预算", "预算")]
    [InlineData("请查找我的桌面文件“学习记录.md”", "学习记录.md")]
    [InlineData("在桌面上找文件会议记录", "会议记录")]
    [InlineData("帮我在我的桌面上查找预算", "预算")]
    [InlineData("找桌面上的搜索笔记.txt", "搜索笔记.txt")]
    [InlineData("找桌面上的打开说明.txt", "打开说明.txt")]
    [InlineData("找桌面上的年度  计划.txt", "年度  计划.txt")]
    public void FindIsLocalReadOnlyAndPreservesTheLiteralName(string text, string name)
    {
        var plan = _planner.Plan(text, new IntentPlanningContext(SelectedFilePath: @"C:\fixture\other.txt"));
        Assert.Equal(UniversalIntentKind.FindDesktopFile, plan.Kind);
        Assert.Equal(IntentPlanReadiness.Ready, plan.Readiness);
        Assert.Equal(name, plan.Target);
        Assert.Null(plan.Capability);
        Assert.False(plan.RequiresConfirmation);
        Assert.Null(plan.MissingContext);
        Assert.True(DesktopFileRequest.TryParse(plan.OriginalText, out var parsed));
        Assert.Equal(name, parsed!.Name);
        Assert.Equal(DesktopFileRequestMode.Find, parsed.Mode);
        Assert.False(DesktopFileRequest.TryGetName(text, out _));
    }

    [Theory]
    [InlineData(UniversalIntentKind.OpenFile)]
    [InlineData(UniversalIntentKind.FindDesktopFile)]
    public void SupplyingFileContextNeverUpgradesFindIntoOpen(UniversalIntentKind persistedIntent)
    {
        var plan = _planner.Plan("找桌面上的预算", new IntentPlanningContext(
            SelectedFilePath: @"C:\fixture\预算.xlsx", ExplicitUserIntent: persistedIntent));
        Assert.Equal(UniversalIntentKind.FindDesktopFile, plan.Kind);
        Assert.Null(plan.Capability);
        Assert.False(plan.RequiresConfirmation);
    }

    [Theory]
    [InlineData("打开桌面上的预算")]
    [InlineData("查看我的桌面文件预算.xlsx")]
    public void OpenKeepsVisibleConfirmationEvenForAnAlreadySelectedFile(string text)
    {
        var plan = _planner.Plan(text, new IntentPlanningContext(SelectedFilePath: @"C:\fixture\预算.xlsx"));
        Assert.Equal(UniversalIntentKind.OpenFile, plan.Kind);
        Assert.Equal("file.open", plan.Capability);
        Assert.True(plan.RequiresConfirmation);
    }

    [Theory]
    [InlineData("找桌面上的")]
    [InlineData("找桌面文件..")]
    [InlineData("找桌面上的*.txt")]
    [InlineData(@"找桌面上的C:\private\预算.xlsx")]
    [InlineData("打开桌面上的../预算.xlsx")]
    [InlineData("找桌面上的预算\n打开网易云")]
    [InlineData("找桌面上的预算\t表.xlsx")]
    [InlineData("找桌面上的预算.xlsx\n")]
    public void InvalidDesktopNamesDoNotBecomeActionsOrProviderConversation(string text)
    {
        var plan = _planner.Plan(text, new IntentPlanningContext());
        Assert.Equal(IntentPlanReadiness.Unsupported, plan.Readiness);
        Assert.Equal(UniversalIntentKind.Unsupported, plan.Kind);
        Assert.Null(plan.Capability);
    }

    [Fact]
    public void OverlongQueryIsRejected()
    {
        Assert.Equal(IntentPlanReadiness.Unsupported,
            _planner.Plan("找桌面上的" + new string('甲', 201), new IntentPlanningContext()).Readiness);
    }

    [Fact]
    public void ExplicitProgrammingEntryKeepsItsScopeWhenTheInstructionDescribesADesktopPath()
    {
        var projectId = Guid.NewGuid();
        var plan = _planner.Plan("打开桌面的 a/b", new IntentPlanningContext(
            SelectedProjectId: projectId, SelectedProjectName: "Fixture project",
            ExplicitUserIntent: UniversalIntentKind.CodingTask));
        Assert.Equal(UniversalIntentKind.CodingTask, plan.Kind);
        Assert.Equal(IntentPlanReadiness.Ready, plan.Readiness);
        Assert.Equal(projectId.ToString("D"), plan.Target);
        Assert.Equal("coding.execute", plan.Capability);
        Assert.True(plan.RequiresConfirmation);
    }

    [Theory]
    [InlineData("不要找桌面上的预算")]
    [InlineData("怎么找桌面上的预算")]
    [InlineData("你能找桌面上的预算吗")]
    public void DiscussionAndNegationDoNotEnumerateFiles(string text)
    {
        Assert.Equal(UniversalIntentKind.Conversation, _planner.Plan(text, new IntentPlanningContext()).Kind);
    }
}
