using ScreenGuide.Voice.Windows;

namespace ScreenGuide.Voice.Windows.Tests;

public sealed class WindowsSpeechOutputTests
{
    [Fact]
    public async Task SpeakAsync_AlreadyCancelled_DoesNotEnqueueSpeech()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            new WindowsSpeechOutput().SpeakAsync("元枢语音测试。", cancellation.Token));
    }
}
