using System.Globalization;
using System.Speech.Recognition;

namespace HelpSys.Services;

public sealed class SpeechInputService : IDisposable
{
    private static readonly TimeSpan RecognitionTimeout = TimeSpan.FromSeconds(8);
    private static readonly TimeSpan ReleaseTimeout = TimeSpan.FromSeconds(2);
    private readonly object _gate = new();
    private SpeechRecognitionEngine? _engine;

    public async Task<string?> RecognizeOnceAsync(CancellationToken cancellationToken)
    {
        using var microphoneLease = MicrophoneCoordinator.BeginForegroundCapture();

        if (!await MicrophoneCoordinator.WaitForBackgroundReleaseAsync(cancellationToken).ConfigureAwait(false))
            throw new InvalidOperationException("コマンダーのマイク待機を安全に切り替えられませんでした。数秒後にもう一度試してください。");

        var recognizer = SelectRecognizer();
        if (recognizer is null)
            throw new InvalidOperationException("Windowsの音声認識エンジンが見つかりません。Windowsの音声機能を確認してください。");

        var engine = new SpeechRecognitionEngine(recognizer);
        var completion = new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously);
        EventHandler<RecognizeCompletedEventArgs>? completedHandler = null;

        try
        {
            engine.LoadGrammar(new DictationGrammar());
            engine.SetInputToDefaultAudioDevice();

            completedHandler = (_, e) =>
            {
                if (e.Error is not null) completion.TrySetException(e.Error);
                else if (e.Cancelled) completion.TrySetResult(null);
                else completion.TrySetResult(string.IsNullOrWhiteSpace(e.Result?.Text) ? null : e.Result.Text.Trim());
            };
            engine.RecognizeCompleted += completedHandler;

            lock (_gate)
            {
                if (_engine is not null)
                    throw new InvalidOperationException("別の音声入力がまだ終了していません。");
                _engine = engine;
            }

            using var registration = cancellationToken.Register(() =>
            {
                try { engine.RecognizeAsyncCancel(); } catch { }
            });

            engine.RecognizeAsync(RecognizeMode.Single);
            try
            {
                return await completion.Task.WaitAsync(RecognitionTimeout, cancellationToken).ConfigureAwait(false);
            }
            catch (TimeoutException)
            {
                try { engine.RecognizeAsyncCancel(); } catch { }
                return null;
            }
        }
        finally
        {
            if (completedHandler is not null)
            {
                try { engine.RecognizeCompleted -= completedHandler; } catch { }
            }

            lock (_gate)
            {
                if (ReferenceEquals(_engine, engine)) _engine = null;
            }

            await ReleaseEngineAsync(engine).ConfigureAwait(false);
        }
    }

    private static RecognizerInfo? SelectRecognizer()
    {
        var recognizers = SpeechRecognitionEngine.InstalledRecognizers();
        if (recognizers.Count == 0) return null;

        var current = CultureInfo.CurrentUICulture.Name;
        return recognizers.FirstOrDefault(x => x.Culture.Name.Equals(current, StringComparison.OrdinalIgnoreCase))
            ?? recognizers.FirstOrDefault(x => x.Culture.Name.Equals("ja-JP", StringComparison.OrdinalIgnoreCase))
            ?? recognizers[0];
    }

    private static async Task ReleaseEngineAsync(SpeechRecognitionEngine engine)
    {
        var release = Task.Run(() =>
        {
            try { engine.RecognizeAsyncCancel(); } catch { }
            try { engine.Dispose(); } catch { }
        });
        MicrophoneCoordinator.TrackBackgroundRelease(release);

        try { await release.WaitAsync(ReleaseTimeout).ConfigureAwait(false); }
        catch (TimeoutException) { }
    }

    public void Dispose()
    {
        SpeechRecognitionEngine? engine;
        lock (_gate)
        {
            engine = _engine;
            _engine = null;
        }
        if (engine is null) return;
        var release = Task.Run(() =>
        {
            try { engine.RecognizeAsyncCancel(); } catch { }
            try { engine.Dispose(); } catch { }
        });
        MicrophoneCoordinator.TrackBackgroundRelease(release);
    }
}
