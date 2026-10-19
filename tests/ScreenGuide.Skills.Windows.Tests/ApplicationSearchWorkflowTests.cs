using System.Text.Json;
using ScreenGuide.Skills.Abstractions;

namespace ScreenGuide.Skills.Windows.Tests;

public sealed class ApplicationSearchWorkflowTests
{
    private static readonly KnownDesktopApplication App = new("registered-app", "测试播放器", Environment.ProcessPath!);
    private static readonly ForegroundWindowSnapshot Window = new(42, "测试播放器", "test", 123, DateTimeOffset.UnixEpoch, DateTimeOffset.UtcNow);
    private static string Binding => InstalledApplicationCatalog.CreateTargetBinding(App);

    [Theory]
    [InlineData(ApplicationSearchFollowUp.None)]
    [InlineData(ApplicationSearchFollowUp.OpenFirst)]
    [InlineData(ApplicationSearchFollowUp.PlayFirst)]
    public async Task RequiredStepsAreOrderedAndFollowUpRunsOnlyOnce(ApplicationSearchFollowUp followUp)
    {
        var driver = new Driver();
        var result = await Executor(driver).ExecuteAsync(App, Binding, Request(followUp));
        Assert.True(result.ApplicationVerified && result.QuerySubmitted && result.ResultsVerified);
        Assert.Equal(followUp != ApplicationSearchFollowUp.None, result.FollowUpVerified);
        Assert.Equal(followUp == ApplicationSearchFollowUp.None ? ["open", "search"] : new[] { "open", "search", "invoke" }, driver.Actions);
    }

    [Fact]
    public async Task UnverifiedPageNeverInvokesOrReportsCompletion()
    {
        var driver = new Driver { Page = new(SearchResultPageState.Unverified, "示例音乐", "", []) };
        var error = await Assert.ThrowsAsync<ApplicationSearchWorkflowException>(() => Executor(driver).ExecuteAsync(App, Binding, Request(ApplicationSearchFollowUp.PlayFirst)));
        Assert.Equal("application_search_results_unverified", error.Code);
        Assert.Equal(["open", "search"], driver.Actions);
    }

    [Fact]
    public async Task RepeatedUnverifiedResultsAreReadUntilReadyWithoutResubmittingSearch()
    {
        var reads = 0;
        Driver driver = null!;
        driver = new Driver
        {
            Page = new(SearchResultPageState.Unverified, "示例音乐", "", []),
            OnStep = step =>
            {
                if (step == "read" && ++reads == 3) driver.NextPage = ResultPage();
            }
        };
        var operation = new WindowsApplicationSearchWorkflowAutomation(new Catalog(), driver, TimeSpan.FromSeconds(3));

        var result = await operation.ExecuteAsync(App, Binding, Request());

        Assert.True(result.ResultsVerified);
        Assert.Equal(3, reads);
        Assert.Equal(["open", "search"], driver.Actions);
    }

    [Fact]
    public async Task MismatchedQueryIsNotTheRequestedSearchResult()
    {
        var driver = new Driver { Page = ResultPage() with { Query = "另一条查询" } };
        var error = await Assert.ThrowsAsync<ApplicationSearchWorkflowException>(() => Executor(driver).ExecuteAsync(App, Binding, Request()));
        Assert.Equal("application_search_results_unverified", error.Code);
    }

    [Theory]
    [InlineData(ApplicationSearchFollowUp.None)]
    [InlineData(ApplicationSearchFollowUp.OpenFirst)]
    [InlineData(ApplicationSearchFollowUp.PlayFirst)]
    public async Task EmptyResultsAreExplicitAndNeverInvoke(ApplicationSearchFollowUp followUp)
    {
        var driver = new Driver { Page = new(SearchResultPageState.Empty, "示例音乐", "empty", []) };
        if (followUp == ApplicationSearchFollowUp.None)
            Assert.True((await Executor(driver).ExecuteAsync(App, Binding, Request(followUp))).NoResults);
        else
            Assert.Equal("application_search_no_results", (await Assert.ThrowsAsync<ApplicationSearchWorkflowException>(
                () => Executor(driver).ExecuteAsync(App, Binding, Request(followUp)))).Code);
        Assert.DoesNotContain("invoke", driver.Actions);
    }

    [Theory]
    [InlineData("query")]
    [InlineData("page")]
    [InlineData("first-id")]
    [InlineData("first-title")]
    [InlineData("empty")]
    public async Task ResultReplacementNeverInvokes(string change)
    {
        var driver = new Driver();
        driver.NextPage = change switch
        {
            "query" => ResultPage() with { Query = "changed" },
            "page" => ResultPage() with { Identity = "changed" },
            "first-id" => ResultPage() with { Items = [new("changed", "示例第一首", new object())] },
            "first-title" => ResultPage() with { Items = [new("first", "changed", new object())] },
            _ => ResultPage() with { Items = [] }
        };
        var error = await Assert.ThrowsAsync<ApplicationSearchWorkflowException>(() => Executor(driver).ExecuteAsync(App, Binding, Request(ApplicationSearchFollowUp.OpenFirst)));
        Assert.Equal("application_search_result_changed", error.Code);
        Assert.DoesNotContain("invoke", driver.Actions);
    }

    [Fact]
    public async Task InvokeIsNotProofOfPlaybackAndIsNotRepeated()
    {
        var driver = new Driver { Verify = false };
        var error = await Assert.ThrowsAsync<ApplicationSearchWorkflowException>(() => Executor(driver).ExecuteAsync(App, Binding, Request(ApplicationSearchFollowUp.PlayFirst)));
        Assert.Equal("application_search_followup_unverified", error.Code);
        Assert.Equal(1, driver.Actions.Count(action => action == "invoke"));
    }

    [Theory]
    [InlineData("open")]
    [InlineData("search")]
    [InlineData("read")]
    [InlineData("invoke-boundary")]
    [InlineData("verify")]
    public async Task CancellationReachesEveryStepAndRejectsLateSuccess(string at)
    {
        using var cancellation = new CancellationTokenSource();
        var driver = new Driver { OnStep = step => { if (step == at) cancellation.Cancel(); } };
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Executor(driver).ExecuteAsync(App, Binding, Request(ApplicationSearchFollowUp.PlayFirst), cancellation.Token));
        if (at != "verify") Assert.DoesNotContain("invoke", driver.Actions);
    }

    [Fact]
    public async Task CancellationBeforeStartNeverOpensApplication()
    {
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        var driver = new Driver();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Executor(driver).ExecuteAsync(App, Binding, Request(), cancelled.Token));
        Assert.Empty(driver.Actions);
    }

    [Fact]
    public async Task ChangedRegistrationAtSubmitBoundaryStopsFollowUp()
    {
        var catalog = new Catalog();
        var driver = new Driver { OnStep = step => { if (step == "search") catalog.Application = App with { LaunchTarget = App.LaunchTarget + ".changed" }; } };
        var error = await Assert.ThrowsAsync<ApplicationSearchWorkflowException>(() => Executor(driver, catalog).ExecuteAsync(App, Binding, Request(ApplicationSearchFollowUp.PlayFirst)));
        Assert.Equal("application_search_target_changed", error.Code);
        Assert.DoesNotContain("invoke", driver.Actions);
    }

    [Fact]
    public async Task ForgedBindingOrDifferentApplicationNeverOpensAnything()
    {
        var driver = new Driver();
        await Assert.ThrowsAsync<ApplicationSearchWorkflowException>(() => Executor(driver).ExecuteAsync(App, new string('0', 64), Request()));
        await Assert.ThrowsAsync<ApplicationSearchWorkflowException>(() => Executor(driver).ExecuteAsync(App, Binding, Request() with { ApplicationId = "other" }));
        Assert.Empty(driver.Actions);
    }

    [Fact]
    public void SameProcessNameAtDifferentPathIsNotBoundApplication()
    {
        var process = new ApplicationProcessSnapshot(123, "test", DateTimeOffset.UnixEpoch, true, App.LaunchTarget, null, null, null);
        Assert.True(WindowsSearchResultDriver.MatchesApplication(App, process));
        Assert.False(WindowsSearchResultDriver.MatchesApplication(App, process with { ExecutablePath = Path.Combine(Path.GetTempPath(), Path.GetFileName(App.LaunchTarget)) }));
        Assert.False(WindowsSearchResultDriver.MatchesApplication(App, process with { ExecutablePath = null }));
        var packaged = App with { LaunchTarget = "shell:AppsFolder\\Fake.Package!App" };
        Assert.True(WindowsSearchResultDriver.MatchesApplication(packaged, process with { ApplicationUserModelId = "Fake.Package!App" }));
        Assert.False(WindowsSearchResultDriver.MatchesApplication(packaged, process with { ApplicationUserModelId = "Other.Package!App" }));
    }

    [Theory]
    [InlineData("播放", true)]
    [InlineData("播放 示例第一首", true)]
    [InlineData("播放全部", false)]
    [InlineData("购买并播放", false)]
    [InlineData("转发", false)]
    [InlineData("删除", false)]
    [InlineData("示例第一首", false)]
    public void OnlyExplicitMediaActionNamesCanBeInvoked(string name, bool expected) =>
        Assert.Equal(expected, WindowsSearchResultDriver.ActionNameMatches(name, "示例第一首", ApplicationSearchFollowUp.PlayFirst));

    [Fact]
    public void QueryMustAppearInAnExplicitResultHeading()
    {
        Assert.True(WindowsSearchResultDriver.IsQueryHeading("搜索结果：示例音乐", "示例音乐"));
        Assert.False(WindowsSearchResultDriver.IsQueryHeading("搜索建议：示例音乐", "示例音乐"));
        Assert.False(WindowsSearchResultDriver.IsQueryHeading("示例音乐", "示例音乐"));
        Assert.False(WindowsSearchResultDriver.IsQueryHeading("搜索结果：旧查询", "示例音乐"));
    }

    [Theory]
    [InlineData("歌曲搜索结果：示例音乐", "加载完成", true)]
    [InlineData("歌曲搜索结果：示例音乐", "加载中", false)]
    [InlineData("歌曲搜索结果：示例音乐", "", false)]
    [InlineData("歌曲搜索结果：旧查询", "加载完成", false)]
    [InlineData("歌曲搜索结果", "加载完成", false)]
    [InlineData("搜索建议：示例音乐", "加载完成", false)]
    public void ResultContainerItselfMustBindTheQueryAndCompletedState(string name, string status, bool expected) =>
        Assert.Equal(expected, WindowsSearchResultDriver.IsReadyQueryContainer(name, status, "示例音乐"));

    [Theory]
    [InlineData("media-id:track-123", "media-id:track-123")]
    [InlineData("media-id:", "")]
    [InlineData("row-1", "")]
    [InlineData("示例第一首", "")]
    [InlineData("00:23", "")]
    [InlineData("media-id:fake\n", "")]
    public void MediaIdentityCannotBeANameRowIndexOrPlaybackTimer(string value, string expected) =>
        Assert.Equal(expected, WindowsSearchResultDriver.MediaIdentity(value));

    [Theory]
    [InlineData("initial", true)]
    [InlineData("new-results", true)]
    [InlineData("header-only", false)]
    [InlineData("same-query-same-results", true)]
    [InlineData("remounted-old-results", false)]
    [InlineData("same-query-busy", false)]
    [InlineData("busy", false)]
    [InlineData("wrong-query", false)]
    [InlineData("no-results", false)]
    [InlineData("unreadable-before", false)]
    public void NativeSearchPageBindsCurrentQueryAndRejectsOldResultsForDifferentQuery(string scenario, bool expected)
    {
        var oldSongs = new[] { new SearchResultItem("native:old", "旧结果", new object()) };
        var newSongs = new[] { new SearchResultItem("native:new", "新的公开结果", new object()) };
        var before = new NativeSearchPageObservation(true, true, "旧关键词", "page", NetEaseSearchPageReader.ComputeGeneration("content", oldSongs), false, oldSongs);
        var after = new NativeSearchPageObservation(true, true, "示例音乐", "page", NetEaseSearchPageReader.ComputeGeneration("content", newSongs), false, newSongs);
        switch (scenario)
        {
            case "initial": before = before with { PageExists = false, Generation = "", Songs = [] }; break;
            case "header-only": after = before with { Query = "示例音乐" }; break;
            case "same-query-same-results": before = after; break;
            case "remounted-old-results": after = after with { Songs = oldSongs.Select(song => song with { Identity = "remounted" }).ToArray() }; break;
            case "same-query-busy": before = after; after = after with { Busy = true }; break;
            case "busy": after = after with { Busy = true }; break;
            case "wrong-query": after = after with { Query = "其他" }; break;
            case "no-results": after = after with { Songs = [] }; break;
            case "unreadable-before": before = before with { Readable = false }; break;
        }
        Assert.Equal(expected, NetEaseSearchPageReader.IsVerifiedResultForQuery(before, after, "示例音乐"));
    }

    [Fact]
    public void NativeSongTitleExcludesInlineAuthorSeparators()
    {
        Assert.Equal("示例歌曲", NetEaseSearchPageReader.ReadSongTitle([
            (41, "block", "示例歌曲"), (41, "inline", " / "), (41, "inline", " / "),
            (20, "block", "Tag: VIP")]));
    }

    [Fact]
    public void NativeSongTitleRejectsAmbiguousOrMissingBlockTitle()
    {
        Assert.Null(NetEaseSearchPageReader.ReadSongTitle([(41, "block", "第一标题"), (41, "block", "第二标题")]));
        Assert.Null(NetEaseSearchPageReader.ReadSongTitle([(41, "inline", " / "), (20, "block", "未知内容")]));
        Assert.Null(NetEaseSearchPageReader.ReadSongTitle([(41, null, "缺少结构证据")]));
    }

    [Theory]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData("a\nb")]
    [InlineData(" padded ")]
    public void InvalidQueriesAreRejected(string query) => Assert.Throws<ArgumentException>(() => (Request() with { Query = query }).Validate());

    [Fact]
    public void CanonicalAuthorityBindsEveryStepAndRejectsUndefinedFollowUp()
    {
        var request = Request();
        Assert.Equal(request.ToCanonicalTarget(Binding), request.ToCanonicalTarget(Binding.ToLowerInvariant()));
        Assert.NotEqual(request.ToCanonicalTarget(Binding), (request with { Query = "其他" }).ToCanonicalTarget(Binding));
        Assert.NotEqual(request.ToCanonicalTarget(Binding), (request with { FollowUp = ApplicationSearchFollowUp.PlayFirst }).ToCanonicalTarget(Binding));
        Assert.Throws<ArgumentException>(() => (request with { FollowUp = (ApplicationSearchFollowUp)999 }).Validate());
        Assert.Throws<ArgumentException>(() => request.ToCanonicalTarget("missing"));
        using var parsed = JsonDocument.Parse(request.ToCanonicalTarget(Binding));
        Assert.Equal("None", parsed.RootElement.GetProperty("followUp").GetString());
    }

    [Fact]
    public void NativeColdStartWaitsForTwoStableReliableSamplesBeforeReadingPageOrWriting()
    {
        var startup = new NativeStartup();
        var ready = NativeInput();
        startup.Observe = sample => sample switch
        {
            1 => [],
            2 => [Input(ready with { NativeSearchIdentity = "", HasNativeSearchSemantics = false })],
            3 => [Input(ready with { HasNativeSearchSemantics = false })],
            4 => [],
            _ => [Input(ready)]
        };
        startup.BeforeReadPage = () => Assert.Equal(6, startup.Samples);

        startup.Search();

        Assert.Equal(6, startup.Samples);
        Assert.Equal(1, startup.Automation.Calls);
        Assert.Equal(1, startup.Automation.Submissions);
    }

    [Theory]
    [InlineData("identity")]
    [InlineData("native-mode")]
    public void NativeStartupRestartsStabilityEvidenceWhenTheInputChanges(string change)
    {
        var startup = new NativeStartup();
        var ready = NativeInput();
        var changed = change switch
        {
            "identity" => ready with { NativeSearchIdentity = "ia2:42:other" },
            _ => ready with { NativeSearchUsesContainer = true }
        };
        startup.Observe = sample => [Input(sample == 1 ? ready : changed)];

        startup.Search();

        Assert.Equal(3, startup.Samples);
        Assert.Equal(1, startup.Automation.Submissions);
    }

    [Fact]
    public void NativeStartupPlaceholderAndValueMayChangeWhileTheSameSearchInputStaysReady()
    {
        var startup = new NativeStartup();
        startup.Observe = sample => [Input(NativeInput() with
        {
            Name = $"推荐歌曲 {sample}", Value = $"old value {sample}",
            RuntimeId = $"runtime-{sample}", Bounds = new(sample, 0, 200, 30)
        })];

        startup.Search();

        Assert.Equal(2, startup.Samples);
        Assert.Equal(1, startup.Automation.Submissions);
    }

    [Fact]
    public void NativePreparationIsReadOnlyAndConsumesReadinessForOneSearchOnly()
    {
        var startup = new NativeStartup();
        var search = startup.Prepare();
        Assert.Equal(2, startup.Samples);
        Assert.Equal(0, startup.PageReads);
        Assert.Equal(0, startup.Automation.Calls);
        search();
        Assert.Equal(2, startup.Samples);
        Assert.Equal(1, startup.PageReads);
        Assert.Equal(1, startup.Automation.Submissions);
        search();
        Assert.Equal(4, startup.Samples);
        Assert.Equal(2, startup.Automation.Submissions);
    }

    [Theory]
    [InlineData("foreground")]
    [InlineData("pid")]
    [InlineData("path")]
    public void NativePreparedInputStillRequiresTheOriginalForegroundTarget(string change)
    {
        var startup = new NativeStartup();
        var search = startup.Prepare();
        switch (change)
        {
            case "foreground": startup.Windows.Foreground++; break;
            case "pid": startup.Windows.Current = startup.Windows.Current with { ProcessId = 456 }; break;
            case "path": startup.Windows.Native = startup.Windows.Native! with { Process = startup.Windows.Native.Process with { ExecutablePath = App.LaunchTarget + ".changed" } }; break;
        }
        var error = Assert.Throws<ApplicationSearchWorkflowException>(() => search());
        Assert.Equal("application_search_window_changed", error.Code);
        Assert.Equal(0, startup.Automation.Calls);
        Assert.Equal(0, startup.PageReads);
    }

    [Theory]
    [InlineData("two-inputs")]
    [InlineData("conflicting-proxies")]
    [InlineData("missing-native-identity")]
    [InlineData("pending-container")]
    [InlineData("not-reliable")]
    public void NativeStartupTimeoutNeverInputsIntoAmbiguousOrUnreadyControls(string state)
    {
        var startup = new NativeStartup { DiscoveryDuration = TimeSpan.FromSeconds(20) };
        var ready = NativeInput();
        startup.Observe = _ => state switch
        {
            "two-inputs" => [Input(ready), Input(ready with { RuntimeId = "other", NativeSearchIdentity = "ia2:42:other" })],
            "conflicting-proxies" => [Input(ready), Input(ready with { RuntimeId = "proxy", IsPassword = true })],
            "missing-native-identity" => [Input(ready with { Name = "搜索", NativeSearchIdentity = "" })],
            "pending-container" => [Input(ready with { NativeSearchContainerPending = true })],
            _ => [Input(ready with { HasNativeSearchSemantics = false })]
        };

        var error = Assert.Throws<ApplicationSearchWorkflowException>(() => startup.Search());

        Assert.Equal("application_search_input_not_ready", error.Code);
        Assert.Equal(0, startup.Automation.Calls);
        Assert.Equal(0, startup.PageReads);
        Assert.InRange(startup.Samples, 2, 3);
    }

    [Fact]
    public void NativeStartupMayMergeOnlyConsistentProxiesForOneNativeInput()
    {
        var startup = new NativeStartup();
        var ready = NativeInput();
        startup.Observe = _ => [Input(ready), Input(ready with { RuntimeId = "proxy" })];

        startup.Search();

        Assert.Equal(2, startup.Samples);
        Assert.Equal(1, startup.Automation.Submissions);
    }

    [Theory]
    [InlineData("foreground")]
    [InlineData("handle")]
    [InlineData("pid")]
    [InlineData("start")]
    [InlineData("path")]
    public void NativeStartupRevalidatesTheOpenedTargetAfterEveryReadonlyProbe(string change)
    {
        var startup = new NativeStartup();
        startup.Observe = _ =>
        {
            switch (change)
            {
                case "foreground": startup.Windows.Foreground++; break;
                case "handle": startup.Windows.Current = startup.Windows.Current with { WindowHandle = 43 }; break;
                case "pid": startup.Windows.Current = startup.Windows.Current with { ProcessId = 456 }; break;
                case "start": startup.Windows.Current = startup.Windows.Current with { ProcessStartTimeUtc = DateTimeOffset.UnixEpoch.AddSeconds(1) }; break;
                case "path": startup.Windows.Native = startup.Windows.Native! with { Process = startup.Windows.Native.Process with { ExecutablePath = App.LaunchTarget + ".changed" } }; break;
            }
            return [Input(NativeInput())];
        };

        var error = Assert.Throws<ApplicationSearchWorkflowException>(() => startup.Search());

        Assert.Equal("application_search_window_changed", error.Code);
        Assert.Equal(1, startup.Samples);
        Assert.Equal(0, startup.Automation.Calls);
    }

    [Fact]
    public void NativeStartupCancellationDuringDiscoveryStopsBeforeReadingOrInput()
    {
        using var cancelled = new CancellationTokenSource();
        var startup = new NativeStartup();
        startup.Observe = _ => { cancelled.Cancel(); return [Input(NativeInput())]; };

        Assert.ThrowsAny<OperationCanceledException>(() => startup.Search(cancelled.Token));

        Assert.Equal(0, startup.Automation.Calls);
        Assert.Equal(0, startup.PageReads);
    }

    [Fact]
    public void NativeStartupAllowsTheLastAlreadyStartedReadonlyProbeToFinishPastItsBudget()
    {
        var startup = new NativeStartup { DiscoveryDuration = TimeSpan.FromSeconds(25) };

        startup.Search();

        Assert.Equal(2, startup.Samples);
        Assert.Equal(1, startup.Automation.Submissions);
    }

    [Theory]
    [InlineData(DesktopSearchErrorCodes.CandidateNotFound)]
    [InlineData(DesktopSearchErrorCodes.CandidateAmbiguous)]
    [InlineData(DesktopSearchErrorCodes.WriteVerificationFailed)]
    public void NativeStartupReadinessDoesNotPermitRepeatingActualSearch(string code)
    {
        var startup = new NativeStartup();
        startup.Automation.BeforeSubmit = _ => throw new DesktopSearchException(code, "changed after readonly readiness");

        Assert.Throws<DesktopSearchException>(() => startup.Search());

        Assert.Equal(2, startup.Samples);
        Assert.Equal(1, startup.Automation.Calls);
        Assert.Equal(0, startup.Automation.Submissions);
    }

    [Fact]
    public void OtherApplicationsKeepTheirExistingSearchSelectionWithoutNativeStartupProbing()
    {
        var startup = new NativeStartup(nativeApplication: false);
        startup.Observe = _ => throw new InvalidOperationException("should not discover");

        startup.Search();

        Assert.Equal(0, startup.Samples);
        Assert.Equal(1, startup.Automation.Submissions);
    }

    [Fact]
    public void BoundApplicationTitleCanFinishLoadingBeforeSearchWrites()
    {
        var windows = new BoundWindows();
        var automation = new IdentityCheckingAutomation(windows);
        var driver = windows.CreateDriver(automation);
        var opened = driver.Open(App, CancellationToken.None);
        windows.Current = Window with { WindowTitle = "测试播放器 - 已完成启动" };

        driver.Search(opened, "示例音乐", CancellationToken.None);

        Assert.Equal(1, automation.Calls);
        Assert.Equal(1, automation.Submissions);
        Assert.Equal(windows.Current.WindowTitle, automation.LastWindow!.WindowTitle);
        Assert.False(WindowIdentityContract.Matches(opened, windows.Current));
    }

    [Fact]
    public void BoundApplicationTitleRefreshesAgainOnlyWhenDiscoveryHasNotWritten()
    {
        var windows = new BoundWindows();
        var automation = new IdentityCheckingAutomation(windows)
        {
            BeforeSubmit = call =>
            {
                if (call != 1) return;
                windows.Current = Window with { WindowTitle = "测试播放器 - 主界面" };
                throw new DesktopSearchException(DesktopSearchErrorCodes.CandidateNotFound, "still loading");
            }
        };
        var driver = windows.CreateDriver(automation);
        var opened = driver.Open(App, CancellationToken.None);

        driver.Search(opened, "示例音乐", CancellationToken.None);

        Assert.Equal(2, automation.Calls);
        Assert.Equal(1, automation.Submissions);
        Assert.Equal(windows.Current.WindowTitle, automation.LastWindow!.WindowTitle);
    }

    [Fact]
    public void TitleChangeInsideInputKeepsTheStrictGuardAndNeverRetries()
    {
        var windows = new BoundWindows();
        var automation = new IdentityCheckingAutomation(windows)
        {
            BeforeSubmit = _ =>
            {
                windows.Current = Window with { WindowTitle = "另一个页面" };
                WindowIdentityContract.RequireMatch(Window, windows.Current);
            }
        };
        var driver = windows.CreateDriver(automation);
        var opened = driver.Open(App, CancellationToken.None);

        Assert.Throws<WindowIdentityException>(() => driver.Search(opened, "示例音乐", CancellationToken.None));

        Assert.Equal(1, automation.Calls);
        Assert.Equal(0, automation.Submissions);
    }

    [Fact]
    public void SearchWithoutOpeningABoundApplicationNeverSubmits()
    {
        var windows = new BoundWindows();
        var automation = new IdentityCheckingAutomation(windows);
        var driver = windows.CreateDriver(automation);

        Assert.Throws<ApplicationSearchWorkflowException>(() => driver.Search(Window, "示例音乐", CancellationToken.None));

        Assert.Equal(0, automation.Calls);
    }

    [Theory]
    [InlineData("handle")]
    [InlineData("pid")]
    [InlineData("start")]
    public void SearchCannotSubstituteAnotherWindowForThisOpen(string change)
    {
        var windows = new BoundWindows();
        var automation = new IdentityCheckingAutomation(windows);
        var driver = windows.CreateDriver(automation);
        var opened = driver.Open(App, CancellationToken.None);
        var substituted = change switch
        {
            "handle" => opened with { WindowHandle = 43 },
            "pid" => opened with { ProcessId = 456 },
            _ => opened with { ProcessStartTimeUtc = opened.ProcessStartTimeUtc.AddSeconds(1) }
        };
        windows.Current = substituted;
        windows.Foreground = substituted.WindowHandle;
        windows.Native = windows.Native! with
        {
            Handle = new IntPtr(substituted.WindowHandle),
            Process = windows.Native.Process with
            {
                ProcessId = substituted.ProcessId,
                ProcessStartTimeUtc = substituted.ProcessStartTimeUtc
            }
        };

        Assert.Throws<ApplicationSearchWorkflowException>(() => driver.Search(substituted, "示例音乐", CancellationToken.None));

        Assert.Equal(0, automation.Calls);
    }

    [Fact]
    public void CancelledTitleRefreshNeverSubmits()
    {
        var windows = new BoundWindows();
        var automation = new IdentityCheckingAutomation(windows);
        var driver = windows.CreateDriver(automation);
        var opened = driver.Open(App, CancellationToken.None);
        windows.Current = Window with { WindowTitle = "启动完成" };
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();

        Assert.ThrowsAny<OperationCanceledException>(() => driver.Search(opened, "示例音乐", cancelled.Token));

        Assert.Equal(0, automation.Calls);
    }

    [Theory]
    [InlineData("foreground")]
    [InlineData("snapshot-handle")]
    [InlineData("snapshot-pid")]
    [InlineData("snapshot-start")]
    [InlineData("snapshot-name")]
    [InlineData("native-handle")]
    [InlineData("native-pid")]
    [InlineData("native-start")]
    [InlineData("native-path")]
    [InlineData("native-stopped")]
    [InlineData("native-missing")]
    public void BoundTitleRefreshRejectsChangesToIdentityBeforeAnyInput(string change)
    {
        var windows = new BoundWindows();
        var automation = new IdentityCheckingAutomation(windows);
        var driver = windows.CreateDriver(automation);
        var opened = driver.Open(App, CancellationToken.None);
        switch (change)
        {
            case "foreground": windows.Foreground = 43; break;
            case "snapshot-handle": windows.Current = Window with { WindowHandle = 43 }; break;
            case "snapshot-pid": windows.Current = Window with { ProcessId = 456 }; break;
            case "snapshot-start": windows.Current = Window with { ProcessStartTimeUtc = Window.ProcessStartTimeUtc.AddSeconds(1) }; break;
            case "snapshot-name": windows.Current = Window with { ProcessName = "other" }; break;
            case "native-handle": windows.Native = windows.Native! with { Handle = new IntPtr(43) }; break;
            case "native-pid": windows.Native = windows.Native! with { Process = windows.Native.Process with { ProcessId = 456 } }; break;
            case "native-start": windows.Native = windows.Native! with { Process = windows.Native.Process with { ProcessStartTimeUtc = Window.ProcessStartTimeUtc.AddSeconds(1) } }; break;
            case "native-path": windows.Native = windows.Native! with { Process = windows.Native.Process with { ExecutablePath = App.LaunchTarget + ".changed" } }; break;
            case "native-stopped": windows.Native = windows.Native! with { Process = windows.Native.Process with { IsRunning = false } }; break;
            case "native-missing": windows.Native = null; break;
        }

        Assert.Throws<ApplicationSearchWorkflowException>(() => driver.Search(opened, "示例音乐", CancellationToken.None));

        Assert.Equal(0, automation.Calls);
    }

    [Fact]
    public void AsynchronouslyExposedSearchBoxCanRetryDiscoveryBeforeAnyWrite()
    {
        var automation = new RetryAutomation(DesktopSearchErrorCodes.CandidateNotFound, failures: 1);
        WindowsSearchResultDriver.SubmitWithDiscoveryRetry(() => automation.Search(Window, "示例音乐"), () => { }, CancellationToken.None);
        Assert.Equal(2, automation.Calls);
        Assert.Equal(1, automation.SuccessfulSubmissions);
    }

    [Theory]
    [InlineData(DesktopSearchErrorCodes.CandidateAmbiguous)]
    [InlineData(DesktopSearchErrorCodes.WriteVerificationFailed)]
    [InlineData(DesktopSearchErrorCodes.FocusChanged)]
    [InlineData(DesktopSearchErrorCodes.TargetChanged)]
    public void SearchNeverRetriesAfterPossibleWriteOrAnUnsafeTarget(string code)
    {
        var automation = new RetryAutomation(code, failures: 1);
        Assert.Throws<DesktopSearchException>(() => WindowsSearchResultDriver.SubmitWithDiscoveryRetry(
            () => automation.Search(Window, "示例音乐"), () => { }, CancellationToken.None));
        Assert.Equal(1, automation.Calls);
        Assert.Equal(0, automation.SuccessfulSubmissions);
    }

    [Fact]
    public void DiscoveryRetryDoesNotReactivateAfterUserChangesForegroundWindow()
    {
        var automation = new RetryAutomation(DesktopSearchErrorCodes.CandidateNotFound, failures: 1);
        Assert.Throws<WindowIdentityException>(() => WindowsSearchResultDriver.SubmitWithDiscoveryRetry(
            () => automation.Search(Window, "示例音乐"),
            () => { if (automation.Calls > 0) throw new WindowIdentityException(WindowIdentityErrorCodes.Changed, "foreground changed"); }, CancellationToken.None));
        Assert.Equal(1, automation.Calls);
        Assert.Equal(0, automation.SuccessfulSubmissions);
    }

    private static DesktopSearchControlSnapshot NativeInput() => new("runtime", Window.WindowHandle,
        "", "", "", "", true, false, false, false, true, false, new(0, 0, 200, 30), "",
        "ia2:42:input", true);
    private static DesktopSearchControl Input(DesktopSearchControlSnapshot snapshot) => new(new object(), snapshot);

    private sealed class ReadinessClock : TimeProvider
    {
        public long Timestamp { get; set; }
        public override long TimestampFrequency => TimeSpan.TicksPerSecond;
        public override long GetTimestamp() => Timestamp;
        public void Advance(TimeSpan elapsed) => Timestamp += elapsed.Ticks;
    }

    private sealed class NativeStartup
    {
        public BoundWindows Windows { get; } = new();
        public IdentityCheckingAutomation Automation { get; }
        public Func<int, IReadOnlyList<DesktopSearchControl>> Observe { get; set; } = _ => [Input(NativeInput())];
        public TimeSpan DiscoveryDuration { get; init; } = TimeSpan.FromSeconds(1);
        public Action? BeforeReadPage { get; set; }
        public int Samples { get; private set; }
        public int PageReads { get; private set; }
        private readonly WindowsSearchResultDriver _driver;
        private readonly ReadinessClock _clock = new();
        public NativeStartup(bool nativeApplication = true)
        {
            if (nativeApplication)
            {
                Windows.Current = Window with { ProcessName = "cloudmusic" };
                Windows.Native = Windows.Native! with { Process = Windows.Native.Process with { ProcessName = "cloudmusic" } };
            }
            Automation = new(Windows);
            _driver = new(new BoundLauncher(), Automation, Windows, _ => Windows.Native, () => Windows.Foreground,
                _ => { _clock.Advance(DiscoveryDuration); return Observe(++Samples); }, _clock,
                (duration, cancellation) => { cancellation.ThrowIfCancellationRequested(); _clock.Advance(duration); },
                (_, _) => { BeforeReadPage?.Invoke(); PageReads++; return new(true, false, "", "", "", false, []); });
        }
        public void Search(CancellationToken cancellation = default)
        {
            var opened = _driver.Open(App, cancellation);
            _driver.Search(opened, "示例音乐", cancellation);
        }
        public Action Prepare(CancellationToken cancellation = default)
        {
            var opened = _driver.Open(App, cancellation);
            _driver.PrepareSearch(opened, cancellation);
            return () => _driver.Search(opened, "示例音乐", cancellation);
        }
    }

    private sealed class BoundWindows : IForegroundWindowContextProvider
    {
        public ForegroundWindowSnapshot Current { get; set; } = Window;
        public ApplicationWindowSnapshot? Native { get; set; } = new(new IntPtr(Window.WindowHandle), Window.WindowTitle,
            new(Window.ProcessId, Window.ProcessName, Window.ProcessStartTimeUtc, true, App.LaunchTarget, null, null, null));
        public long Foreground { get; set; } = Window.WindowHandle;
        public ForegroundWindowSnapshot? GetLastExternalWindow() => Current;
        public ForegroundWindowSnapshot? ResolveWindow(long handle) => Current;
        public WindowsSearchResultDriver CreateDriver(IReliableDesktopAutomation automation) =>
            new(new BoundLauncher(), automation, this, _ => Native, () => Foreground);
    }

    private sealed class BoundLauncher : IDesktopProcessLauncher
    {
        public int? Start(string target) => throw new NotSupportedException();
        public VisibleDesktopLaunchResult OpenApplicationVisible(string target) =>
            new(Window.ProcessId, Window.WindowHandle, Window.WindowTitle);
        public VisibleDesktopLaunchResult OpenWebsiteVisible(string? target, Uri website) => throw new NotSupportedException();
    }

    private sealed class IdentityCheckingAutomation(BoundWindows windows) : IReliableDesktopAutomation
    {
        public Action<int>? BeforeSubmit { get; set; }
        public int Calls { get; private set; }
        public int Submissions { get; private set; }
        public ForegroundWindowSnapshot? LastWindow { get; private set; }
        public DesktopAutomationResult Search(ForegroundWindowSnapshot window, string query)
        {
            Calls++;
            WindowIdentityContract.RequireMatch(window, windows.Current);
            LastWindow = window;
            BeforeSubmit?.Invoke(Calls);
            Submissions++;
            return new(true, "submitted");
        }
        public DesktopAutomationResult Describe(ForegroundWindowSnapshot window) => throw new NotSupportedException();
    }

    private sealed class RetryAutomation(string code, int failures) : IReliableDesktopAutomation
    {
        public int Calls { get; private set; }
        public int SuccessfulSubmissions { get; private set; }
        public DesktopAutomationResult Search(ForegroundWindowSnapshot window, string query)
        {
            if (++Calls <= failures) throw new DesktopSearchException(code, "fixture");
            SuccessfulSubmissions++;
            return new(true, "submitted");
        }
        public DesktopAutomationResult Describe(ForegroundWindowSnapshot window) => throw new NotSupportedException();
    }
    private sealed class UnusedLauncher : IDesktopProcessLauncher
    {
        public int? Start(string target) => throw new NotSupportedException();
        public VisibleDesktopLaunchResult OpenApplicationVisible(string target) => throw new NotSupportedException();
        public VisibleDesktopLaunchResult OpenWebsiteVisible(string? target, Uri website) => throw new NotSupportedException();
    }
    private sealed class UnusedWindows : IForegroundWindowContextProvider
    {
        public ForegroundWindowSnapshot? GetLastExternalWindow() => null;
    }

    private static ApplicationSearchWorkflow Request(ApplicationSearchFollowUp followUp = ApplicationSearchFollowUp.None) => new(App.Id, "示例音乐", followUp);
    private static SearchResultPage ResultPage() => new(SearchResultPageState.Results, "示例音乐", "list", [new("first", "示例第一首", new object())]);
    private static WindowsApplicationSearchWorkflowAutomation Executor(Driver driver, Catalog? catalog = null) => new(catalog ?? new(), driver, TimeSpan.Zero);

    private sealed class Catalog : IInstalledApplicationCatalog
    {
        public KnownDesktopApplication Application { get; set; } = App;
        public IReadOnlyList<KnownDesktopApplication> GetApplications() => [Application];
        public KnownDesktopApplication? FindById(string id) => id == Application.Id ? Application : null;
        public KnownDesktopApplication? FindByDisplayName(string name) => name == Application.DisplayName ? Application : null;
        public KnownDesktopApplication? FindBrowser(string browserName) => null;
    }

    private sealed class Driver : IApplicationSearchWorkflowDriver
    {
        public List<string> Actions { get; } = [];
        public SearchResultPage Page { get; init; } = ResultPage();
        public SearchResultPage? NextPage { get; set; }
        public bool Verify { get; init; } = true;
        public Action<string>? OnStep { get; init; }
        private int _reads;
        public ForegroundWindowSnapshot Open(KnownDesktopApplication app, CancellationToken ct) { Actions.Add("open"); OnStep?.Invoke("open"); return Window; }
        public ForegroundWindowSnapshot RequireBound(KnownDesktopApplication app, ForegroundWindowSnapshot window, CancellationToken ct) { ct.ThrowIfCancellationRequested(); return window; }
        public void Search(ForegroundWindowSnapshot window, string query, CancellationToken ct) { Actions.Add("search"); OnStep?.Invoke("search"); }
        public SearchResultPage ReadResults(ForegroundWindowSnapshot window, string query, CancellationToken ct) { OnStep?.Invoke("read"); return _reads++ == 0 ? Page : NextPage ?? Page; }
        public void Invoke(ForegroundWindowSnapshot window, SearchResultPage page, ApplicationSearchFollowUp followUp, Action validateAtInvoke, CancellationToken ct)
        {
            OnStep?.Invoke("invoke-boundary"); validateAtInvoke(); ct.ThrowIfCancellationRequested(); Actions.Add("invoke");
        }
        public bool VerifyFollowUp(ForegroundWindowSnapshot window, SearchResultItem item, ApplicationSearchFollowUp followUp, CancellationToken ct) { OnStep?.Invoke("verify"); return Verify; }
    }
}
