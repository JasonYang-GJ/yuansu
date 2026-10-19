using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Windows.Automation;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using ScreenGuide.AI.Core;
using ScreenGuide.DesktopHost.Configuration;
using ScreenGuide.DesktopProtocol;
using ScreenGuide.FakeCodexCli;

namespace ScreenGuide.DesktopProduct.Tests;

public sealed class Stage2AiSettingsUiAutomationTests
{
    private const string FakeKey = "ds-fake-stage2-ui-canary-never-send";
    private const string VoiceHarnessResultPrefix = "SCREEN_GUIDE_VOICE_HARNESS_RESULT ";

    [Fact]
    [Trait("Category", "DesktopAcceptance")]
    public async Task ActualReleaseClientKeepsStage2RoutingCredentialsAndSessionVisible()
    {
        var testRoot = Path.Combine(
            Path.GetTempPath(),
            $"screen-guide-stage2-ui-{Guid.NewGuid():N}");
        var dataRoot = Path.Combine(testRoot, "user-data");
        var projectRoot = Path.Combine(testRoot, "authorized-project");
        var selectedFile = Path.Combine(testRoot, "selected-file.txt");
        Directory.CreateDirectory(dataRoot);
        Directory.CreateDirectory(projectRoot);
        await RunGitAsync(projectRoot, "init", "--quiet");
        await File.WriteAllTextAsync(selectedFile, "stage2 real client file context");
        await WriteCompletedOnboardingSettingsAsync(dataRoot);

        var codex = RecordingChatProvider.Codex();
        var deepSeek = RecordingChatProvider.DeepSeek();
        var qwen = RecordingChatProvider.Qwen();
        Process? clientProcess = null;
        IHost? host = null;
        try
        {
            var binaries = LocateReleaseBinaries();
            var pipeName = $"ScreenGuide.Stage2UiAcceptance.{Guid.NewGuid():N}";
            var options = new DesktopHostOptions(dataRoot, binaries.FakeCodexPath, pipeName);
            host = DesktopHostFactory.Build(
                [],
                options,
                services =>
                {
                    services.RemoveAll<IChatModelProvider>();
                    services.AddSingleton<IChatModelProvider>(codex);
                    services.AddSingleton<IChatModelProvider>(deepSeek);
                    services.AddSingleton<IChatModelProvider>(qwen);
                });
            await host.StartAsync();

            var api = new DesktopApiClient(pipeName, TimeSpan.FromSeconds(5));
            await WaitUntilAsync(() => api.PingAsync(), TimeSpan.FromSeconds(20));
            _ = await api.SetChatRouteAsync(new SetChatRouteRequestDto(
                "codex",
                "codex-default"));
            var project = await api.AddProjectAsync(new AddProjectRequestDto(
                projectRoot,
                "Stage2 real client project"));
            clientProcess = StartClient(binaries, dataRoot, pipeName);
            var clientWindow = await WaitForMainWindowAsync(
                clientProcess,
                dataRoot,
                TimeSpan.FromSeconds(15));
            var automationRoot = AutomationElement.FromHandle(clientWindow)
                ?? throw new InvalidOperationException("无法连接实际 DesktopClient 的 WPF 自动化树。");

            Invoke(await WaitForElementByNameAsync(
                automationRoot,
                "设置",
                ControlType.Button,
                TimeSpan.FromSeconds(10)));
            var providerCombo = await WaitForElementByAutomationIdAsync(
                automationRoot,
                "AiChatProvider",
                TimeSpan.FromSeconds(10));
            _ = await WaitForElementByNameAsync(
                automationRoot,
                "由下面选择的 Provider 和 Model 负责。",
                ControlType.Text,
                TimeSpan.FromSeconds(10));
            _ = await WaitForElementByNameAsync(
                automationRoot,
                "编程任务仍由 Codex 负责。",
                ControlType.Text,
                TimeSpan.FromSeconds(10));

            var modelCombo = await WaitForElementByAutomationIdAsync(
                automationRoot,
                "AiChatModel",
                TimeSpan.FromSeconds(10));
            Assert.Equal("Codex", SelectedItemName(providerCombo));
            Assert.Equal("Codex Chat", SelectedItemName(modelCombo));

            await SelectComboBoxItemAsync(
                providerCombo,
                clientProcess.Id,
                "千问",
                TimeSpan.FromSeconds(10));
            await WaitUntilAsync(
                () => Task.FromResult(SelectedItemName(modelCombo) == "千问 3.7 Plus"),
                TimeSpan.FromSeconds(10));
            var qwenDestination = await WaitForElementByAutomationIdAsync(
                automationRoot,
                "AiDataDestination",
                TimeSpan.FromSeconds(10));
            Assert.Contains(
                "https://dashscope.aliyuncs.com",
                qwenDestination.Current.Name,
                StringComparison.Ordinal);
            Assert.Empty(qwen.Requests);

            await SelectComboBoxItemAsync(
                providerCombo,
                clientProcess.Id,
                "Codex",
                TimeSpan.FromSeconds(10));
            await WaitUntilAsync(
                () => Task.FromResult(SelectedItemName(modelCombo) == "Codex Chat"),
                TimeSpan.FromSeconds(10));

            Invoke(await WaitForElementByNameAsync(
                automationRoot,
                "首页",
                ControlType.Button,
                TimeSpan.FromSeconds(10)));
            Invoke(await WaitForElementByAutomationIdAsync(
                automationRoot,
                "NewTopic",
                TimeSpan.FromSeconds(10)));
            var session = await WaitForSessionAsync(
                api,
                snapshot => snapshot is { Title: "新话题" },
                TimeSpan.FromSeconds(10));

            const string firstQuestion = "请看看这个阶段二第一轮代号并记住青石。";
            var firstTurn = await api.SubmitSessionInputAsync(new SessionInputRequestDto(
                firstQuestion,
                "Text",
                $"stage2-ui-codex-{Guid.NewGuid():N}",
                session.SessionId));
            Assert.Equal(
                firstTurn.TurnId,
                await codex.SemanticBlockingTurnStarted.Task.WaitAsync(TimeSpan.FromSeconds(10)));

            Invoke(await WaitForElementByNameAsync(
                automationRoot,
                "设置",
                ControlType.Button,
                TimeSpan.FromSeconds(10)));
            providerCombo = await WaitForElementByAutomationIdAsync(
                automationRoot,
                "AiChatProvider",
                TimeSpan.FromSeconds(10));
            modelCombo = await WaitForElementByAutomationIdAsync(
                automationRoot,
                "AiChatModel",
                TimeSpan.FromSeconds(10));
            await SelectComboBoxItemAsync(
                providerCombo,
                clientProcess.Id,
                "DeepSeek",
                TimeSpan.FromSeconds(10));
            await WaitUntilAsync(
                () => Task.FromResult(SelectedItemName(modelCombo) == "DeepSeek V4 Flash"),
                TimeSpan.FromSeconds(10));
            await SelectComboBoxItemAsync(
                modelCombo,
                clientProcess.Id,
                "DeepSeek V4 Pro",
                TimeSpan.FromSeconds(10));

            var destination = await WaitForElementByAutomationIdAsync(
                automationRoot,
                "AiDataDestination",
                TimeSpan.FromSeconds(10));
            Assert.Contains("https://api.deepseek.com", destination.Current.Name, StringComparison.Ordinal);
            Assert.Contains("离开本机", destination.Current.Name, StringComparison.Ordinal);

            var password = await WaitForElementByAutomationIdAsync(
                automationRoot,
                "AiCredentialPassword",
                TimeSpan.FromSeconds(10));
            Assert.True(password.Current.IsPassword);
            Assert.Equal("Provider 密钥，只能设置，不能查看", password.Current.Name);
            Assert.True(
                password.TryGetCurrentPattern(ValuePattern.Pattern, out var rawPasswordValue),
                "Provider 密钥输入框必须允许 UI Automation 安全写入。 ");
            ((ValuePattern)rawPasswordValue).SetValue(FakeKey);
            AssertNoAutomationNameContains(automationRoot, FakeKey);
            Invoke(await WaitForElementByAutomationIdAsync(
                automationRoot,
                "SaveAiCredential",
                TimeSpan.FromSeconds(10)));
            await WaitForAutomationTextAsync(
                automationRoot,
                "AiCredentialStatus",
                "已配置",
                TimeSpan.FromSeconds(10));
            Assert.True(password.Current.IsPassword);
            AssertNoAutomationNameContains(automationRoot, FakeKey);
            AssertNoPlaintextCanary(dataRoot, FakeKey);

            Invoke(await WaitForElementByAutomationIdAsync(
                automationRoot,
                "CheckAiProviderHealth",
                TimeSpan.FromSeconds(10)));
            await WaitForAutomationTextAsync(
                automationRoot,
                "AiProviderHealthStatus",
                "连接状态：正常",
                TimeSpan.FromSeconds(10));
            await WaitForAutomationTextAsync(
                automationRoot,
                "AiProviderHealthStatus",
                "DeepSeek Fake Provider 连接正常",
                TimeSpan.FromSeconds(10));

            Invoke(await WaitForElementByAutomationIdAsync(
                automationRoot,
                "SaveAiChatRoute",
                TimeSpan.FromSeconds(10)));
            await WaitUntilAsync(
                async () =>
                {
                    var settings = await api.GetAiSettingsAsync();
                    return settings.CurrentChatRoute is
                    {
                        ProviderId: "deepseek",
                        ModelId: "deepseek-v4-pro"
                    };
                },
                TimeSpan.FromSeconds(10));

            codex.AllowSemanticCompletion.TrySetResult();
            var firstCompleted = await WaitForTurnPhaseAsync(
                api,
                firstTurn.TurnId,
                "Completed",
                TimeSpan.FromSeconds(10));
            var codexRequests = codex.Requests
                .Where(request => request.TurnId == firstTurn.TurnId)
                .ToArray();
            Assert.Collection(
                codexRequests,
                semanticRequest =>
                {
                    Assert.Equal("codex", semanticRequest.ProviderId);
                    Assert.Equal("codex-default", semanticRequest.ModelId);
                    Assert.Equal("intent.semantic", semanticRequest.PromptId);
                    Assert.Collection(
                        semanticRequest.Messages,
                        message => AssertChatMessage(
                            message,
                            ChatMessageRole.User,
                            firstQuestion));
                },
                chatRequest =>
                {
                    Assert.Equal("codex", chatRequest.ProviderId);
                    Assert.Equal("codex-default", chatRequest.ModelId);
                    Assert.Equal("chat.general", chatRequest.PromptId);
                    Assert.Collection(
                        chatRequest.Messages,
                        message => AssertChatMessage(
                            message,
                            ChatMessageRole.User,
                            firstQuestion));
                });
            var firstReply = Assert.Single(
                firstCompleted.Messages,
                message => string.Equals(message.Role, "Assistant", StringComparison.Ordinal));

            Invoke(await WaitForElementByNameAsync(
                automationRoot,
                "首页",
                ControlType.Button,
                TimeSpan.FromSeconds(10)));
            var continuedSession = await WaitForSessionAsync(
                api,
                snapshot => snapshot?.SessionId == session.SessionId,
                TimeSpan.FromSeconds(10));
            Assert.Equal(session.ConversationId, continuedSession.ConversationId);
            _ = await WaitForElementByNameAsync(
                automationRoot,
                firstReply.Content,
                ControlType.Text,
                TimeSpan.FromSeconds(10));

            const string secondQuestion = "刚才那个代号是什么？请只引用它。";
            var secondTurn = await api.SubmitSessionInputAsync(new SessionInputRequestDto(
                secondQuestion,
                "Text",
                $"stage2-ui-deepseek-{Guid.NewGuid():N}",
                session.SessionId));
            var secondCompleted = await WaitForTurnPhaseAsync(
                api,
                secondTurn.TurnId,
                "Completed",
                TimeSpan.FromSeconds(10));
            var deepSeekRequest = Assert.Single(deepSeek.Requests);
            Assert.Equal(secondTurn.TurnId, deepSeekRequest.TurnId);
            Assert.Equal("deepseek-v4-pro", deepSeekRequest.ModelId);
            Assert.Collection(
                deepSeekRequest.Messages,
                message => AssertChatMessage(message, ChatMessageRole.User, firstQuestion),
                message => AssertChatMessage(message, ChatMessageRole.Assistant, firstReply.Content),
                message => AssertChatMessage(message, ChatMessageRole.User, secondQuestion));
            var secondReply = secondCompleted.Messages.Last(message =>
                string.Equals(message.Role, "Assistant", StringComparison.Ordinal));
            _ = await WaitForElementByNameAsync(
                automationRoot,
                secondReply.Content,
                ControlType.Text,
                TimeSpan.FromSeconds(10));

            const string replacedQuestion = "STAGE2_BLOCK_THEN_LATE_RESULT";
            var replacedTurn = await api.SubmitSessionInputAsync(new SessionInputRequestDto(
                replacedQuestion,
                "Text",
                $"stage2-ui-replaced-{Guid.NewGuid():N}",
                session.SessionId));
            var providerTurnId = await deepSeek.BlockingTurnStarted.Task.WaitAsync(
                TimeSpan.FromSeconds(10));
            Assert.Equal(replacedTurn.TurnId, providerTurnId);
            await WaitForTurnPhaseAsync(
                api,
                replacedTurn.TurnId,
                "Responding",
                TimeSpan.FromSeconds(10));
            const string replacementQuestion = "请改为回答替换后的新问题。";
            var replacementSubmit = api.SubmitSessionInputAsync(new SessionInputRequestDto(
                replacementQuestion,
                "Text",
                $"stage2-ui-replacement-{Guid.NewGuid():N}",
                session.SessionId));
            Assert.Equal(
                providerTurnId,
                await deepSeek.CancellationObserved.Task.WaitAsync(TimeSpan.FromSeconds(10)));
            Assert.False(replacementSubmit.IsCompleted);
            deepSeek.AllowLateReply.TrySetResult();
            Assert.Equal(
                providerTurnId,
                await deepSeek.LateReplyReturned.Task.WaitAsync(TimeSpan.FromSeconds(10)));
            var replacementTurn = await replacementSubmit;
            var replacementCompleted = await WaitForTurnPhaseAsync(
                api,
                replacementTurn.TurnId,
                "Completed",
                TimeSpan.FromSeconds(10));
            var afterReplacement = await api.GetCurrentSessionAsync();
            Assert.NotNull(afterReplacement);
            Assert.Equal(
                "Cancelled",
                afterReplacement.Turns.Single(turn => turn.Id == replacedTurn.TurnId).Phase);
            Assert.Equal(
                "Completed",
                afterReplacement.Turns.Single(turn => turn.Id == replacementTurn.TurnId).Phase);
            Assert.DoesNotContain(
                afterReplacement.Messages,
                message => message.Content.Contains("迟到", StringComparison.Ordinal));
            Assert.Contains(
                afterReplacement.Messages,
                message => message.Role == "User" && message.Content == replacementQuestion);
            var replacementReply = replacementCompleted.Messages.Last(message =>
                string.Equals(message.Role, "Assistant", StringComparison.Ordinal));
            _ = await WaitForElementByNameAsync(
                automationRoot,
                replacementReply.Content,
                ControlType.Text,
                TimeSpan.FromSeconds(10));

            const string projectQuestion = "把这个项目补齐流程准备好，但先不要执行。";
            var projectTurn = await api.SubmitSessionInputAsync(new SessionInputRequestDto(
                projectQuestion,
                "ProgrammingTask",
                $"stage2-ui-project-context-{Guid.NewGuid():N}",
                session.SessionId));
            await WaitForTurnPhaseAsync(
                api,
                projectTurn.TurnId,
                "WaitingForProject",
                TimeSpan.FromSeconds(10));
            var projectCombo = await WaitForElementByAutomationIdAsync(
                automationRoot,
                "SessionProjectPicker",
                TimeSpan.FromSeconds(10));
            Assert.Contains(
                project.Name,
                SelectedItemName(projectCombo),
                StringComparison.Ordinal);
            Invoke(await WaitForElementByAutomationIdAsync(
                automationRoot,
                "ContinueSessionContext",
                TimeSpan.FromSeconds(10)));
            var projectSelected = await WaitForTurnPhaseAsync(
                api,
                projectTurn.TurnId,
                "WaitingForConfirmation",
                TimeSpan.FromSeconds(10));
            var projectSelectedTurn = projectSelected.Turns.Single(turn =>
                turn.Id == projectTurn.TurnId);
            Assert.Equal(projectQuestion, projectSelectedTurn.InputText);
            Assert.Equal(project.Id, projectSelectedTurn.ProjectId);
            Invoke(await WaitForElementByAutomationIdAsync(
                automationRoot,
                "CancelSessionContext",
                TimeSpan.FromSeconds(10)));
            await WaitForTurnPhaseAsync(
                api,
                projectTurn.TurnId,
                "Cancelled",
                TimeSpan.FromSeconds(10));

            const string fileQuestion = "帮我打开这个文件。";
            var fileTurn = await api.SubmitSessionInputAsync(new SessionInputRequestDto(
                fileQuestion,
                "Text",
                $"stage2-ui-file-context-{Guid.NewGuid():N}",
                session.SessionId));
            await WaitForTurnPhaseAsync(
                api,
                fileTurn.TurnId,
                "WaitingForFile",
                TimeSpan.FromSeconds(10));
            Invoke(await WaitForElementByAutomationIdAsync(
                automationRoot,
                "ContinueSessionContext",
                TimeSpan.FromSeconds(10)));
            var fileDialog = await WaitForTopLevelAutomationWindowAsync(
                clientProcess.Id,
                "选择这一次要处理的文件",
                TimeSpan.FromSeconds(10));
            await ChooseFileInDialogAsync(fileDialog, selectedFile, TimeSpan.FromSeconds(10));
            var fileSelected = await WaitForTurnPhaseAsync(
                api,
                fileTurn.TurnId,
                "WaitingForConfirmation",
                TimeSpan.FromSeconds(10));
            var fileSelectedTurn = fileSelected.Turns.Single(turn => turn.Id == fileTurn.TurnId);
            Assert.Equal(fileQuestion, fileSelectedTurn.InputText);
            Assert.Equal(
                GetFinalPath(selectedFile),
                GetFinalPath(fileSelectedTurn.FilePath!),
                ignoreCase: true);
            Invoke(await WaitForElementByAutomationIdAsync(
                automationRoot,
                "CancelSessionContext",
                TimeSpan.FromSeconds(10)));
            await WaitForTurnPhaseAsync(
                api,
                fileTurn.TurnId,
                "Cancelled",
                TimeSpan.FromSeconds(10));

            Invoke(await WaitForElementByNameAsync(
                automationRoot,
                "设置",
                ControlType.Button,
                TimeSpan.FromSeconds(10)));
            await WaitForAutomationTextAsync(
                automationRoot,
                "AiCredentialStatus",
                "已配置",
                TimeSpan.FromSeconds(10));
            Invoke(await WaitForElementByAutomationIdAsync(
                automationRoot,
                "DeleteAiCredential",
                TimeSpan.FromSeconds(10)));
            var deleteDialog = await WaitForTopLevelAutomationWindowAsync(
                clientProcess.Id,
                "删除 Provider 密钥",
                TimeSpan.FromSeconds(10));
            AssertAutomationTreeContains(deleteDialog, "DeepSeek");
            AssertAutomationTreeContains(deleteDialog, "无法使用普通聊天");
            await SendDialogCommandAsync(deleteDialog, "7", TimeSpan.FromSeconds(10));
            await WaitForTopLevelWindowToCloseAsync(
                clientProcess.Id,
                "删除 Provider 密钥",
                TimeSpan.FromSeconds(10));
            await WaitForAutomationTextAsync(
                automationRoot,
                "AiCredentialStatus",
                "已配置",
                TimeSpan.FromSeconds(10));

            Invoke(await WaitForElementByAutomationIdAsync(
                automationRoot,
                "DeleteAiCredential",
                TimeSpan.FromSeconds(10)));
            deleteDialog = await WaitForTopLevelAutomationWindowAsync(
                clientProcess.Id,
                "删除 Provider 密钥",
                TimeSpan.FromSeconds(10));
            await SendDialogCommandAsync(deleteDialog, "6", TimeSpan.FromSeconds(10));
            await WaitForAutomationTextAsync(
                automationRoot,
                "AiCredentialStatus",
                "未配置",
                TimeSpan.FromSeconds(10));
            await WaitUntilAsync(
                async () =>
                {
                    var settings = await api.GetAiSettingsAsync();
                    return settings.Providers.Single(provider =>
                            string.Equals(provider.ProviderId, "deepseek", StringComparison.OrdinalIgnoreCase))
                        .ConfigurationState == "Missing";
                },
                TimeSpan.FromSeconds(10));
            AssertNoPlaintextCanary(dataRoot, FakeKey);
        }
        finally
        {
            await StopProcessAsync(clientProcess, entireProcessTree: false);
            if (host is not null)
            {
                await host.StopAsync().WaitAsync(TimeSpan.FromSeconds(10));
                host.Dispose();
            }

            await DeleteDirectoryWithRetryAsync(testRoot);
        }
    }

    [Fact]
    [Trait("Category", "DesktopAcceptance")]
    public async Task ActualReleaseClientProvidesVisibleStage3MemoryConsentFlowOffline()
    {
        const string initialTitle = "离线验收偏好";
        const string initialBody = "离线验收正文，不得发送到真实网络。";
        const string updatedTitle = "离线验收偏好已修正";
        const string updatedBody = "离线修正正文，只允许发送给进程内 Fake Provider。";
        const string firstQuestion = "请按我选择的记忆回答本轮离线问题。";
        const string invalidatedQuestion = "这次确认必须在输入变化后失效。";
        var testRoot = Path.Combine(
            Path.GetTempPath(),
            $"screen-guide-stage3-memory-ui-{Guid.NewGuid():N}");
        var dataRoot = Path.Combine(testRoot, "user-data");
        Directory.CreateDirectory(dataRoot);
        await WriteCompletedOnboardingSettingsAsync(dataRoot);

        var provider = RecordingChatProvider.DeepSeek();
        Process? clientProcess = null;
        IHost? host = null;
        try
        {
            var binaries = LocateReleaseBinaries();
            var pipeName = $"ScreenGuide.Stage3MemoryUi.{Guid.NewGuid():N}";
            var options = new DesktopHostOptions(dataRoot, binaries.FakeCodexPath, pipeName);
            host = DesktopHostFactory.Build(
                [],
                options,
                services =>
                {
                    services.RemoveAll<IChatModelProvider>();
                    services.AddSingleton<IChatModelProvider>(provider);
                });
            await host.StartAsync();

            var api = new DesktopApiClient(pipeName, TimeSpan.FromSeconds(5));
            await WaitUntilAsync(() => api.PingAsync(), TimeSpan.FromSeconds(20));
            _ = await api.SetChatRouteAsync(new SetChatRouteRequestDto(
                "deepseek",
                "deepseek-v4-pro"));
            clientProcess = StartClient(binaries, dataRoot, pipeName);
            var clientWindow = await WaitForMainWindowAsync(
                clientProcess,
                dataRoot,
                TimeSpan.FromSeconds(15));
            var automationRoot = AutomationElement.FromHandle(clientWindow)
                ?? throw new InvalidOperationException("无法连接实际 DesktopClient 的 WPF 自动化树。");

            Invoke(await WaitForElementByNameAsync(
                automationRoot,
                "设置",
                ControlType.Button,
                TimeSpan.FromSeconds(10)));
            _ = await WaitForElementByAutomationIdAsync(
                automationRoot,
                "MemoryLocalOnlyNotice",
                TimeSpan.FromSeconds(10));
            SetElementValue(
                await WaitForElementByAutomationIdAsync(
                    automationRoot,
                    "MemoryTitle",
                    TimeSpan.FromSeconds(10)),
                initialTitle);
            SetElementValue(
                await WaitForElementByAutomationIdAsync(
                    automationRoot,
                    "MemoryBody",
                    TimeSpan.FromSeconds(10)),
                initialBody);
            Invoke(await WaitForElementByAutomationIdAsync(
                automationRoot,
                "SaveMemory",
                TimeSpan.FromSeconds(10)));

            MemoryDto? memory = null;
            await WaitUntilAsync(
                async () =>
                {
                    memory = (await api.ListMemoriesAsync()).SingleOrDefault(item =>
                        string.Equals(item.Title, initialTitle, StringComparison.Ordinal));
                    return memory is not null;
                },
                TimeSpan.FromSeconds(10));
            Assert.Equal("Active", memory!.Status);
            Assert.Equal(initialBody, memory.Body);
            var memoryId = memory.Id;
            AssertAutomationTreeContains(
                await WaitForElementByAutomationIdAsync(
                    automationRoot,
                    "MemoryList",
                    TimeSpan.FromSeconds(10)),
                initialTitle);
            AssertNoPlaintextCanary(dataRoot, initialBody);

            SetElementValue(
                await WaitForElementByAutomationIdAsync(
                    automationRoot,
                    "MemoryTitle",
                    TimeSpan.FromSeconds(10)),
                updatedTitle);
            SetElementValue(
                await WaitForElementByAutomationIdAsync(
                    automationRoot,
                    "MemoryBody",
                    TimeSpan.FromSeconds(10)),
                updatedBody);
            Invoke(await WaitForElementByAutomationIdAsync(
                automationRoot,
                "SaveMemory",
                TimeSpan.FromSeconds(10)));
            await WaitUntilAsync(
                async () =>
                {
                    memory = await api.GetMemoryAsync(memoryId);
                    return memory.Version == 2
                           && string.Equals(memory.Title, updatedTitle, StringComparison.Ordinal)
                           && string.Equals(memory.Body, updatedBody, StringComparison.Ordinal);
                },
                TimeSpan.FromSeconds(10));

            Invoke(await WaitForElementByAutomationIdAsync(
                automationRoot,
                "ToggleMemory",
                TimeSpan.FromSeconds(10)));
            await WaitUntilAsync(
                async () => string.Equals(
                    (await api.GetMemoryAsync(memoryId)).Status,
                    "Disabled",
                    StringComparison.Ordinal),
                TimeSpan.FromSeconds(10));
            Invoke(await WaitForElementByAutomationIdAsync(
                automationRoot,
                "ToggleMemory",
                TimeSpan.FromSeconds(10)));
            await WaitUntilAsync(
                async () => string.Equals(
                    (await api.GetMemoryAsync(memoryId)).Status,
                    "Active",
                    StringComparison.Ordinal),
                TimeSpan.FromSeconds(10));
            memory = await api.GetMemoryAsync(memoryId);

            SetElementValue(
                await WaitForElementByAutomationIdAsync(
                    automationRoot,
                    "MemoryPreviewQuery",
                    TimeSpan.FromSeconds(10)),
                "离线 修正");
            Invoke(await WaitForElementByAutomationIdAsync(
                automationRoot,
                "RunMemoryPreview",
                TimeSpan.FromSeconds(10)));
            var previewList = await WaitForElementByAutomationIdAsync(
                automationRoot,
                "MemoryPreviewResults",
                TimeSpan.FromSeconds(10));
            await WaitUntilAsync(
                () => Task.FromResult(previewList.FindAll(
                        TreeScope.Descendants,
                        Condition.TrueCondition)
                    .Cast<AutomationElement>()
                    .Any(item => item.Current.Name.Contains(
                        updatedTitle,
                        StringComparison.Ordinal))),
                TimeSpan.FromSeconds(10));
            Assert.Empty(provider.Requests);

            Invoke(await WaitForElementByNameAsync(
                automationRoot,
                "对话",
                ControlType.Button,
                TimeSpan.FromSeconds(10)));
            Invoke(await WaitForElementByAutomationIdAsync(
                automationRoot,
                "NewConversation",
                TimeSpan.FromSeconds(10)));
            _ = await WaitForSessionAsync(
                api,
                snapshot => snapshot is { Title: "新话题" },
                TimeSpan.FromSeconds(10));
            var (_, memorySelection) = await WaitForSelectableListItemAsync(
                new AndCondition(
                    new PropertyCondition(AutomationElement.ProcessIdProperty, clientProcess.Id),
                    new PropertyCondition(AutomationElement.NameProperty, updatedTitle),
                    new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.ListItem)),
                TimeSpan.FromSeconds(10));
            memorySelection.Select();
            SetElementValue(
                await WaitForElementByAutomationIdAsync(
                    automationRoot,
                    "ConversationInput",
                    TimeSpan.FromSeconds(10)),
                firstQuestion);
            Invoke(await WaitForElementByAutomationIdAsync(
                automationRoot,
                "SendConversation",
                TimeSpan.FromSeconds(10)));

            var waiting = await WaitForSessionAsync(
                api,
                snapshot => snapshot?.ForegroundTurn?.Phase == "WaitingForMemoryOutboundConsent"
                            && snapshot.MemoryOutboundConsents?.Count == 1,
                TimeSpan.FromSeconds(10));
            var firstTurnId = waiting.ForegroundTurn!.Id;
            var firstConsent = Assert.Single(waiting.MemoryOutboundConsents!);
            Assert.Equal("deepseek", firstConsent.ProviderId);
            Assert.Equal("deepseek-v4-pro", firstConsent.ModelId);
            Assert.Equal("https://api.deepseek.com", firstConsent.DestinationOrigin);
            await Task.Delay(TimeSpan.FromSeconds(1));
            var stableWaiting = await api.GetCurrentSessionAsync();
            Assert.Equal(
                "WaitingForMemoryOutboundConsent",
                stableWaiting?.ForegroundTurn?.Phase);
            Assert.Single(stableWaiting!.MemoryOutboundConsents!);
            var confirmMemoryOutbound = await WaitForRenderedElementByAutomationIdAsync(
                automationRoot,
                "ConfirmMemoryOutbound",
                TimeSpan.FromSeconds(20));
            AssertAutomationTreeContains(automationRoot, "deepseek-v4-pro");
            AssertAutomationTreeContains(automationRoot, "https://api.deepseek.com");
            AssertAutomationTreeContains(automationRoot, updatedTitle);
            AssertAutomationTreeContains(automationRoot, updatedBody);
            Assert.Empty(provider.Requests);

            Invoke(confirmMemoryOutbound);
            _ = await WaitForTurnPhaseAsync(
                api,
                firstTurnId,
                "Completed",
                TimeSpan.FromSeconds(10));
            var firstRequest = Assert.Single(
                provider.Requests,
                request => request.TurnId == firstTurnId);
            Assert.Equal("deepseek", firstRequest.ProviderId);
            Assert.Equal("deepseek-v4-pro", firstRequest.ModelId);
            Assert.Equal("chat.general", firstRequest.PromptId);
            Assert.StartsWith(
                "{\"type\":\"USER_SELECTED_MEMORY_CONTEXT_V1\"",
                firstRequest.Messages[0].Content,
                StringComparison.Ordinal);
            using (var memoryContext = JsonDocument.Parse(firstRequest.Messages[0].Content))
            {
                var outboundItem = Assert.Single(
                    memoryContext.RootElement.GetProperty("items").EnumerateArray());
                Assert.Equal(updatedTitle, outboundItem.GetProperty("title").GetString());
                Assert.Equal(updatedBody, outboundItem.GetProperty("body").GetString());
            }

            Assert.Equal(firstQuestion, firstRequest.Messages[1].Content);

            Invoke(await WaitForElementByAutomationIdAsync(
                automationRoot,
                "NewConversation",
                TimeSpan.FromSeconds(10)));
            _ = await WaitForSessionAsync(
                api,
                snapshot => snapshot is { Title: "新话题" }
                            && snapshot.SessionId != waiting.SessionId,
                TimeSpan.FromSeconds(10));
            var (_, invalidationSelection) = await WaitForSelectableListItemAsync(
                new AndCondition(
                    new PropertyCondition(AutomationElement.ProcessIdProperty, clientProcess.Id),
                    new PropertyCondition(AutomationElement.NameProperty, updatedTitle),
                    new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.ListItem)),
                TimeSpan.FromSeconds(10));
            invalidationSelection.Select();
            var conversationInput = await WaitForElementByAutomationIdAsync(
                automationRoot,
                "ConversationInput",
                TimeSpan.FromSeconds(10));
            SetElementValue(conversationInput, invalidatedQuestion);
            Invoke(await WaitForElementByAutomationIdAsync(
                automationRoot,
                "SendConversation",
                TimeSpan.FromSeconds(10)));
            var invalidationWaiting = await WaitForSessionAsync(
                api,
                snapshot => snapshot?.ForegroundTurn?.Phase == "WaitingForMemoryOutboundConsent"
                            && snapshot.MemoryOutboundConsents?.Count == 1,
                TimeSpan.FromSeconds(10));
            var invalidatedTurnId = invalidationWaiting.ForegroundTurn!.Id;
            _ = Assert.Single(invalidationWaiting.MemoryOutboundConsents!);
            _ = await WaitForRenderedElementByAutomationIdAsync(
                automationRoot,
                "ConfirmMemoryOutbound",
                TimeSpan.FromSeconds(20));
            SetElementValue(conversationInput, "输入变化，撤销上一份确认快照。");
            _ = await WaitForTurnPhaseAsync(
                api,
                invalidatedTurnId,
                "Cancelled",
                TimeSpan.FromSeconds(10));
            Assert.DoesNotContain(
                provider.Requests,
                request => request.TurnId == invalidatedTurnId);
            Assert.Single(provider.Requests);

            Invoke(await WaitForElementByNameAsync(
                automationRoot,
                "设置",
                ControlType.Button,
                TimeSpan.FromSeconds(10)));
            var (_, memoryListSelection) = await WaitForSelectableListItemAsync(
                new AndCondition(
                    new PropertyCondition(AutomationElement.ProcessIdProperty, clientProcess.Id),
                    new PropertyCondition(AutomationElement.NameProperty, updatedTitle),
                    new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.ListItem)),
                TimeSpan.FromSeconds(10));
            memoryListSelection.Select();
            Invoke(await WaitForElementByAutomationIdAsync(
                automationRoot,
                "DeleteMemory",
                TimeSpan.FromSeconds(10)));
            var deleteDialog = await WaitForTopLevelAutomationWindowAsync(
                clientProcess.Id,
                "删除长期记忆",
                TimeSpan.FromSeconds(10));
            await SendDialogCommandAsync(deleteDialog, "6", TimeSpan.FromSeconds(10));
            await WaitUntilAsync(
                async () =>
                    (await api.ListMemoriesAsync()).All(item => item.Id != memoryId),
                TimeSpan.FromSeconds(10));
            AssertNoPlaintextCanary(dataRoot, updatedBody);
        }
        finally
        {
            await StopProcessAsync(clientProcess, entireProcessTree: false);
            if (host is not null)
            {
                await host.StopAsync().WaitAsync(TimeSpan.FromSeconds(10));
                host.Dispose();
            }

            await DeleteDirectoryWithRetryAsync(testRoot);
        }
    }

    [Fact]
    [Trait("Category", "DesktopAcceptance")]
    public async Task ActualReleaseMainWindowVoiceBargeInCancelsOldTurnAndRejectsLateReply()
    {
        var harnessPath = LocateReleaseVoiceHarness();
        Assert.True(File.Exists(harnessPath), "独立 WPF Voice Harness 尚未生成 Release 可执行程序。");

        using var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = harnessPath,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            }
        };
        process.StartInfo.ArgumentList.Add(LocateReleaseBinaries().FakeCodexPath);
        Assert.True(process.Start(), "无法启动独立 WPF Voice Harness。");
        var standardOutput = process.StandardOutput.ReadToEndAsync();
        var standardError = process.StandardError.ReadToEndAsync();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(90));
        try
        {
            await process.WaitForExitAsync(timeout.Token);
        }
        catch (OperationCanceledException) when (timeout.IsCancellationRequested)
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
                await process.WaitForExitAsync();
            }

            throw new TimeoutException("独立 WPF Voice Harness 未在失败上限内自然退出。");
        }

        var output = await standardOutput;
        var error = await standardError;
        var outputLines = output.Split(
                ['\r', '\n'],
                StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .ToArray();
        var resultLine = Assert.Single(outputLines);
        Assert.StartsWith(VoiceHarnessResultPrefix, resultLine, StringComparison.Ordinal);
        Assert.True(string.IsNullOrWhiteSpace(error), "Voice Harness 不应向标准错误输出内容。");

        var result = JsonSerializer.Deserialize<VoiceHarnessResult>(
            resultLine[VoiceHarnessResultPrefix.Length..],
            new JsonSerializerOptions(JsonSerializerDefaults.Web)
            {
                UnmappedMemberHandling =
                    System.Text.Json.Serialization.JsonUnmappedMemberHandling.Disallow
            });
        Assert.NotNull(result);
        Assert.True(process.ExitCode == 0,
            $"Voice Harness failed: {result.ErrorCode} ({result.ExceptionType}).");
        Assert.Equal("completed", result.Stage);
        Assert.Equal("Cancelled", result.OldTurnState);
        Assert.Equal("Completed", result.NewTurnState);
        Assert.True(result.EntryAssemblyConfirmed);
        Assert.True(result.ResourceAssemblyConfirmed);
        Assert.True(result.FirstInputVoice);
        Assert.True(result.SecondInputVoice);
        Assert.True(result.DistinctSessionTurns);
        Assert.True(result.OldTurnResponding);
        Assert.True(result.UiShowedOldInput);
        Assert.True(result.UiShowedStop);
        Assert.True(result.CancellationObserved);
        Assert.True(result.CancellationTurnMatched);
        Assert.True(result.LateReplyReturned);
        Assert.True(result.NewInputPersisted);
        Assert.True(result.NewReplyPersisted);
        Assert.True(result.LateReplyRejected);
        Assert.True(result.UiShowedNewInput);
        Assert.True(result.UiShowedNewReply);
        Assert.True(result.UiRejectedLateReply);
        Assert.True(result.NaturalShutdown);
        Assert.NotNull(result.Orb);
        Assert.True(result.Orb.PausedOrbWithoutListening);
        Assert.True(result.Orb.ToggleStartsOnce);
        Assert.True(result.Orb.ManagementCloseKeepsListening);
        Assert.True(result.Orb.RepeatedSurfaceDoesNotRestart);
        Assert.True(result.Orb.ExitStopsMicrophone);
        Assert.InRange(result.ElapsedMilliseconds, 1, 90_000);
        Assert.Null(result.ErrorCode);
        Assert.Null(result.ExceptionType);
    }

    [Fact]
    [Trait("Category", "DesktopAcceptance")]
    public async Task ActualReleaseClientKeepsProgrammingAgentAvailableWhenCodexChatIsDisabled()
    {
        var testRoot = Path.Combine(
            Path.GetTempPath(),
            $"screen-guide-stage2-programming-ui-{Guid.NewGuid():N}");
        var dataRoot = Path.Combine(testRoot, "user-data");
        var projectRoot = Path.Combine(testRoot, "authorized-project");
        var chatMarker = Path.Combine(testRoot, "chat-cli-must-not-run.txt");
        var programmingMarker = Path.Combine(projectRoot, "delayed-change.txt");
        Directory.CreateDirectory(dataRoot);
        Directory.CreateDirectory(projectRoot);
        await RunGitAsync(projectRoot, "init", "--quiet");
        await WriteCompletedOnboardingSettingsAsync(dataRoot);

        Process? clientProcess = null;
        IHost? host = null;
        try
        {
            var binaries = LocateReleaseBinaries();
            var pipeName = $"ScreenGuide.Stage2ProgrammingUi.{Guid.NewGuid():N}";
            var options = new DesktopHostOptions(dataRoot, binaries.FakeCodexPath, pipeName);
            // Exercise a persisted legacy Codex route; the current default is intentionally different.
            var legacyRoute = new AiSettings(new ChatModelRoute("codex", "codex-default"));
            using (var routeStore = new FileAiSettingsStore(options.AiSettingsPath, legacyRoute))
                await routeStore.SaveAsync(legacyRoute);
            host = DesktopHostFactory.Build([], options);
            await host.StartAsync();

            var api = new DesktopApiClient(pipeName, TimeSpan.FromSeconds(5));
            await WaitUntilAsync(() => api.PingAsync(), TimeSpan.FromSeconds(20));
            var project = await api.AddProjectAsync(new AddProjectRequestDto(
                projectRoot,
                "Stage2 isolated Fake Codex project"));
            clientProcess = StartClient(binaries, dataRoot, pipeName);
            var clientWindow = await WaitForMainWindowAsync(
                clientProcess,
                dataRoot,
                TimeSpan.FromSeconds(15));
            var automationRoot = AutomationElement.FromHandle(clientWindow)
                ?? throw new InvalidOperationException("无法连接实际 DesktopClient 的 WPF 自动化树。");

            Invoke(await WaitForElementByAutomationIdAsync(
                automationRoot,
                "NewTopic",
                TimeSpan.FromSeconds(10)));
            var session = await WaitForSessionAsync(
                api,
                snapshot => snapshot is { Title: "新话题" },
                TimeSpan.FromSeconds(10));
            var failedChat = await api.SubmitSessionInputAsync(new SessionInputRequestDto(
                $"这只是普通聊天，不能启动编程进程。{Environment.NewLine}" +
                $"TEST_LONG_RUNNING{Environment.NewLine}MARKER={chatMarker}",
                "Text",
                $"stage2-ui-disabled-chat-{Guid.NewGuid():N}",
                session.SessionId));
            var chatFailed = await WaitForTurnPhaseAsync(
                api,
                failedChat.TurnId,
                "Failed",
                TimeSpan.FromSeconds(10));
            var failedTurn = chatFailed.Turns.Single(turn => turn.Id == failedChat.TurnId);
            Assert.Contains("Codex 普通聊天", failedTurn.FailureMessage, StringComparison.Ordinal);
            Assert.Contains("出于安全原因", failedTurn.FailureMessage, StringComparison.Ordinal);
            Assert.Contains("当前版本暂不提供", failedTurn.FailureMessage, StringComparison.Ordinal);
            Assert.Contains("编程任务不受影响", failedTurn.FailureMessage, StringComparison.Ordinal);
            Assert.False(File.Exists(chatMarker));

            var taskIdsBefore = (await api.ListTasksAsync())
                .Select(task => task.Id)
                .ToHashSet();
            Invoke(await WaitForElementByNameAsync(
                automationRoot,
                "编程任务",
                ControlType.Button,
                TimeSpan.FromSeconds(10)));
            var projectCombo = await WaitForComboBoxWithSelectedItemAsync(
                automationRoot,
                project.Name,
                TimeSpan.FromSeconds(10));
            Assert.Contains(
                project.Name,
                SelectedItemName(projectCombo),
                StringComparison.Ordinal);
            await SetLastVisibleEditableTextBoxValueAsync(
                automationRoot,
                "TEST_DELAYED_SUCCESS",
                TimeSpan.FromSeconds(10));
            Invoke(await WaitForElementByNameAsync(
                automationRoot,
                "确认并开始任务",
                ControlType.Button,
                TimeSpan.FromSeconds(10)));

            _ = await WaitForElementByNameAsync(
                automationRoot,
                "当前任务",
                ControlType.Text,
                TimeSpan.FromSeconds(10));
            var task = await WaitForNewTaskTerminalAsync(
                api,
                taskIdsBefore,
                TimeSpan.FromSeconds(20));
            Assert.Equal(project.Id, task.Summary.ProjectId);
            Assert.Equal("Succeeded", task.Summary.Status);
            Assert.True(
                File.Exists(programmingMarker),
                "真实 DesktopClient 启动的隔离 Fake Codex 编程任务没有生成预期标记。");
            Assert.False(File.Exists(chatMarker));
        }
        finally
        {
            await StopProcessAsync(clientProcess, entireProcessTree: false);
            if (host is not null)
            {
                await host.StopAsync().WaitAsync(TimeSpan.FromSeconds(10));
                host.Dispose();
            }

            await DeleteDirectoryWithRetryAsync(testRoot);
        }
    }

    private static async Task WriteCompletedOnboardingSettingsAsync(string dataRoot) =>
        await File.WriteAllTextAsync(
            Path.Combine(dataRoot, "client-settings.json"),
            JsonSerializer.Serialize(
                new
                {
                    startWithWindows = false,
                    runInBackground = false,
                    closeToTray = false,
                    notificationsEnabled = false,
                    onboardingCompleted = true,
                    notificationStateInitialized = true,
                    deliveredNotificationKeys = Array.Empty<string>()
                },
                new JsonSerializerOptions { WriteIndented = true }));

    private static Process StartClient(Binaries binaries, string dataRoot, string pipeName)
    {
        var info = new ProcessStartInfo
        {
            FileName = binaries.ClientPath,
            WorkingDirectory = Path.GetDirectoryName(binaries.ClientPath)!,
            UseShellExecute = false
        };
        info.Environment[DesktopHostOptions.DataDirectoryEnvironmentVariable] = dataRoot;
        info.Environment[DesktopHostOptions.PipeNameEnvironmentVariable] = pipeName;
        info.Environment["SCREEN_GUIDE_DESKTOP_HOST_PATH"] = binaries.HostPath;
        info.Environment[DesktopHostOptions.CodexExecutableEnvironmentVariable] = binaries.FakeCodexPath;
        info.ArgumentList.Add("--show");
        return Process.Start(info)
               ?? throw new InvalidOperationException("实际 Release DesktopClient 未启动。");
    }

    private static async Task RunGitAsync(string workingDirectory, params string[] arguments)
    {
        using var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = "git",
                WorkingDirectory = workingDirectory,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            }
        };
        foreach (var argument in arguments)
        {
            process.StartInfo.ArgumentList.Add(argument);
        }

        Assert.True(process.Start(), "无法启动测试所需的本机 Git。");
        var output = process.StandardOutput.ReadToEndAsync();
        var error = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();
        Assert.True(
            process.ExitCode == 0,
            $"Git {string.Join(' ', arguments)} 失败：{await output}{await error}");
    }

    private static string GetFinalPath(string path)
    {
        var fullPath = Path.GetFullPath(path);
        using var stream = new FileStream(
            fullPath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete);
        var buffer = new StringBuilder(32_768);
        var length = GetFinalPathNameByHandle(
            stream.SafeFileHandle.DangerousGetHandle(),
            buffer,
            buffer.Capacity,
            0);
        Assert.True(length > 0 && length < buffer.Capacity, $"无法规范化测试路径：{fullPath}");
        return buffer.ToString().StartsWith("\\\\?\\", StringComparison.Ordinal)
            ? buffer.ToString()[4..]
            : buffer.ToString();
    }

    private static async Task<IntPtr> WaitForMainWindowAsync(
        Process process,
        string dataRoot,
        TimeSpan timeout)
    {
        var deadline = DateTimeOffset.UtcNow + timeout;
        while (DateTimeOffset.UtcNow < deadline)
        {
            process.Refresh();
            if (process.HasExited)
            {
                throw new InvalidOperationException(
                    $"DesktopClient 已退出，退出码 {process.ExitCode}；{ReadStartupError(dataRoot)}");
            }

            var window = FindTopLevelWindow(process.Id, "元枢 · 管理");
            if (window != IntPtr.Zero)
            {
                return window;
            }

            await Task.Delay(200);
        }

        throw new TimeoutException($"实际 DesktopClient 主窗口未出现；{ReadStartupError(dataRoot)}");
    }

    private static async Task<AutomationElement> WaitForElementByAutomationIdAsync(
        AutomationElement root,
        string automationId,
        TimeSpan timeout) =>
        await WaitForElementAsync(
            root,
            new PropertyCondition(AutomationElement.AutomationIdProperty, automationId),
            timeout);

    private static async Task<AutomationElement> WaitForRenderedElementByAutomationIdAsync(
        AutomationElement root,
        string automationId,
        TimeSpan timeout)
    {
        AutomationElement? match = null;
        var lastState = "automation element not found";
        try
        {
            await WaitUntilAsync(
                () =>
                {
                    try
                    {
                        match = root.FindFirst(
                            TreeScope.Descendants,
                            new PropertyCondition(
                                AutomationElement.AutomationIdProperty,
                                automationId));
                        if (match is null)
                        {
                            lastState = "automation element not found";
                            return Task.FromResult(false);
                        }

                        var bounds = match.Current.BoundingRectangle;
                        lastState = $"enabled={match.Current.IsEnabled}, offscreen={match.Current.IsOffscreen}, bounds={bounds}";
                        return Task.FromResult(
                            match.Current.IsEnabled
                            && bounds.Width > 0
                            && bounds.Height > 0);
                    }
                    catch (Exception exception) when (
                        exception is ElementNotAvailableException or COMException)
                    {
                        lastState = exception.GetType().Name;
                        return Task.FromResult(false);
                    }
                },
                timeout);
        }
        catch (TimeoutException exception)
        {
            throw new TimeoutException(
                $"UI element '{automationId}' did not render: {lastState}.",
                exception);
        }

        return match!;
    }

    private static void SetElementValue(AutomationElement element, string value)
    {
        Assert.True(element.Current.IsEnabled, $"控件 {element.Current.AutomationId} 当前不可用。");
        Assert.True(
            element.TryGetCurrentPattern(ValuePattern.Pattern, out var rawPattern),
            $"控件 {element.Current.AutomationId} 不支持 ValuePattern。");
        ((ValuePattern)rawPattern).SetValue(value);
    }

    private static async Task<AutomationElement> WaitForElementByNameAsync(
        AutomationElement root,
        string name,
        ControlType controlType,
        TimeSpan timeout) =>
        await WaitForElementAsync(
            root,
            new AndCondition(
                new PropertyCondition(AutomationElement.NameProperty, name),
                new PropertyCondition(AutomationElement.ControlTypeProperty, controlType)),
            timeout);

    private static async Task WaitForAutomationTextAsync(
        AutomationElement root,
        string automationId,
        string expectedText,
        TimeSpan timeout) =>
        await WaitUntilAsync(
            () =>
            {
                try
                {
                    var element = root.FindFirst(
                        TreeScope.Descendants,
                        new PropertyCondition(
                            AutomationElement.AutomationIdProperty,
                            automationId));
                    return Task.FromResult(
                        element?.Current.Name.Contains(expectedText, StringComparison.Ordinal)
                        == true);
                }
                catch (Exception exception) when (
                    exception is ElementNotAvailableException or COMException)
                {
                    return Task.FromResult(false);
                }
            },
            timeout);

    private static async Task<AutomationElement> WaitForElementAsync(
        AutomationElement root,
        Condition condition,
        TimeSpan timeout)
    {
        AutomationElement? match = null;
        await WaitUntilAsync(
            () =>
            {
                try
                {
                    match = root.FindFirst(TreeScope.Descendants, condition);
                    return Task.FromResult(match is not null);
                }
                catch (Exception exception) when (exception is ElementNotAvailableException or COMException)
                {
                    return Task.FromResult(false);
                }
            },
            timeout);
        return match!;
    }

    private static async Task SelectComboBoxItemAsync(
        AutomationElement comboBox,
        int processId,
        string itemName,
        TimeSpan timeout)
    {
        Assert.True(
            comboBox.TryGetCurrentPattern(ExpandCollapsePattern.Pattern, out var rawExpand),
            $"ComboBox {comboBox.Current.AutomationId} 不支持展开。");
        var expand = (ExpandCollapsePattern)rawExpand;
        expand.Expand();
        try
        {
            var (item, selection) = await WaitForSelectableListItemAsync(
                new AndCondition(
                    new PropertyCondition(AutomationElement.ProcessIdProperty, processId),
                    new PropertyCondition(AutomationElement.NameProperty, itemName),
                    new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.ListItem)),
                timeout);
            selection.Select();

            await WaitUntilAsync(
                () => Task.FromResult(SelectedItemName(comboBox) == itemName),
                timeout);
        }
        finally
        {
            try
            {
                expand.Collapse();
            }
            catch (ElementNotAvailableException)
            {
            }
        }
    }

    private static async Task<(AutomationElement Item, SelectionItemPattern Selection)>
        WaitForSelectableListItemAsync(
            Condition condition,
            TimeSpan timeout)
    {
        AutomationElement? match = null;
        SelectionItemPattern? selection = null;
        await WaitUntilAsync(
            () =>
            {
                try
                {
                    var candidates = AutomationElement.RootElement.FindAll(
                        TreeScope.Descendants,
                        condition);
                    for (var index = 0; index < candidates.Count; index++)
                    {
                        var candidate = candidates[index];
                        if (!candidate.TryGetCurrentPattern(
                                SelectionItemPattern.Pattern,
                                out var rawSelection))
                        {
                            continue;
                        }

                        match = candidate;
                        selection = (SelectionItemPattern)rawSelection;
                        return Task.FromResult(true);
                    }

                    return Task.FromResult(false);
                }
                catch (Exception exception) when (
                    exception is ElementNotAvailableException or COMException)
                {
                    return Task.FromResult(false);
                }
            },
            timeout);

        return (match!, selection!);
    }

    private static string? SelectedItemName(AutomationElement comboBox)
    {
        try
        {
            if (!comboBox.TryGetCurrentPattern(SelectionPattern.Pattern, out var rawSelection))
            {
                return null;
            }

            return ((SelectionPattern)rawSelection).Current.GetSelection().SingleOrDefault()?.Current.Name;
        }
        catch (Exception exception) when (exception is ElementNotAvailableException or COMException)
        {
            return null;
        }
    }

    private static async Task<AutomationElement> WaitForComboBoxWithSelectedItemAsync(
        AutomationElement root,
        string selectedItemName,
        TimeSpan timeout)
    {
        AutomationElement? match = null;
        await WaitUntilAsync(
            () =>
            {
                try
                {
                    var candidates = root.FindAll(
                        TreeScope.Descendants,
                        new PropertyCondition(
                            AutomationElement.ControlTypeProperty,
                            ControlType.ComboBox));
                    match = candidates.Cast<AutomationElement>().FirstOrDefault(candidate =>
                        candidate.Current.IsEnabled
                        && !candidate.Current.IsOffscreen
                        && SelectedItemName(candidate)?.Contains(
                            selectedItemName,
                            StringComparison.Ordinal) == true);
                    return Task.FromResult(match is not null);
                }
                catch (Exception exception) when (
                    exception is ElementNotAvailableException or COMException)
                {
                    return Task.FromResult(false);
                }
            },
            timeout);
        return match!;
    }

    private static async Task SetLastVisibleEditableTextBoxValueAsync(
        AutomationElement root,
        string value,
        TimeSpan timeout)
    {
        AutomationElement? match = null;
        ValuePattern? valuePattern = null;
        await WaitUntilAsync(
            () =>
            {
                try
                {
                    var candidates = root.FindAll(
                            TreeScope.Descendants,
                            new PropertyCondition(
                                AutomationElement.ControlTypeProperty,
                                ControlType.Edit))
                        .Cast<AutomationElement>()
                        .Where(candidate => candidate.Current.IsEnabled
                                            && !candidate.Current.IsOffscreen
                                            && !candidate.Current.IsPassword
                                            && candidate.TryGetCurrentPattern(
                                                ValuePattern.Pattern,
                                                out _))
                        .OrderBy(candidate => candidate.Current.BoundingRectangle.Top)
                        .ToArray();
                    match = candidates.LastOrDefault();
                    if (match is null
                        || !match.TryGetCurrentPattern(
                            ValuePattern.Pattern,
                            out var rawValuePattern))
                    {
                        return Task.FromResult(false);
                    }

                    valuePattern = (ValuePattern)rawValuePattern;
                    return Task.FromResult(true);
                }
                catch (Exception exception) when (
                    exception is ElementNotAvailableException or COMException)
                {
                    return Task.FromResult(false);
                }
            },
            timeout);

        valuePattern!.SetValue(value);
        Assert.Equal(value, valuePattern.Current.Value);
    }

    private static async Task ChooseFileInDialogAsync(
        AutomationElement dialog,
        string filePath,
        TimeSpan timeout)
    {
        ValuePattern? fileNameValue = null;
        var processId = dialog.Current.ProcessId;
        var dialogTitle = dialog.Current.Name;
        await WaitUntilAsync(
            () =>
            {
                try
                {
                    var edits = dialog.FindAll(
                            TreeScope.Descendants,
                            new PropertyCondition(
                                AutomationElement.ControlTypeProperty,
                                ControlType.Edit))
                        .Cast<AutomationElement>()
                        .Where(candidate => candidate.Current.IsEnabled
                                            && candidate.TryGetCurrentPattern(
                                                ValuePattern.Pattern,
                                                out _))
                        .ToArray();
                    var fileName = edits.SingleOrDefault(candidate =>
                        string.Equals(
                            candidate.Current.AutomationId,
                            "1148",
                            StringComparison.Ordinal)
                        && candidate.Current.Name.Contains(
                            "文件名",
                            StringComparison.Ordinal));
                    if (fileName is null
                        || !fileName.TryGetCurrentPattern(
                            ValuePattern.Pattern,
                            out var rawFileNameValue))
                    {
                        return Task.FromResult(false);
                    }

                    fileNameValue = (ValuePattern)rawFileNameValue;
                    return Task.FromResult(true);
                }
                catch (Exception exception) when (
                    exception is ElementNotAvailableException or COMException)
                {
                    return Task.FromResult(false);
                }
            },
            timeout);

        var fullPath = Path.GetFullPath(filePath);
        fileNameValue!.SetValue(fullPath);
        Assert.Equal(fullPath, fileNameValue.Current.Value);
        var dialogHandle = new IntPtr(dialog.Current.NativeWindowHandle);
        Assert.NotEqual(IntPtr.Zero, dialogHandle);
        _ = SendMessage(dialogHandle, 0x0111, new IntPtr(1), IntPtr.Zero); // WM_COMMAND / IDOK

        await WaitUntilAsync(
            () => Task.FromResult(
                FindTopLevelWindow(processId, dialogTitle) == IntPtr.Zero),
            timeout);
    }

    private static async Task SendDialogCommandAsync(
        AutomationElement dialog,
        string automationId,
        TimeSpan timeout)
    {
        var button = await WaitForElementAsync(
            dialog,
            new AndCondition(
                new PropertyCondition(
                    AutomationElement.AutomationIdProperty,
                    automationId),
                new PropertyCondition(
                    AutomationElement.ControlTypeProperty,
                    ControlType.Button)),
            timeout);
        Assert.True(button.Current.IsEnabled && !button.Current.IsOffscreen);
        Assert.True(int.TryParse(automationId, out var commandId));
        var dialogHandle = new IntPtr(dialog.Current.NativeWindowHandle);
        Assert.NotEqual(IntPtr.Zero, dialogHandle);
        _ = SendMessage(dialogHandle, 0x0111, new IntPtr(commandId), IntPtr.Zero);
    }

    private static async Task<TaskDetailsDto> WaitForNewTaskTerminalAsync(
        IDesktopApiClient api,
        IReadOnlySet<Guid> existingTaskIds,
        TimeSpan timeout)
    {
        TaskDetailsDto? details = null;
        await WaitUntilAsync(
            async () =>
            {
                var summary = (await api.ListTasksAsync())
                    .FirstOrDefault(task => !existingTaskIds.Contains(task.Id));
                if (summary is null)
                {
                    return false;
                }

                details = await api.GetTaskAsync(summary.Id);
                return details?.Summary.Status is "Succeeded" or "Failed" or "Cancelled";
            },
            timeout);
        return details!;
    }

    private static async Task<SessionSnapshotDto> WaitForSessionAsync(
        IDesktopApiClient api,
        Func<SessionSnapshotDto?, bool> condition,
        TimeSpan timeout)
    {
        SessionSnapshotDto? snapshot = null;
        await WaitUntilAsync(
            async () =>
            {
                snapshot = await api.GetCurrentSessionAsync();
                return condition(snapshot);
            },
            timeout);
        return snapshot!;
    }

    private static async Task<SessionSnapshotDto> WaitForTurnPhaseAsync(
        IDesktopApiClient api,
        Guid turnId,
        string expectedPhase,
        TimeSpan timeout) =>
        await WaitForSessionAsync(
            api,
            snapshot => snapshot?.Turns.Any(turn =>
                turn.Id == turnId
                && string.Equals(turn.Phase, expectedPhase, StringComparison.Ordinal)) == true,
            timeout);

    private static async Task<AutomationElement> WaitForTopLevelAutomationWindowAsync(
        int processId,
        string title,
        TimeSpan timeout)
    {
        AutomationElement? window = null;
        await WaitUntilAsync(
            () =>
            {
                var handle = FindTopLevelWindow(processId, title);
                window = handle == IntPtr.Zero ? null : AutomationElement.FromHandle(handle);
                return Task.FromResult(window is not null);
            },
            timeout);
        return window!;
    }

    private static async Task WaitForTopLevelWindowToCloseAsync(
        int processId,
        string title,
        TimeSpan timeout) =>
        await WaitUntilAsync(
            () => Task.FromResult(FindTopLevelWindow(processId, title) == IntPtr.Zero),
            timeout);

    private static void AssertNoAutomationNameContains(AutomationElement root, string secret)
    {
        var elements = root.FindAll(TreeScope.Subtree, Condition.TrueCondition);
        foreach (AutomationElement element in elements)
        {
            Assert.DoesNotContain(secret, element.Current.Name, StringComparison.Ordinal);
        }
    }

    private static void AssertAutomationTreeContains(AutomationElement root, string expectedText)
    {
        var elements = root.FindAll(TreeScope.Subtree, Condition.TrueCondition);
        Assert.Contains(
            elements.Cast<AutomationElement>(),
            element => element.Current.Name.Contains(expectedText, StringComparison.Ordinal));
    }

    private static void AssertNoPlaintextCanary(string dataRoot, string canary)
    {
        var needle = Encoding.UTF8.GetBytes(canary);
        foreach (var file in Directory.EnumerateFiles(dataRoot, "*", SearchOption.AllDirectories))
        {
            using var stream = new FileStream(
                file,
                FileMode.Open,
                FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete);
            Assert.False(
                StreamContains(stream, needle),
                $"隔离数据目录不应明文保存 Fake Key：{file}");
        }
    }

    private static bool StreamContains(Stream stream, byte[] needle)
    {
        var buffer = new byte[8 * 1024 + needle.Length];
        var preserved = 0;
        while (true)
        {
            var read = stream.Read(buffer, preserved, 8 * 1024);
            if (read == 0)
            {
                return false;
            }

            var available = preserved + read;
            if (buffer.AsSpan(0, available).IndexOf(needle) >= 0)
            {
                return true;
            }

            preserved = Math.Min(needle.Length - 1, available);
            buffer.AsSpan(available - preserved, preserved).CopyTo(buffer);
        }
    }

    private static void AssertChatMessage(
        ChatMessage actual,
        ChatMessageRole expectedRole,
        string expectedContent)
    {
        Assert.Equal(expectedRole, actual.Role);
        Assert.Equal(expectedContent, actual.Content);
    }

    private static void Invoke(AutomationElement element)
    {
        Assert.True(element.Current.IsEnabled, $"控件 {element.Current.Name} 当前不可用。");
        Assert.True(
            element.TryGetCurrentPattern(InvokePattern.Pattern, out var rawPattern),
            $"控件 {element.Current.Name} 不支持 InvokePattern。");
        ((InvokePattern)rawPattern).Invoke();
    }

    private static async Task SelectComboBoxItemWithKeyboardAsync(
        AutomationElement comboBox,
        AutomationElement item)
    {
        var position = item.GetCurrentPropertyValue(
            AutomationElement.PositionInSetProperty,
            ignoreDefaultValue: true);
        var oneBasedPosition = position is int reportedPosition && reportedPosition > 0
            ? reportedPosition
            : PositionAmongListItemSiblings(item) + 1;
        Assert.True(oneBasedPosition > 0, $"无法确定 {item.Current.Name} 的下拉列表位置。");

        comboBox.SetFocus();
        await Task.Delay(75);
        SendVirtualKey(0x24); // Home
        for (var index = 1; index < oneBasedPosition; index++)
        {
            await Task.Delay(50);
            SendVirtualKey(0x28); // Down
        }

        await Task.Delay(75);
        SendVirtualKey(0x0D); // Enter
        await Task.Delay(150);
    }

    private static int PositionAmongListItemSiblings(AutomationElement item)
    {
        var parent = TreeWalker.ControlViewWalker.GetParent(item);
        if (parent is null)
        {
            return -1;
        }

        var siblings = parent.FindAll(
            TreeScope.Children,
            new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.ListItem));
        var targetRuntimeId = item.GetRuntimeId();
        for (var index = 0; index < siblings.Count; index++)
        {
            if (siblings[index].GetRuntimeId().SequenceEqual(targetRuntimeId))
            {
                return index;
            }
        }

        return -1;
    }

    private static void SendVirtualKey(byte virtualKey)
    {
        KeyboardEvent(virtualKey, 0, 0, UIntPtr.Zero);
        KeyboardEvent(virtualKey, 0, 0x0002, UIntPtr.Zero);
    }

    private static async Task WaitUntilAsync(Func<Task<bool>> condition, TimeSpan timeout)
    {
        var deadline = DateTimeOffset.UtcNow + timeout;
        Exception? lastError = null;
        while (DateTimeOffset.UtcNow < deadline)
        {
            try
            {
                if (await condition())
                {
                    return;
                }
            }
            catch (Exception exception) when (
                exception is IOException
                    or DesktopApiException
                    or ElementNotAvailableException
                    or COMException)
            {
                lastError = exception;
            }

            await Task.Delay(150);
        }

        throw new TimeoutException(
            lastError is null
                ? "Stage 2 真实桌面验收条件超时。"
                : $"Stage 2 真实桌面验收条件超时：{lastError.Message}");
    }

    private static IntPtr FindTopLevelWindow(int processId, string expectedTitle)
    {
        var match = IntPtr.Zero;
        EnumWindows(
            (window, ignored) =>
            {
                _ = GetWindowThreadProcessId(window, out var ownerProcessId);
                if (ownerProcessId != processId || !IsWindowVisible(window))
                {
                    return true;
                }

                var length = GetWindowTextLength(window);
                var title = new StringBuilder(Math.Max(1, length + 1));
                _ = GetWindowText(window, title, title.Capacity);
                if (!string.Equals(title.ToString(), expectedTitle, StringComparison.Ordinal))
                {
                    return true;
                }

                match = window;
                return false;
            },
            IntPtr.Zero);
        return match;
    }

    private static string ReadStartupError(string dataRoot)
    {
        var errorPath = Path.Combine(dataRoot, "state", "client-startup-error.json");
        var stagePath = Path.Combine(dataRoot, "state", "client-startup-stage.txt");
        return $"stage={(File.Exists(stagePath) ? File.ReadAllText(stagePath) : "none")}; "
               + $"error={(File.Exists(errorPath) ? File.ReadAllText(errorPath) : "none")}";
    }

    private static async Task StopProcessAsync(Process? process, bool entireProcessTree)
    {
        if (process is null)
        {
            return;
        }

        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree);
                await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10));
            }
        }
        catch (Exception exception) when (exception is InvalidOperationException or TimeoutException)
        {
        }
        finally
        {
            process.Dispose();
        }
    }

    private static async Task DeleteDirectoryWithRetryAsync(string path)
    {
        var normalized = Path.GetFullPath(path);
        var temporaryRoot = Path.GetFullPath(Path.GetTempPath());
        Assert.StartsWith(temporaryRoot, normalized, StringComparison.OrdinalIgnoreCase);
        for (var attempt = 0; attempt < 8; attempt++)
        {
            try
            {
                if (Directory.Exists(normalized))
                {
                    Directory.Delete(normalized, recursive: true);
                }

                return;
            }
            catch (Exception exception) when (
                exception is IOException or UnauthorizedAccessException
                    && attempt < 7)
            {
                await Task.Delay(250);
            }
        }
    }

    private static Binaries LocateReleaseBinaries()
    {
        var repository = FindRepositoryRoot();
        const string framework = "net10.0-windows10.0.19041.0";
        var client = Path.Combine(
            repository,
            "src",
            "ScreenGuide.DesktopClient",
            "bin",
            "Release",
            framework,
            "ScreenGuide.DesktopClient.exe");
        var host = Path.Combine(
            repository,
            "src",
            "ScreenGuide.DesktopHost",
            "bin",
            "Release",
            framework,
            "ScreenGuide.DesktopHost.exe");
        var fakeCodex = Path.ChangeExtension(typeof(FakeCodexMarker).Assembly.Location, ".exe");
        Assert.True(File.Exists(client), $"缺少实际 Release DesktopClient：{client}");
        Assert.True(File.Exists(host), $"缺少实际 Release DesktopHost：{host}");
        Assert.True(File.Exists(fakeCodex), $"缺少 FakeCodex：{fakeCodex}");
        return new Binaries(client, host, fakeCodex);
    }

    private static string LocateReleaseVoiceHarness()
    {
        var repositoryRoot = FindRepositoryRoot();
        return Path.Combine(
            repositoryRoot,
            "tests",
            "ScreenGuide.DesktopClient.VoiceHarness",
            "bin",
            "Release",
            "net10.0-windows10.0.19041.0",
            "ScreenGuide.DesktopClient.VoiceHarness.exe");
    }

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null
               && !File.Exists(Path.Combine(directory.FullName, "ScreenGuide.slnx")))
        {
            directory = directory.Parent;
        }

        return directory?.FullName
               ?? throw new DirectoryNotFoundException("没有找到 ScreenGuide 仓库根目录。");
    }

    private sealed record Binaries(string ClientPath, string HostPath, string FakeCodexPath);

    private sealed record VoiceHarnessResult(
        string Stage,
        string OldTurnState,
        string NewTurnState,
        bool EntryAssemblyConfirmed,
        bool ResourceAssemblyConfirmed,
        bool FirstInputVoice,
        bool SecondInputVoice,
        bool DistinctSessionTurns,
        bool OldTurnResponding,
        bool UiShowedOldInput,
        bool UiShowedStop,
        bool CancellationObserved,
        bool CancellationTurnMatched,
        bool LateReplyReturned,
        bool NewInputPersisted,
        bool NewReplyPersisted,
        bool LateReplyRejected,
        bool UiShowedNewInput,
        bool UiShowedNewReply,
        bool UiRejectedLateReply,
        bool NaturalShutdown,
        long ElapsedMilliseconds,
        string? ErrorCode,
        string? ExceptionType,
        OrbHarnessEvidence? Orb);

    private sealed record OrbHarnessEvidence(
        bool PausedOrbWithoutListening,
        bool ToggleStartsOnce,
        bool ManagementCloseKeepsListening,
        bool RepeatedSurfaceDoesNotRestart,
        bool ExitStopsMicrophone);

    private sealed class RecordingChatProvider : IChatModelProvider
    {
        private readonly ConcurrentDictionary<Guid, CancellationTokenSource> _active = new();

        private RecordingChatProvider(ChatProviderDescriptor descriptor) => Descriptor = descriptor;

        public ChatProviderDescriptor Descriptor { get; }

        public ConcurrentQueue<ObservedRequest> Requests { get; } = new();

        public TaskCompletionSource<Guid> BlockingTurnStarted { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource<Guid> SemanticBlockingTurnStarted { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource AllowSemanticCompletion { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource<Guid> CancellationObserved { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource AllowLateReply { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource<Guid> LateReplyReturned { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public static RecordingChatProvider Codex() => new(new ChatProviderDescriptor(
            "codex",
            "Codex",
            "OpenAI Codex cloud（Fake acceptance provider）",
            SendsDataOffDevice: true,
            [new ChatModelDescriptor(
                "codex-default",
                "Codex Chat",
                ChatModelCapabilities.Streaming | ChatModelCapabilities.JsonObjectOutput)],
            ChatProviderCredentialKind.None,
            ChatProviderWorkloads.OrdinaryChat | ChatProviderWorkloads.ProgrammingAgent));

        public static RecordingChatProvider DeepSeek() => new(new ChatProviderDescriptor(
            "deepseek",
            "DeepSeek",
            "https://api.deepseek.com",
            SendsDataOffDevice: true,
            [
                new ChatModelDescriptor(
                    "deepseek-v4-flash",
                    "DeepSeek V4 Flash",
                    ChatModelCapabilities.Streaming | ChatModelCapabilities.JsonObjectOutput),
                new ChatModelDescriptor(
                    "deepseek-v4-pro",
                    "DeepSeek V4 Pro",
                    ChatModelCapabilities.Streaming | ChatModelCapabilities.JsonObjectOutput)
            ],
            ChatProviderCredentialKind.ApiKey));

        public static RecordingChatProvider Qwen() => new(new ChatProviderDescriptor(
            "qwen",
            "千问",
            "https://dashscope.aliyuncs.com",
            SendsDataOffDevice: true,
            [new ChatModelDescriptor(
                "qwen3.7-plus",
                "千问 3.7 Plus",
                ChatModelCapabilities.Streaming | ChatModelCapabilities.JsonObjectOutput)],
            ChatProviderCredentialKind.ApiKey));

        public Task<ChatProviderHealth> CheckHealthAsync(
            CancellationToken cancellationToken = default) =>
            Task.FromResult(new ChatProviderHealth(
                Descriptor.ProviderId,
                ChatProviderHealthState.Healthy,
                IsConfigured: true,
                $"{Descriptor.DisplayName} Fake Provider 连接正常。",
                DateTimeOffset.UtcNow));

        public Task<ChatModelResponse> CompleteAsync(
            ChatModelRequest request,
            ChatModelStreamCallback? streamCallback = null,
            CancellationToken cancellationToken = default)
        {
            var completion = CompleteCoreAsync(request, cancellationToken);
            if (!string.Equals(
                    request.Prompt?.PromptId,
                    "intent.semantic",
                    StringComparison.Ordinal)
                && string.Equals(
                    request.Messages.Last().Content,
                    "STAGE2_BLOCK_THEN_LATE_RESULT",
                    StringComparison.Ordinal))
            {
                _ = completion.ContinueWith(
                    completed =>
                    {
                        if (completed.Status == TaskStatus.RanToCompletion)
                        {
                            LateReplyReturned.TrySetResult(request.TurnId);
                        }
                    },
                    CancellationToken.None,
                    TaskContinuationOptions.ExecuteSynchronously,
                    TaskScheduler.Default);
            }

            return completion;
        }

        private async Task<ChatModelResponse> CompleteCoreAsync(
            ChatModelRequest request,
            CancellationToken cancellationToken)
        {
            using var local = new CancellationTokenSource();
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(
                cancellationToken,
                local.Token);
            if (!_active.TryAdd(request.TurnId, local))
            {
                throw new InvalidOperationException("Turn already active in fake provider.");
            }

            try
            {
                Requests.Enqueue(new ObservedRequest(
                    request.TurnId,
                    Descriptor.ProviderId,
                    request.ModelId,
                    request.Prompt?.PromptId,
                    request.Messages.ToArray()));
                var latest = request.Messages.Last().Content;
                if (string.Equals(
                        request.Prompt?.PromptId,
                        "intent.semantic",
                        StringComparison.Ordinal))
                {
                    if (latest.Contains("阶段二第一轮代号", StringComparison.Ordinal))
                    {
                        SemanticBlockingTurnStarted.TrySetResult(request.TurnId);
                        await AllowSemanticCompletion.Task.WaitAsync(linked.Token);
                    }

                    const string structured =
                        "{\"kind\":\"Conversation\",\"target\":null,\"confidence\":0.99," +
                        "\"isAmbiguous\":false,\"missingContext\":\"None\"}";
                    return new ChatModelResponse(
                        structured,
                        ChatFinishReason.Stop,
                        new ChatModelUsage(10, 5, 15),
                        new ChatProviderMetadata(
                            Descriptor.ProviderId,
                            request.ModelId,
                            $"fake-{Guid.NewGuid():N}",
                            Descriptor.DataDestination),
                        StructuredJson: structured);
                }

                if (string.Equals(
                        latest,
                        "STAGE2_BLOCK_THEN_LATE_RESULT",
                        StringComparison.Ordinal))
                {
                    BlockingTurnStarted.TrySetResult(request.TurnId);
                    try
                    {
                        await Task.Delay(Timeout.InfiniteTimeSpan, linked.Token);
                    }
                    catch (OperationCanceledException) when (linked.IsCancellationRequested)
                    {
                        CancellationObserved.TrySetResult(request.TurnId);
                        await AllowLateReply.Task;
                        return new ChatModelResponse(
                            $"{Descriptor.DisplayName}/{request.ModelId} 第一条迟到回答",
                            ChatFinishReason.Stop,
                            new ChatModelUsage(10, 5, 15),
                            new ChatProviderMetadata(
                                Descriptor.ProviderId,
                                request.ModelId,
                                $"fake-{Guid.NewGuid():N}",
                                Descriptor.DataDestination));
                    }
                }

                var text = $"{Descriptor.DisplayName}/{request.ModelId} 已回答：{latest}";
                return new ChatModelResponse(
                    text,
                    ChatFinishReason.Stop,
                    new ChatModelUsage(10, 5, 15),
                    new ChatProviderMetadata(
                        Descriptor.ProviderId,
                        request.ModelId,
                        $"fake-{Guid.NewGuid():N}",
                        Descriptor.DataDestination));
            }
            finally
            {
                _active.TryRemove(request.TurnId, out _);
            }
        }

        public Task CancelAsync(Guid turnId, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (_active.TryGetValue(turnId, out var active))
            {
                active.Cancel();
            }

            return Task.CompletedTask;
        }

        public ValueTask DisposeAsync()
        {
            AllowSemanticCompletion.TrySetResult();
            AllowLateReply.TrySetResult();
            foreach (var active in _active.Values)
            {
                active.Cancel();
            }

            _active.Clear();
            return ValueTask.CompletedTask;
        }
    }

    private sealed record ObservedRequest(
        Guid TurnId,
        string ProviderId,
        string ModelId,
        string? PromptId,
        IReadOnlyList<ChatMessage> Messages);

    private delegate bool EnumWindowsCallback(IntPtr window, IntPtr parameter);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool EnumWindows(EnumWindowsCallback callback, IntPtr parameter);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr window, out int processId);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr SendMessage(
        IntPtr window,
        uint message,
        IntPtr wParam,
        IntPtr lParam);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern uint GetFinalPathNameByHandle(
        IntPtr fileHandle,
        StringBuilder path,
        int bufferLength,
        uint flags);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetWindowText(IntPtr window, StringBuilder text, int maximumCount);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetWindowTextLength(IntPtr window);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsWindowVisible(IntPtr window);

    [DllImport("user32.dll", EntryPoint = "keybd_event")]
    private static extern void KeyboardEvent(
        byte virtualKey,
        byte scanCode,
        uint flags,
        UIntPtr extraInfo);
}
