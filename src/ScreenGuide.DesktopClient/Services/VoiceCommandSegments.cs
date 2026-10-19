using System.Text.RegularExpressions;

namespace ScreenGuide.DesktopClient.Services;

/// <summary>
/// Keeps only a short, explicitly connected recognition phrase. This is microphone input
/// assembly, never a remembered target or an authorization to execute an action.
/// </summary>
public sealed partial class VoiceCommandSegments(TimeProvider? timeProvider = null)
{
    private readonly TimeProvider _time = timeProvider ?? TimeProvider.System;
    private string? _prefix;
    private long? _microphoneGeneration;
    private long _recognitionGeneration;
    private DateTimeOffset _startedAt;
    private DateTimeOffset _lastFinalAt;
    private DateTimeOffset? _nextSegmentStartedAt;
    private int _segmentCount;
    private static readonly TimeSpan MaximumPause = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan MaximumPhrase = TimeSpan.FromSeconds(12);

    public VoiceSegmentPreview ObservePartial(string text, long microphoneGeneration, long recognitionGeneration)
    {
        Synchronize(microphoneGeneration, recognitionGeneration);
        text = text.Trim();
        if (text.Length == 0 || !HasTimelyPrefix()) return new(text, false);
        _nextSegmentStartedAt ??= _time.GetUtcNow();
        return CanContinue(text) || text is "然" or "然后" or "并" or "并且" or "接" or "接着" or "之" or "之后" or "再"
            ? new(Join(text), true) : new(text, false);
    }

    public string AcceptFinal(string text, long microphoneGeneration, long recognitionGeneration)
    {
        Synchronize(microphoneGeneration, recognitionGeneration);
        text = text.Trim();
        var continued = HasTimelyPrefix() && CanContinue(text);
        var joined = continued ? Join(text) : text;
        var now = _time.GetUtcNow();
        var count = continued ? _segmentCount + 1 : 1;
        var started = continued ? _startedAt : now;
        ResetPhrase();
        if (count < 3 && joined.Length <= 280 && CanStartPhrase(joined))
        {
            _prefix = joined;
            _startedAt = started;
            _lastFinalAt = now;
            _segmentCount = count;
        }
        return joined;
    }

    public void Reset()
    {
        ResetPhrase();
        _microphoneGeneration = null;
    }

    private void Synchronize(long microphoneGeneration, long recognitionGeneration)
    {
        if (_microphoneGeneration == microphoneGeneration && _recognitionGeneration == recognitionGeneration) return;
        ResetPhrase();
        _microphoneGeneration = microphoneGeneration;
        _recognitionGeneration = recognitionGeneration;
    }

    private bool HasTimelyPrefix()
    {
        var now = _time.GetUtcNow();
        if (_prefix is null || now < _lastFinalAt || now - _startedAt > MaximumPhrase
            || (_nextSegmentStartedAt ?? now) - _lastFinalAt > MaximumPause)
        {
            ResetPhrase();
            return false;
        }
        return true;
    }

    private bool CanContinue(string text)
    {
        if (_prefix is null || text.Length == 0 || NewInstruction().IsMatch(text)) return false;
        // A bare noun phrase is only valid immediately after an unfinished search verb.
        // Once a complete query has arrived, the phrase is cleared rather than remembered.
        return SearchSuffix().IsMatch(_prefix) || SearchContinuation().IsMatch(text);
    }

    private string Join(string text) => _prefix!.TrimEnd(' ', '，', ',', '。', '.', '；', ';') + text;

    private static bool CanStartPhrase(string text)
    {
        // Preserve the recognizer's words verbatim even if its opening verb is unclear.
        // The Host must still reject or clarify that complete phrase; no verb is invented here.
        if (UnfinishedConnectedSearch().IsMatch(text)) return true;
        if (!ApplicationOpening().IsMatch(text)) return false;
        return SearchSuffix().IsMatch(text) || !SearchWord().IsMatch(text);
    }

    private void ResetPhrase()
    {
        _prefix = null;
        _nextSegmentStartedAt = null;
        _segmentCount = 0;
    }

    [GeneratedRegex(@"^(?:(?:就是|呃|嗯|那个)[，,\s]*){0,2}(?:(?:请你|请|帮我|给我|麻烦你|麻烦|替我|帮忙|能不能|可以)[，,\s]*){0,4}(?:(?:打开|启动|开启|运行).{1,200}|(?:(?:我的?|)(?:电脑|桌面)上(?:的)?|我的).{1,80}(?:然后|并且|接着|之后|再|并)搜索(?:一下)?)$", RegexOptions.CultureInvariant)]
    private static partial Regex ApplicationOpening();

    [GeneratedRegex(@"(?:搜索(?:一下)?|搜一下|搜)[，,。.!！?？\s]*$", RegexOptions.CultureInvariant)]
    private static partial Regex SearchSuffix();

    [GeneratedRegex(@"^(?:(?:然后|并且|接着|之后|再|并)[，,\s]*)?(?:搜索|搜一下|搜)", RegexOptions.CultureInvariant)]
    private static partial Regex SearchContinuation();

    [GeneratedRegex(@"搜索|搜一下|搜", RegexOptions.CultureInvariant)]
    private static partial Regex SearchWord();

    [GeneratedRegex(@"^(?:(?:然后|请你|请|帮我|给我|麻烦|就是)[，,\s]*){0,3}(?:打开|启动|开启|运行|发送|发给|付款|支付|删除|停止|停(?:一下)?(?:$|[，,。.!！?？\s])|等一下|等等|你说错了|暂停|取消|算了|不要|别|不对|不是|你好|请问|我想问|换个|换一|改成|谢谢|先休息|退下)", RegexOptions.CultureInvariant)]
    private static partial Regex NewInstruction();

    [GeneratedRegex(@"^.{1,200}(?:然后|并且|接着|之后)[，,\s]*(?:搜索(?:一下)?|搜一下|搜)[，,。.!！?？\s]*$", RegexOptions.CultureInvariant)]
    private static partial Regex UnfinishedConnectedSearch();
}

public sealed record VoiceSegmentPreview(string Text, bool ContinuesPhrase);
