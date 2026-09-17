using System.Globalization;
using System.Speech.Recognition;
using System.Speech.Synthesis;

namespace HelpSys.Stable;

internal sealed class SpeechService : IDisposable
{
    private readonly SemaphoreSlim _listenGate = new(1, 1);
    private readonly RecognizerInfo? _recognizer;
    private SpeechSynthesizer? _synthesizer;
    private bool _disposed;

    public SpeechService()
    {
        try
        {
            _recognizer = SpeechRecognitionEngine.InstalledRecognizers()
                .OrderByDescending(x => x.Culture.Name.Equals("ja-JP", StringComparison.OrdinalIgnoreCase))
                .FirstOrDefault();
        }
        catch
        {
            _recognizer = null;
        }
    }

    public bool RecognitionAvailable => _recognizer is not null;

    public void Speak(string? text)
    {
        if (_disposed || string.IsNullOrWhiteSpace(text)) return;
        try
        {
            _synthesizer ??= CreateSynthesizer();
            _synthesizer.SpeakAsyncCancelAll();
            _synthesizer.SpeakAsync(text.Trim());
        }
        catch
        {
            // Speech is optional. It must never take down guidance.
        }
    }

    public void StopSpeaking()
    {
        try { _synthesizer?.SpeakAsyncCancelAll(); }
        catch { }
    }

    public async Task<string?> ListenOnceAsync(CancellationToken cancellationToken)
    {
        if (_disposed) throw new ObjectDisposedException(nameof(SpeechService));
        if (_recognizer is null)
            throw new PlannerException("このPCではWindows音声認識を利用できません。文字入力またはF8案内を使ってください。");
        if (!await _listenGate.WaitAsync(0, cancellationToken))
            throw new PlannerException("音声認識はすでに実行中です。");

        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            var result = await Task.Run(() =>
            {
                using var engine = new SpeechRecognitionEngine(_recognizer);
                engine.LoadGrammar(new DictationGrammar());
                engine.SetInputToDefaultAudioDevice();
                return engine.Recognize(TimeSpan.FromSeconds(8))?.Text?.Trim();
            }, CancellationToken.None);

            cancellationToken.ThrowIfCancellationRequested();
            return string.IsNullOrWhiteSpace(result) ? null : result;
        }
        catch (InvalidOperationException ex)
        {
            throw new PlannerException("マイクを開始できませんでした。他の音声アプリを閉じてもう一度試してください。", ex);
        }
        finally
        {
            _listenGate.Release();
        }
    }

    private static SpeechSynthesizer CreateSynthesizer()
    {
        var synth = new SpeechSynthesizer();
        synth.SetOutputToDefaultAudioDevice();
        try
        {
            var ja = synth.GetInstalledVoices()
                .FirstOrDefault(v => v.Enabled && v.VoiceInfo.Culture.Name.Equals("ja-JP", StringComparison.OrdinalIgnoreCase));
            if (ja is not null) synth.SelectVoice(ja.VoiceInfo.Name);
        }
        catch { }
        return synth;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        try { _synthesizer?.SpeakAsyncCancelAll(); } catch { }
        try { _synthesizer?.Dispose(); } catch { }
    }
}
