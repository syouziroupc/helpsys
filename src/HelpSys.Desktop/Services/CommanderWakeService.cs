using System.Globalization;
using System.Speech.Recognition;

namespace HelpSys.Services;

public sealed class CommanderWakeService : IDisposable
{
    private static readonly TimeSpan RetryDelay = TimeSpan.FromSeconds(4);
    private const float MinimumWakeConfidence = 0.55f;

    private readonly object _gate = new();
    private SpeechRecognitionEngine? _engine;
    private Timer? _retryTimer;
    private bool _enabled = true;
    private bool _disposed;
    private int _suspendCount;
    private bool _listening;
    private string _status = "準備中";

    public event EventHandler? WakeDetected;
    public event EventHandler? StatusChanged;

    public CommanderWakeService()
    {
        MicrophoneCoordinator.ForegroundCaptureChanged += MicrophoneCoordinator_ForegroundCaptureChanged;
    }

    public bool Enabled
    {
        get { lock (_gate) return _enabled; }
    }

    public bool Listening
    {
        get { lock (_gate) return _listening; }
    }

    public string Status
    {
        get { lock (_gate) return _status; }
    }

    public void Start()
    {
        lock (_gate)
        {
            if (_disposed) return;
            _enabled = true;
        }
        TryStartListening();
    }

    public void SetEnabled(bool enabled)
    {
        bool start;
        lock (_gate)
        {
            if (_disposed) return;
            _enabled = enabled;
            start = enabled && _suspendCount == 0;
            if (!enabled)
            {
                StopEngineLocked();
                CancelRetryLocked();
                SetStatusLocked("OFF");
            }
        }

        RaiseStatusChanged();
        if (start) TryStartListening();
    }

    public void Suspend()
    {
        lock (_gate)
        {
            if (_disposed) return;
            _suspendCount++;
            StopEngineLocked();
            CancelRetryLocked();
            SetStatusLocked(_enabled ? "一時停止" : "OFF");
        }
        RaiseStatusChanged();
    }

    public void Resume()
    {
        bool shouldStart;
        lock (_gate)
        {
            if (_disposed) return;
            if (_suspendCount > 0) _suspendCount--;
            shouldStart = _enabled && _suspendCount == 0;
            if (!shouldStart) SetStatusLocked(_enabled ? "一時停止" : "OFF");
        }
        RaiseStatusChanged();
        if (shouldStart) TryStartListening();
    }

    private void MicrophoneCoordinator_ForegroundCaptureChanged(object? sender, bool active)
    {
        if (active) Suspend();
        else Resume();
    }

    private void TryStartListening()
    {
        SpeechRecognitionEngine? engine = null;
        try
        {
            lock (_gate)
            {
                if (_disposed || !_enabled || _suspendCount > 0 || _engine is not null) return;
                CancelRetryLocked();
                SetStatusLocked("起動中");
            }
            RaiseStatusChanged();

            var recognizer = SelectJapaneseRecognizer();
            if (recognizer is null)
            {
                lock (_gate)
                {
                    if (_disposed) return;
                    SetStatusLocked("日本語音声認識なし");
                    ScheduleRetryLocked();
                }
                RaiseStatusChanged();
                return;
            }

            engine = new SpeechRecognitionEngine(recognizer);
            var phrases = new Choices(
                "ねえ コマンダー",
                "ねぇ コマンダー",
                "ねー コマンダー",
                "ねえコマンダー",
                "ねぇコマンダー",
                "ねーコマンダー");
            var grammarBuilder = new GrammarBuilder { Culture = recognizer.Culture };
            grammarBuilder.Append(phrases);
            engine.LoadGrammar(new Grammar(grammarBuilder));
            engine.SpeechRecognized += Engine_SpeechRecognized;
            engine.RecognizeCompleted += Engine_RecognizeCompleted;
            engine.SetInputToDefaultAudioDevice();

            lock (_gate)
            {
                if (_disposed || !_enabled || _suspendCount > 0)
                {
                    DetachAndDispose(engine);
                    return;
                }
                _engine = engine;
                engine = null;
                _listening = true;
                SetStatusLocked("待機中");
                _engine.RecognizeAsync(RecognizeMode.Multiple);
            }
            RaiseStatusChanged();
        }
        catch
        {
            if (engine is not null) DetachAndDispose(engine);
            lock (_gate)
            {
                if (_disposed) return;
                StopEngineLocked();
                SetStatusLocked("マイク待機");
                ScheduleRetryLocked();
            }
            RaiseStatusChanged();
        }
    }

    private void Engine_SpeechRecognized(object? sender, SpeechRecognizedEventArgs e)
    {
        if (e.Result is null || e.Result.Confidence < MinimumWakeConfidence || !IsWakePhrase(e.Result.Text)) return;

        // Do not dispose the recognizer from inside its recognition callback. Queue the
        // transition so the audio engine can return from the callback first, then fully
        // release the microphone before Commander starts normal dictation.
        _ = Task.Run(() =>
        {
            lock (_gate)
            {
                if (_disposed || !_enabled || _suspendCount > 0) return;
                _suspendCount++;
                StopEngineLocked();
                CancelRetryLocked();
                SetStatusLocked("呼び出し中");
            }
            RaiseStatusChanged();
            try { WakeDetected?.Invoke(this, EventArgs.Empty); } catch { ResumeAfterWakeFailure(); }
        });
    }

    private void ResumeAfterWakeFailure()
    {
        lock (_gate)
        {
            if (_suspendCount > 0) _suspendCount--;
        }
        ResumeIfReady();
    }

    public void CompleteWakeInteraction()
    {
        lock (_gate)
        {
            if (_disposed) return;
            if (_suspendCount > 0) _suspendCount--;
        }
        ResumeIfReady();
    }

    private void ResumeIfReady()
    {
        bool shouldStart;
        lock (_gate)
        {
            if (_disposed) return;
            shouldStart = _enabled && _suspendCount == 0;
            if (!shouldStart) SetStatusLocked(_enabled ? "一時停止" : "OFF");
        }
        RaiseStatusChanged();
        if (shouldStart) TryStartListening();
    }

    private void Engine_RecognizeCompleted(object? sender, RecognizeCompletedEventArgs e)
    {
        lock (_gate)
        {
            if (_disposed || !ReferenceEquals(sender, _engine)) return;
            StopEngineLocked();
            if (_enabled && _suspendCount == 0)
            {
                SetStatusLocked("マイク待機");
                ScheduleRetryLocked();
            }
        }
        RaiseStatusChanged();
    }

    private static bool IsWakePhrase(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return false;
        var normalized = new string(text
            .Where(c => !char.IsWhiteSpace(c) && c is not '、' and not '。' and not ',' and not '.')
            .ToArray())
            .Replace("ねぇ", "ねえ", StringComparison.Ordinal)
            .Replace("ねー", "ねえ", StringComparison.Ordinal);
        return normalized.Contains("ねえコマンダー", StringComparison.OrdinalIgnoreCase);
    }

    private static RecognizerInfo? SelectJapaneseRecognizer()
    {
        var recognizers = SpeechRecognitionEngine.InstalledRecognizers();
        if (recognizers.Count == 0) return null;
        return recognizers.FirstOrDefault(x => x.Culture.Name.Equals("ja-JP", StringComparison.OrdinalIgnoreCase))
            ?? recognizers.FirstOrDefault(x => x.Culture.TwoLetterISOLanguageName.Equals("ja", StringComparison.OrdinalIgnoreCase));
    }

    private void ScheduleRetryLocked()
    {
        if (_disposed || !_enabled || _suspendCount > 0 || _retryTimer is not null) return;
        _retryTimer = new Timer(_ =>
        {
            lock (_gate)
            {
                _retryTimer?.Dispose();
                _retryTimer = null;
            }
            TryStartListening();
        }, null, RetryDelay, Timeout.InfiniteTimeSpan);
    }

    private void CancelRetryLocked()
    {
        _retryTimer?.Dispose();
        _retryTimer = null;
    }

    private void StopEngineLocked()
    {
        var engine = _engine;
        _engine = null;
        _listening = false;
        if (engine is null) return;
        try { engine.RecognizeAsyncCancel(); } catch { }
        DetachAndDispose(engine);
    }

    private void DetachAndDispose(SpeechRecognitionEngine engine)
    {
        try { engine.SpeechRecognized -= Engine_SpeechRecognized; } catch { }
        try { engine.RecognizeCompleted -= Engine_RecognizeCompleted; } catch { }
        try { engine.Dispose(); } catch { }
    }

    private void SetStatusLocked(string status) => _status = status;

    private void RaiseStatusChanged()
    {
        try { StatusChanged?.Invoke(this, EventArgs.Empty); } catch { }
    }

    public void Dispose()
    {
        MicrophoneCoordinator.ForegroundCaptureChanged -= MicrophoneCoordinator_ForegroundCaptureChanged;
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            _enabled = false;
            CancelRetryLocked();
            StopEngineLocked();
            SetStatusLocked("OFF");
        }
    }
}
