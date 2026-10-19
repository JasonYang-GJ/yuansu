using ScreenGuide.DesktopClient.Services;

namespace ScreenGuide.DesktopClient.Tests;

public sealed class VoiceReceptionGateTests
{
    [Fact]
    public void ANewMicrophoneSessionStartsInDirectConversation()
    {
        var gate = new VoiceReceptionGate();
        Assert.Equal(VoiceReceptionDecision.Ignore, gate.Classify("打开微信", null, 1));
        gate.SynchronizeMicrophone(1, 1);
        Assert.Equal(VoiceReceptionDecision.Submit, gate.Classify("打开微信", 1, 1));
        Assert.Equal(VoiceReceptionDecision.Rest, gate.Classify("先休息。", 1, 1));
    }

    [Theory]
    [InlineData("元枢", VoiceReceptionDecision.Wake)]
    [InlineData("你好，元枢。", VoiceReceptionDecision.Wake)]
    [InlineData("元书", VoiceReceptionDecision.Wake)]
    [InlineData("原书", VoiceReceptionDecision.Wake)]
    [InlineData("袁叔", VoiceReceptionDecision.Wake)]
    [InlineData("袁书", VoiceReceptionDecision.Wake)]
    [InlineData("你好元书", VoiceReceptionDecision.Wake)]
    [InlineData("你好原书", VoiceReceptionDecision.Wake)]
    [InlineData("你好，袁叔。", VoiceReceptionDecision.Wake)]
    [InlineData("你好袁书", VoiceReceptionDecision.Wake)]
    [InlineData("元素", VoiceReceptionDecision.Ignore)]
    [InlineData("你好元素", VoiceReceptionDecision.Ignore)]
    [InlineData("元枢好", VoiceReceptionDecision.Ignore)]
    [InlineData("元书打开微信", VoiceReceptionDecision.Ignore)]
    [InlineData("打开微信元书", VoiceReceptionDecision.Ignore)]
    [InlineData("我说你好袁叔", VoiceReceptionDecision.Ignore)]
    [InlineData("", VoiceReceptionDecision.Ignore)]
    [InlineData("元枢打开微信", VoiceReceptionDecision.Ignore)]
    [InlineData("这首歌提到了元枢", VoiceReceptionDecision.Ignore)]
    [InlineData("打开微信", VoiceReceptionDecision.Ignore)]
    public void StandbyConsumesOnlyAnExactWakePhrase(string phrase, VoiceReceptionDecision expected)
    {
        var gate = new VoiceReceptionGate();
        gate.SynchronizeMicrophone(7, 2);
        var transition = gate.BeginTransition();
        Assert.True(gate.CompleteTransition(transition, 7, 3, VoiceReceptionMode.Standby));
        Assert.False(gate.AcceptsPartial(7, 3));
        Assert.Equal(expected, gate.Classify(phrase, 7, 3));
    }

    [Fact]
    public void OldRecognitionAndOldTransitionCannotWakeOrRestoreReception()
    {
        var gate = new VoiceReceptionGate();
        gate.SynchronizeMicrophone(7, 2);
        var old = gate.BeginTransition();
        Assert.Equal(VoiceReceptionDecision.Ignore, gate.Classify("元枢", 7, 2));
        var current = gate.BeginTransition();
        Assert.False(gate.CompleteTransition(old, 7, 3, VoiceReceptionMode.Direct));
        Assert.True(gate.CompleteTransition(current, 7, 4, VoiceReceptionMode.Standby));
        Assert.Equal(VoiceReceptionDecision.Ignore, gate.Classify("元枢", 7, 2));
        gate.SynchronizeMicrophone(null, 5);
        Assert.False(gate.CompleteTransition(current, 7, 4, VoiceReceptionMode.Standby));
        gate.SynchronizeMicrophone(8, 6);
        Assert.Equal(VoiceReceptionMode.Direct, gate.Mode);
        Assert.Equal(VoiceReceptionDecision.Ignore, gate.Classify("打开微信", 7, 4));
    }

    [Fact]
    public void HomophoneWakeRemainsOrdinaryTextOutsideStandbyAndRejectsStaleAudio()
    {
        var gate = new VoiceReceptionGate();
        gate.SynchronizeMicrophone(7, 2);
        Assert.Equal(VoiceReceptionDecision.Submit, gate.Classify("元书", 7, 2));
        var transition = gate.BeginTransition();
        Assert.Equal(VoiceReceptionDecision.Ignore, gate.Classify("元书", 7, 2));
        Assert.True(gate.CompleteTransition(transition, 7, 3, VoiceReceptionMode.Standby));
        Assert.Equal(VoiceReceptionDecision.Wake, gate.Classify("元书", 7, 3));
        Assert.Equal(VoiceReceptionDecision.Ignore, gate.Classify("元书", 7, 2));
        Assert.Equal(VoiceReceptionDecision.Ignore, gate.Classify("你好袁叔", 6, 3));
        gate.SynchronizeMicrophone(null, 4);
        Assert.Equal(VoiceReceptionDecision.Ignore, gate.Classify("元书", 7, 3));
    }
}
