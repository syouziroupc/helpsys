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
            using var engine = new SpeechRecognitionEngine(_recognizer);
            engine.LoadGrammar(new DictationGrammar());
            engine.SetInputToDefaultAudioDevice();
            engine.InitialSilenceTimeout = TimeSpan.FromSeconds(8);
            engine.BabbleTimeout = TimeSpan.FromSeconds(8);
            engine.EndSilenceTimeout = TimeSpan.FromSeconds(1);
            engine.EndSilenceTimeoutAmbiguous = TimeSpan.FromSeconds(1.5);

            var completion = new TaskCompletionSource<string?>(
                TaskCreationOptions.RunContinuationsAsynchronously);
            EventHandler<RecognizeCompletedEventArgs>? handler = null;
            handler = (_, args) =>
            {
                if (args.Cancelled)
                    completion.TrySetCanceled();
                else if (args.Error is not null)
                    completion.TrySetException(args.Error);
                else
                    completion.TrySetResult(args.Result?.Text?.Trim());
            };
            engine.RecognizeCompleted += handler;

            using var hardLimit = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            hardLimit.CancelAfter(TimeSpan.FromSeconds(8));
            using var registration = hardLimit.Token.Register(() =>
            {
                try { engine.RecognizeAsyncCancel(); } catch { }
            });

            try
            {
                engine.RecognizeAsync(RecognizeMode.Single);
                var result = await completion.Task.WaitAsync(hardLimit.Token);
                return string.IsNullOrWhiteSpace(result) ? null : result;
            }
            catch (OperationCanceledException) when (hardLimit.IsCancellationRequested)
            {
                try { engine.RecognizeAsyncCancel(); } catch { }
                throw;
            }
            finally
            {
                engine.RecognizeCompleted -= handler;
            }
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
