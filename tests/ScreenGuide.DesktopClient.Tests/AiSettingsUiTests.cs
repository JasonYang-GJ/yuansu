using ScreenGuide.DesktopClient.Services;
using ScreenGuide.DesktopProtocol;

namespace ScreenGuide.DesktopClient.Tests;

public sealed class AiSettingsUiTests
{
    [Fact]
    public void SettingsPageExposesExplicitLocalOnlyMemoryCrudWithoutAutomaticModelUse()
    {
        var xaml = File.ReadAllText(Path.Combine(
            FindRepositoryRoot(),
            "src",
            "ScreenGuide.DesktopClient",
            "MainWindow.xaml"));

        Assert.Contains("我的记忆", xaml, StringComparison.Ordinal);
        Assert.Contains("仅保存在本机；当前不会自动发送给模型。", xaml, StringComparison.Ordinal);
        Assert.Contains("AutomationProperties.AutomationId=\"MemoryList\"", xaml, StringComparison.Ordinal);
        Assert.Contains("AutomationProperties.AutomationId=\"MemoryTitle\"", xaml, StringComparison.Ordinal);
        Assert.Contains("AutomationProperties.AutomationId=\"MemoryBody\"", xaml, StringComparison.Ordinal);
        Assert.Contains("Click=\"SaveMemoryButton_Click\"", xaml, StringComparison.Ordinal);
        Assert.Contains("Click=\"ToggleMemoryButton_Click\"", xaml, StringComparison.Ordinal);
        Assert.Contains("Click=\"DeleteMemoryButton_Click\"", xaml, StringComparison.Ordinal);
        Assert.Contains("不会自动发送给模型", xaml, StringComparison.Ordinal);
        Assert.DoesNotContain("自动提取记忆", xaml, StringComparison.Ordinal);
    }

    [Fact]
    public void ConversationRequiresAVisibleFullPerTurnMemoryOutboundConfirmation()
    {
        var repositoryRoot = FindRepositoryRoot();
        var xaml = File.ReadAllText(Path.Combine(
            repositoryRoot,
            "src",
            "ScreenGuide.DesktopClient",
            "MainWindow.xaml"));
        var code = File.ReadAllText(Path.Combine(
            repositoryRoot,
            "src",
            "ScreenGuide.DesktopClient",
            "MainWindow.xaml.cs"));
        var normalizedCode = code.ReplaceLineEndings("\n");

        Assert.Contains("默认 0 条，不会自动发送", xaml, StringComparison.Ordinal);
        Assert.Contains("AutomationProperties.AutomationId=\"ConversationMemorySelection\"", xaml, StringComparison.Ordinal);
        Assert.Contains("AutomationProperties.AutomationId=\"MemoryOutboundConsent\"", xaml, StringComparison.Ordinal);
        Assert.Contains("HTTPS 去向", code, StringComparison.Ordinal);
        Assert.Contains("项目绑定", code, StringComparison.Ordinal);
        Assert.Contains("{item.Title}", code, StringComparison.Ordinal);
        Assert.Contains("item.Body", code, StringComparison.Ordinal);
        Assert.Contains("version {item.Version}", code, StringComparison.Ordinal);
        Assert.Contains("{item.CharacterCount} 字符", code, StringComparison.Ordinal);
        Assert.Contains("确认发送这一次", xaml, StringComparison.Ordinal);
        Assert.Contains("ConfirmMemoryOutboundAsync", code, StringComparison.Ordinal);
        Assert.Contains("private async void MemoryPreviewQueryTextBox_TextChanged", code, StringComparison.Ordinal);
        Assert.Contains("private async void MemoryPreviewProjectComboBox_SelectionChanged", code, StringComparison.Ordinal);
        Assert.Contains("if (selected is not null)\n            {\n                await InvalidateDisplayedMemoryConsentAsync();", normalizedCode, StringComparison.Ordinal);
        Assert.DoesNotContain("自动携带", xaml, StringComparison.Ordinal);
    }

    [Fact]
    public void ConversationExposesOnlyAnExplicitBoundedEarlierHistoryAction()
    {
        var repositoryRoot = FindRepositoryRoot();
        var xaml = File.ReadAllText(Path.Combine(
            repositoryRoot,
            "src",
            "ScreenGuide.DesktopClient",
            "MainWindow.xaml"));
        var code = File.ReadAllText(Path.Combine(
            repositoryRoot,
            "src",
            "ScreenGuide.DesktopClient",
            "MainWindow.xaml.cs"));

        Assert.Contains("AutomationProperties.AutomationId=\"LoadEarlierMessages\"", xaml, StringComparison.Ordinal);
        Assert.Contains("Click=\"LoadEarlierMessagesButton_Click\"", xaml, StringComparison.Ordinal);
        Assert.Contains("GetSessionMessagesPageAsync", code, StringComparison.Ordinal);
        Assert.Contains("_sessionProjectionCache.LatestMessageSequenceNumber", code, StringComparison.Ordinal);
        Assert.DoesNotContain("current.Messages.LastOrDefault()?.SequenceNumber", code, StringComparison.Ordinal);
        Assert.DoesNotContain("LoadAllMessages", code, StringComparison.Ordinal);
    }

    [Fact]
    public void PointerQuestionRequiresVisibleLocalCaptureAndExactTextOnlyOutboundConfirmation()
    {
        var repositoryRoot = FindRepositoryRoot();
        var xaml = File.ReadAllText(Path.Combine(
            repositoryRoot,
            "src",
            "ScreenGuide.DesktopClient",
            "MainWindow.xaml"));
        var code = File.ReadAllText(Path.Combine(
            repositoryRoot,
            "src",
            "ScreenGuide.DesktopClient",
            "MainWindow.xaml.cs"));

        Assert.Contains("AutomationProperties.AutomationId=\"AskPointerQuestion\"", xaml, StringComparison.Ordinal);
        Assert.Contains("AutomationProperties.AutomationId=\"PointerAnswerOutboundConsent\"", xaml, StringComparison.Ordinal);
        Assert.Contains("将发送的完整问题", xaml, StringComparison.Ordinal);
        Assert.Contains("将发送的完整本机 OCR 文字", xaml, StringComparison.Ordinal);
        Assert.Contains("不会发送图片、整页文字、窗口标题或文件路径", xaml, StringComparison.Ordinal);
        Assert.Contains("PreparePointerRegionAsync", code, StringComparison.Ordinal);
        Assert.Contains("PointerAnchorId: anchor.AnchorId", code, StringComparison.Ordinal);
        Assert.Contains("ConfirmPointerAnswerAsync", code, StringComparison.Ordinal);
        Assert.Contains("consent.PreviewHash", code, StringComparison.Ordinal);
        Assert.DoesNotContain("PointerAnswerImage", xaml, StringComparison.Ordinal);
    }

    [Fact]
    public void PointerAnswerProtocolDiagnosticsRedactQuestionAndOcrText()
    {
        const string question = "pointer-question-sentinel";
        const string ocr = "pointer-ocr-sentinel";
        var consent = new PointerAnswerConsentDto(
            Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(),
            "qwen", "qwen3.7-plus", "https://qwen.example",
            "window.pointer.answer", "1", new string('A', 64),
            question, ocr, question.Length, ocr.Length, 1, 320, 120,
            "uia-element", new string('B', 64), DateTimeOffset.UtcNow,
            DateTimeOffset.UtcNow.AddSeconds(10));

        Assert.DoesNotContain(question, consent.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain(ocr, consent.ToString(), StringComparison.Ordinal);
        Assert.Contains("[REDACTED]", consent.ToString(), StringComparison.Ordinal);
        Assert.False(consent.SendsImage);
    }

    [Fact]
    public void MemoryOutboundProtocolObjectsRedactUserInputAndMemoryContent()
    {
        const string input = "outbound-input-sentinel";
        const string title = "outbound-title-sentinel";
        const string body = "outbound-body-sentinel";
        var item = new MemoryOutboundPreparedItemDto(
            Guid.NewGuid(), 2, "UserFact", "Global", title, body, title.Length + body.Length);
        var consent = new MemoryOutboundConsentDto(
            Guid.NewGuid(), Guid.NewGuid(), "WaitingForMemoryOutboundConsent",
            "qwen", "qwen3.7-plus", "https://dashscope.aliyuncs.com", null, null,
            [item], 1, title.Length + body.Length, DateTimeOffset.UtcNow,
            DateTimeOffset.UtcNow.AddMinutes(10), "SAFE-HASH");
        var request = new SessionInputRequestDto(
            input, MemoryItems: [new MemoryOutboundItemReferenceDto(item.MemoryId, item.Version)]);

        Assert.DoesNotContain(input, request.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain(title, item.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain(body, item.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain(title, consent.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain(body, consent.ToString(), StringComparison.Ordinal);
        Assert.Contains("[REDACTED]", request.ToString(), StringComparison.Ordinal);
        Assert.Contains("[REDACTED]", consent.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void MemoryProtocolObjectsRedactPlaintextFromDiagnosticStringRepresentations()
    {
        const string fakeTitle = "fake-title-sentinel";
        const string fakeBody = "fake-body-sentinel";
        var request = new CreateMemoryRequestDto(
            "UserFact",
            "Global",
            null,
            fakeTitle,
            fakeBody,
            null);
        var result = new MemoryDto(
            Guid.NewGuid(),
            "UserFact",
            "Global",
            null,
            "Active",
            "用户明确保存",
            fakeTitle,
            fakeBody,
            DateTimeOffset.UtcNow,
            DateTimeOffset.UtcNow,
            null,
            1.0,
            1);

        Assert.DoesNotContain(fakeTitle, request.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain(fakeBody, request.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain(fakeTitle, result.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain(fakeBody, result.ToString(), StringComparison.Ordinal);
        Assert.Contains("[REDACTED]", request.ToString(), StringComparison.Ordinal);
        Assert.Contains("[REDACTED]", result.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void SettingsPageExposesExplicitLocalMemoryPreviewWithoutAModelUseControl()
    {
        var repositoryRoot = FindRepositoryRoot();
        var xaml = File.ReadAllText(Path.Combine(
            repositoryRoot,
            "src",
            "ScreenGuide.DesktopClient",
            "MainWindow.xaml"));
        var code = File.ReadAllText(Path.Combine(
            repositoryRoot,
            "src",
            "ScreenGuide.DesktopClient",
            "MainWindow.xaml.cs"));

        Assert.Contains("本地相关记忆预览", xaml, StringComparison.Ordinal);
        Assert.Contains("只在本机匹配；不会发送给模型。", xaml, StringComparison.Ordinal);
        Assert.Contains("AutomationProperties.AutomationId=\"MemoryPreviewQuery\"", xaml, StringComparison.Ordinal);
        Assert.Contains("AutomationProperties.AutomationId=\"MemoryPreviewProject\"", xaml, StringComparison.Ordinal);
        Assert.Contains("AutomationProperties.AutomationId=\"RunMemoryPreview\"", xaml, StringComparison.Ordinal);
        Assert.Contains("AutomationProperties.AutomationId=\"MemoryPreviewResults\"", xaml, StringComparison.Ordinal);
        Assert.Contains("Click=\"RunMemoryPreviewButton_Click\"", xaml, StringComparison.Ordinal);
        Assert.Contains("TextChanged=\"MemoryPreviewQueryTextBox_TextChanged\"", xaml, StringComparison.Ordinal);
        Assert.Contains("PreviewMemoriesAsync", code, StringComparison.Ordinal);
        Assert.DoesNotContain("MemoryPreviewUseInChat", xaml, StringComparison.Ordinal);
        Assert.DoesNotContain("MemoryPreviewAuto", xaml, StringComparison.Ordinal);
    }

    [Fact]
    public void SettingsPageExposesAiRoutingPrivacyAndCredentialControls()
    {
        var xaml = File.ReadAllText(Path.Combine(
            FindRepositoryRoot(),
            "src",
            "ScreenGuide.DesktopClient",
            "MainWindow.xaml"));

        Assert.Contains("普通聊天大脑", xaml, StringComparison.Ordinal);
        Assert.Contains("数据去向", xaml, StringComparison.Ordinal);
        Assert.Contains("同一会话的既有历史和下一条消息会发送给所选 Provider", xaml, StringComparison.Ordinal);
        Assert.Contains("编程任务仍由 Codex 负责", xaml, StringComparison.Ordinal);
        Assert.Contains("AI 服务（Provider）", xaml, StringComparison.Ordinal);
        Assert.Contains("聊天模型（Model）", xaml, StringComparison.Ordinal);
        Assert.Contains("使用 Codex 登录账号，无需在元枢保存 Key", xaml, StringComparison.Ordinal);
        Assert.Contains("AutomationProperties.AutomationId=\"AiResponsibilityBoundary\"", xaml, StringComparison.Ordinal);
        Assert.Contains("AutomationProperties.AutomationId=\"AiChatProvider\"", xaml, StringComparison.Ordinal);
        Assert.Contains("AutomationProperties.AutomationId=\"AiChatModel\"", xaml, StringComparison.Ordinal);
        Assert.Contains("AutomationProperties.AutomationId=\"SaveAiChatRoute\"", xaml, StringComparison.Ordinal);
        Assert.Contains("AutomationProperties.AutomationId=\"AiDataDestination\"", xaml, StringComparison.Ordinal);
        Assert.Contains("AutomationProperties.AutomationId=\"AiCredentialPassword\"", xaml, StringComparison.Ordinal);
        Assert.Contains("AutomationProperties.AutomationId=\"AiCredentialRequiredPanel\"", xaml, StringComparison.Ordinal);
        Assert.Contains("AutomationProperties.AutomationId=\"AiCredentialNotRequired\"", xaml, StringComparison.Ordinal);
        Assert.Contains("<PasswordBox", xaml, StringComparison.Ordinal);
        Assert.Contains("AutomationProperties.AutomationId=\"SaveAiCredential\"", xaml, StringComparison.Ordinal);
        Assert.Contains("AutomationProperties.AutomationId=\"DeleteAiCredential\"", xaml, StringComparison.Ordinal);
        Assert.Contains("AutomationProperties.AutomationId=\"CheckAiProviderHealth\"", xaml, StringComparison.Ordinal);
        Assert.Equal(2, xaml.Split("TextSearch.TextPath=\"DisplayName\"", StringSplitOptions.None).Length - 1);
        Assert.Equal(2, xaml.Split("AutomationProperties.Name=\"{Binding DisplayName}\"", StringSplitOptions.None).Length - 1);
        Assert.Equal(
            2,
            xaml.Split(
                "<Setter Property=\"AutomationProperties.Name\" Value=\"{Binding DisplayName}\" />",
                StringSplitOptions.None).Length - 1);
        Assert.DoesNotContain("显示密钥", xaml, StringComparison.Ordinal);
    }

    [Fact]
    public void AiSettingsControlsWireSelectionAndAllUserActions()
    {
        var xaml = File.ReadAllText(Path.Combine(
            FindRepositoryRoot(),
            "src",
            "ScreenGuide.DesktopClient",
            "MainWindow.xaml"));

        Assert.Contains("SelectionChanged=\"AiChatProviderComboBox_SelectionChanged\"", xaml, StringComparison.Ordinal);
        Assert.Contains("PasswordChanged=\"AiCredentialPasswordBox_PasswordChanged\"", xaml, StringComparison.Ordinal);
        Assert.Contains("Click=\"SaveAiChatRouteButton_Click\"", xaml, StringComparison.Ordinal);
        Assert.Contains("Click=\"SaveAiCredentialButton_Click\"", xaml, StringComparison.Ordinal);
        Assert.Contains("Click=\"DeleteAiCredentialButton_Click\"", xaml, StringComparison.Ordinal);
        Assert.Contains("Click=\"CheckAiProviderHealthButton_Click\"", xaml, StringComparison.Ordinal);
    }

    [Fact]
    public void CodexNotRequiredStateHidesCredentialActionsAndSurvivesHealthRefresh()
    {
        var provider = Provider("NotRequired");

        var presentation = AiSettingsUiPolicy.PresentCredential(
            provider,
            hasSecretInput: true,
            hostOnline: true);
        var refreshed = AiSettingsUiPolicy.ApplyHealth(
            provider,
            new AiProviderHealthDto(
                "codex",
                "Healthy",
                IsConfigured: true,
                "Codex 登录有效。"));

        Assert.False(presentation.ShowCredentialInputs);
        Assert.False(presentation.CanSave);
        Assert.False(presentation.CanDelete);
        Assert.Equal("使用 Codex 登录账号，无需在元枢保存 Key。", presentation.StatusText);
        Assert.Equal("NotRequired", refreshed.ConfigurationState);
    }

    [Theory]
    [InlineData("Missing", true, false)]
    [InlineData("Configured", true, true)]
    public void ApiKeyProviderShowsOnlyCredentialActionsAllowedByCurrentState(
        string configurationState,
        bool canSave,
        bool canDelete)
    {
        var presentation = AiSettingsUiPolicy.PresentCredential(
            Provider(configurationState),
            hasSecretInput: true,
            hostOnline: true);

        Assert.True(presentation.ShowCredentialInputs);
        Assert.Equal(canSave, presentation.CanSave);
        Assert.Equal(canDelete, presentation.CanDelete);
    }

    [Fact]
    public void SwitchingBackToCurrentProviderRestoresItsRoutedModelInsteadOfAStaleOtherProviderModel()
    {
        var provider = Provider("Configured") with
        {
            ProviderId = "deepseek",
            Models =
            [
                new AiModelSettingsDto("deepseek-chat", "DeepSeek Chat", ["Text"]),
                new AiModelSettingsDto("deepseek-reasoner", "DeepSeek Reasoner", ["Reasoning"])
            ]
        };

        var selectedModelId = AiSettingsUiPolicy.SelectModelId(
            provider,
            requestedModelId: "codex-default",
            new AiChatRouteDto("deepseek", "deepseek-reasoner"));

        Assert.Equal("deepseek-reasoner", selectedModelId);
    }

    [Fact]
    public async Task CredentialSubmissionClearsPasswordAfterFailedSaveAttempt()
    {
        var cleared = false;

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            AiCredentialSubmission.RunAsync(
                "fake-test-key",
                _ => throw new InvalidOperationException("safe failure"),
                () => cleared = true));

        Assert.True(cleared);
    }

    [Fact]
    public async Task CredentialSubmissionClearsPasswordAfterSuccessfulSaveAttempt()
    {
        var cleared = false;
        string? submitted = null;

        await AiCredentialSubmission.RunAsync(
            "fake-test-key",
            value =>
            {
                submitted = value;
                return Task.CompletedTask;
            },
            () => cleared = true);

        Assert.Equal("fake-test-key", submitted);
        Assert.True(cleared);
    }

    [Fact]
    public void CredentialDeletionRequiresProviderSpecificVisibleConfirmation()
    {
        var provider = Provider("Configured") with { DisplayName = "DeepSeek" };

        var confirmation = AiSettingsUiPolicy.CredentialDeleteConfirmation(provider);

        Assert.Contains("DeepSeek", confirmation, StringComparison.Ordinal);
        Assert.Contains("删除", confirmation, StringComparison.Ordinal);
        Assert.Contains("无法使用普通聊天", confirmation, StringComparison.Ordinal);
    }

    [Fact]
    public void DescriptorDrivenQwenSelectionShowsItsOnlyModelAndProviderScopedCredentialControls()
    {
        var qwen = new AiProviderSettingsDto(
            "qwen",
            "千问",
            "https://dashscope.aliyuncs.com",
            SendsDataOffDevice: true,
            "Missing",
            new AiProviderHealthDto(
                "qwen",
                "NotConfigured",
                IsConfigured: false,
                "尚未配置 API Key。"),
            [new AiModelSettingsDto(
                "qwen3.7-plus",
                "千问 3.7 Plus",
                ["Streaming", "JsonObjectOutput"])]);

        var selectedModel = AiSettingsUiPolicy.SelectModelId(
            qwen,
            requestedModelId: "deepseek-v4-pro",
            new AiChatRouteDto("qwen", "qwen3.7-plus"));
        var credentials = AiSettingsUiPolicy.PresentCredential(
            qwen,
            hasSecretInput: true,
            hostOnline: true);
        var confirmation = AiSettingsUiPolicy.CredentialDeleteConfirmation(qwen);

        Assert.Equal("qwen3.7-plus", selectedModel);
        Assert.True(credentials.ShowCredentialInputs);
        Assert.True(credentials.CanSave);
        Assert.False(credentials.CanDelete);
        Assert.Contains("千问", confirmation, StringComparison.Ordinal);
    }

    private static AiProviderSettingsDto Provider(string configurationState) => new(
        "codex",
        "Codex",
        "OpenAI Codex 服务",
        SendsDataOffDevice: true,
        configurationState,
        new AiProviderHealthDto(
            "codex",
            "Healthy",
            IsConfigured: true,
            "Codex 登录有效。"),
        [new AiModelSettingsDto("codex", "Codex", ["Coding"])]);

    private static string FindRepositoryRoot()
    {
        var current = new DirectoryInfo(AppContext.BaseDirectory);
        while (current is not null)
        {
            if (File.Exists(Path.Combine(current.FullName, "ScreenGuide.slnx")))
            {
                return current.FullName;
            }

            current = current.Parent;
        }

        throw new DirectoryNotFoundException("ScreenGuide repository root was not found.");
    }
}
