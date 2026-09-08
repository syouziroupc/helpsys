using System.Globalization;
using System.Speech.Synthesis;

namespace HelpSys.Services;

public sealed class SpeechOutputService : IDisposable
{
    private static readonly TimeSpan SpeechCommitDelay = TimeSpan.FromMilliseconds(750);
    private static readonly TimeSpan DuplicateSuppressionWindow = TimeSpan.FromSeconds(4);

    private readonly SpeechSynthesizer _synthesizer = new();
    private readonly object _gate = new();
    private CancellationTokenSource? _pendingSpeechCts;
    private string? _pendingSpeechText;
    private string? _lastSpokenText;
    private DateTime _lastSpokenUtc = DateTime.MinValue;
    private bool _disposed;

    public SpeechOutputService()
    {
        _synthesizer.Volume = 100;
        _synthesizer.Rate = -1;
        TrySelectJapaneseVoice();
    }

    public void Speak(string? text, bool allowRepeat = false)
    {
        var value = Prepare(text);
        if (string.IsNullOrWhiteSpace(value)) return;

        CancellationTokenSource? previous;
        CancellationTokenSource current;
        lock (_gate)
        {
            if (_disposed) return;

            // Automatic live guidance can request the same sentence repeatedly while UIA is
            // noisy. Ignore that chatter without interrupting speech that is already correct.
            if (!allowRepeat &&
                (string.Equals(_pendingSpeechText, value, StringComparison.Ordinal) ||
                 (string.Equals(_lastSpokenText, value, StringComparison.Ordinal) &&
                  DateTime.UtcNow - _lastSpokenUtc < DuplicateSuppressionWindow)))
            {
                return;
            }

            previous = _pendingSpeechCts;
            current = new CancellationTokenSource();
            _pendingSpeechCts = current;
            _pendingSpeechText = value;

            // Once a different/new instruction has been accepted, the previous committed
            // instruction is stale. Stop it immediately; only the new instruction is delayed.
            try { _synthesizer.SpeakAsyncCancelAll(); } catch { }
        }

        try { previous?.Cancel(); } catch { }
        _ = CommitSpeechAsync(value, current, allowRepeat);
    }

    private async Task CommitSpeechAsync(string value, CancellationTokenSource source, bool allowRepeat)
    {
        try
        {
            await Task.Delay(SpeechCommitDelay, source.Token).ConfigureAwait(false);

            lock (_gate)
            {
                if (_disposed || source.IsCancellationRequested || !ReferenceEquals(_pendingSpeechCts, source)) return;
                _pendingSpeechCts = null;
                _pendingSpeechText = null;

                if (!allowRepeat && string.Equals(_lastSpokenText, value, StringComparison.Ordinal) &&
                    DateTime.UtcNow - _lastSpokenUtc < DuplicateSuppressionWindow)
                {
                    return;
                }

                try
                {
                    _synthesizer.SpeakAsyncCancelAll();
                    _synthesizer.SpeakAsync(value);
                    _lastSpokenText = value;
                    _lastSpokenUtc = DateTime.UtcNow;
                }
                catch { }
            }
        }
        catch (OperationCanceledException) { }
        finally
        {
            lock (_gate)
            {
                if (ReferenceEquals(_pendingSpeechCts, source))
                {
                    _pendingSpeechCts = null;
                    _pendingSpeechText = null;
                }
            }
            source.Dispose();
        }
    }

    public void Stop()
    {
        CancellationTokenSource? pending;
        lock (_gate)
        {
            pending = _pendingSpeechCts;
            _pendingSpeechCts = null;
            _pendingSpeechText = null;
            _lastSpokenText = null;
            _lastSpokenUtc = DateTime.MinValue;
            try { _synthesizer.SpeakAsyncCancelAll(); } catch { }
        }
        try { pending?.Cancel(); } catch { }
    }

    private void TrySelectJapaneseVoice()
    {
        try
        {
            var japanese = _synthesizer.GetInstalledVoices()
                .Where(x => x.Enabled)
                .Select(x => x.VoiceInfo)
                .FirstOrDefault(x => x.Culture.Name.Equals("ja-JP", StringComparison.OrdinalIgnoreCase) || x.Culture.TwoLetterISOLanguageName == "ja");
            if (japanese is not null) _synthesizer.SelectVoice(japanese.Name);
        }
        catch { }
    }

    private static string Prepare(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return string.Empty;
        return text.Trim()
            .Replace("Ctrl", "コントロール", StringComparison.OrdinalIgnoreCase)
            .Replace("Alt", "オルト", StringComparison.OrdinalIgnoreCase)
            .Replace("Enter", "エンター", StringComparison.OrdinalIgnoreCase)
            .Replace("Windows", "ウィンドウズ", StringComparison.OrdinalIgnoreCase)
            .Replace("+", "、を押したまま、", StringComparison.Ordinal)
            .Replace("URL", "ホームページのアドレス", StringComparison.OrdinalIgnoreCase);
    }

    public void Dispose()
    {
        CancellationTokenSource? pending;
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            pending = _pendingSpeechCts;
            _pendingSpeechCts = null;
            _pendingSpeechText = null;
            _lastSpokenText = null;
            try { _synthesizer.SpeakAsyncCancelAll(); } catch { }
        }
        try { pending?.Cancel(); } catch { }
        _synthesizer.Dispose();
    }
}
