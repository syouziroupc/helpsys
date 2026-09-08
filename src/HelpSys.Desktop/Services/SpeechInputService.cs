using System.Globalization;
using System.Speech.Recognition;

namespace HelpSys.Services;

public sealed class SpeechInputService : IDisposable
{
    private SpeechRecognitionEngine? _engine;

    public async Task<string?> RecognizeOnceAsync(CancellationToken cancellationToken)
    {
        DisposeEngine();
        var recognizer = SelectRecognizer();
        if (recognizer is null)
            throw new InvalidOperationException("Windowsの音声認識エンジンが見つかりません。Windowsの音声機能を確認してください。");

        _engine = new SpeechRecognitionEngine(recognizer);
        _engine.LoadGrammar(new DictationGrammar());
        _engine.SetInputToDefaultAudioDevice();

        var completion = new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously);
        EventHandler<RecognizeCompletedEventArgs>? completedHandler = null;
        completedHandler = (_, e) =>
        {
            if (e.Error is not null) completion.TrySetException(e.Error);
            else if (e.Cancelled) completion.TrySetResult(null);
            else completion.TrySetResult(string.IsNullOrWhiteSpace(e.Result?.Text) ? null : e.Result.Text.Trim());
        };
        _engine.RecognizeCompleted += completedHandler;

        using var registration = cancellationToken.Register(() =>
        {
            try { _engine?.RecognizeAsyncCancel(); } catch { }
        });

        try
        {
            _engine.RecognizeAsync(RecognizeMode.Single);
            try
            {
                return await completion.Task.WaitAsync(TimeSpan.FromSeconds(12), cancellationToken);
            }
            catch (TimeoutException)
            {
                try { _engine.RecognizeAsyncCancel(); } catch { }
                return null;
            }
        }
        finally
        {
            if (_engine is not null && completedHandler is not null) _engine.RecognizeCompleted -= completedHandler;
            DisposeEngine();
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

    private void DisposeEngine()
    {
        try { _engine?.RecognizeAsyncCancel(); } catch { }
        _engine?.Dispose();
        _engine = null;
    }

    public void Dispose() => DisposeEngine();
}
