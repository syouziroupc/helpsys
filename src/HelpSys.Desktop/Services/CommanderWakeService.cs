using System.Globalization;
using System.Speech.Recognition;

namespace HelpSys.Services;

public sealed class CommanderWakeService : IDisposable
{
    private static readonly TimeSpan RetryDelay = TimeSpan.FromSeconds(3);
    private static readonly TimeSpan ReleaseDeadline = TimeSpan.FromSeconds(2);
    private const float MinimumWakeConfidence = 0.72f;

    private readonly object _gate = new();
    private SpeechRecognitionEngine? _engine;
    private Timer? _retryTimer;
    private bool _enabled = true;
    private bool _disposed;
    private bool _starting;
    private bool _interactionHeld;
    private int _foregroundSuspendCount;
    private bool _listening;
    private string _status = "準備中";

    public event EventHandler? WakeDetected;
    public event EventHandler? StatusChanged;

    public CommanderWakeService()
    {
        MicrophoneCoordinator.ForegroundCaptureChanged += MicrophoneCoordinator_ForegroundCaptureChanged;
    }

    public bool Enabled { get { lock (_gate) return _enabled; } }
    public bool Listening { get { lock (_gate) return _listening; } }
    public string Status { get { lock (_gate) return _status; } }

    public void Start() => SetEnabled(true);

    public void SetEnabled(bool enabled)
    {
        SpeechRecognitionEngine? release = null;
        bool shouldStart;
        lock (_gate)
        {
            if (_disposed) return;
            _enabled = enabled;
            if (!enabled)
            {
                _interactionHeld = false;
                _starting = false;
                release = DetachEngineLocked();
                CancelRetryLocked();
                SetStatusLocked("OFF");
            }
            shouldStart = CanStartLocked();
        }

        TrackRelease(release);
        RaiseStatusChanged();
        if (shouldStart) QueueStart();
    }

    public void Suspend()
    {
        SpeechRecognitionEngine? release;
        lock (_gate)
        {
            if (_disposed) return;
            _foregroundSuspendCount++;
            release = DetachEngineLocked();
            CancelRetryLocked();
            SetStatusLocked(_enabled ? "他の音声入力を優先" : "OFF");
        }

        TrackRelease(release);
        RaiseStatusChanged();
    }

    public void Resume()
    {
        bool shouldStart;
        lock (_gate)
        {
            if (_disposed) return;
            if (_foregroundSuspendCount > 0) _foregroundSuspendCount--;
            shouldStart = CanStartLocked();
            if (!shouldStart && _enabled && _foregroundSuspendCount > 0) SetStatusLocked("他の音声入力を優先");
        }
        RaiseStatusChanged();
        if (shouldStart) QueueStart();
    }

    public void CompleteWakeInteraction()
    {
        bool shouldStart;
        lock (_gate)
        {
            if (_disposed) return;
            _interactionHeld = false;
            shouldStart = CanStartLocked();
            if (!shouldStart && _enabled && _foregroundSuspendCount > 0) SetStatusLocked("他の音声入力を優先");
        }
        RaiseStatusChanged();
        if (shouldStart) QueueStart();
    }

    private void MicrophoneCoordinator_ForegroundCaptureChanged(object? sender, bool active)
    {
        if (active) Suspend();
        else Resume();
    }

    private bool CanStartLocked()
        => !_disposed && _enabled && !_interactionHeld && _foregroundSuspendCount == 0 && !_starting && _engine is null;

    private void QueueStart() => _ = Task.Run(StartListeningAsync);

    private async Task StartListeningAsync()
    {
        lock (_gate)
        {
            if (!CanStartLocked()) return;
            _starting = true;
            CancelRetryLocked();
            SetStatusLocked("マイク準備中");
        }
        RaiseStatusChanged();

        SpeechRecognitionEngine? candidate = null;
        try
        {
            if (!await MicrophoneCoordinator.WaitForBackgroundReleaseAsync().ConfigureAwait(false))
            {
                lock (_gate)
                {
                    _starting = false;
                    if (!_disposed && _enabled) {
                        SetStatusLocked("マイク解放待ち");
                        ScheduleRetryLocked();
                    }
                }
                RaiseStatusChanged();
                return;
            }

            var recognizer = SelectJapaneseRecognizer();
            if (recognizer is null)
            {
                lock (_gate)
                {
                    _starting = false;
                    if (!_disposed && _enabled)
                    {
                        SetStatusLocked("日本語音声認識なし");
                        ScheduleRetryLocked();
                    }
                }
                RaiseStatusChanged();
                return;
            }

            candidate = new SpeechRecognitionEngine(recognizer);
            var phrases = new Choices(
                "ねえ コマンダー", "ねぇ コマンダー", "ねー コマンダー",
                "ねえコマンダー", "ねぇコマンダー", "ねーコマンダー");
            var grammarBuilder = new GrammarBuilder { Culture = recognizer.Culture };
            grammarBuilder.Append(phrases);
            candidate.LoadGrammar(new Grammar(grammarBuilder));
            candidate.SpeechRecognized += Engine_SpeechRecognized;
            candidate.RecognizeCompleted += Engine_RecognizeCompleted;
            candidate.SetInputToDefaultAudioDevice();
            candidate.RecognizeAsync(RecognizeMode.Multiple);

            bool accepted;
            lock (_gate)
            {
                _starting = false;
                accepted = CanStartLocked();
                if (accepted)
                {
                    _engine = candidate;
                    candidate = null;
                    _listening = true;
                    SetStatusLocked("待機中");
                }
            }
            if (!accepted) TrackRelease(candidate);
            RaiseStatusChanged();
        }
        catch
        {
            TrackRelease(candidate);
            lock (_gate)
            {
                _starting = false;
                if (!_disposed && _enabled)
                {
                    SetStatusLocked("マイク再試行待ち");
                    ScheduleRetryLocked();
                }
            }
            RaiseStatusChanged();
        }
    }

    private void Engine_SpeechRecognized(object? sender, SpeechRecognizedEventArgs e)
    {
        if (e.Result is null || e.Result.Confidence < MinimumWakeConfidence || !IsWakePhrase(e.Result.Text)) return;

        SpeechRecognitionEngine? release;
        lock (_gate)
        {
            if (_disposed || !_enabled || _interactionHeld || _foregroundSuspendCount > 0 || !ReferenceEquals(sender, _engine)) return;
            _interactionHeld = true;
            release = DetachEngineLocked();
            CancelRetryLocked();
            SetStatusLocked("マイク切替中");
        }
        RaiseStatusChanged();

        var releaseTask = ReleaseEngineAsync(release);
        MicrophoneCoordinator.TrackBackgroundRelease(releaseTask);
        _ = NotifyWakeAfterReleaseAsync(releaseTask);
    }

    private async Task NotifyWakeAfterReleaseAsync(Task releaseTask)
    {
        try
        {
            await releaseTask.WaitAsync(ReleaseDeadline).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            lock (_gate)
            {
                if (_disposed) return;
                _interactionHeld = false;
                SetStatusLocked("マイク再初期化待ち");
            }
            RaiseStatusChanged();
            _ = RestartAfterSlowReleaseAsync(releaseTask);
            return;
        }
        catch
        {
            CompleteWakeInteraction();
            return;
        }

        lock (_gate)
        {
            if (_disposed || !_enabled || !_interactionHeld) return;
            SetStatusLocked("呼び出し中");
        }
        RaiseStatusChanged();

        try { WakeDetected?.Invoke(this, EventArgs.Empty); }
        catch { CompleteWakeInteraction(); }
    }

    private async Task RestartAfterSlowReleaseAsync(Task releaseTask)
    {
        try { await releaseTask.ConfigureAwait(false); } catch { }
        bool shouldStart;
        lock (_gate) shouldStart = CanStartLocked();
        if (shouldStart) QueueStart();
    }

    private void Engine_RecognizeCompleted(object? sender, RecognizeCompletedEventArgs e)
    {
        SpeechRecognitionEngine? release = null;
        lock (_gate)
        {
            if (_disposed || !ReferenceEquals(sender, _engine)) return;
            release = DetachEngineLocked();
            if (_enabled && !_interactionHeld && _foregroundSuspendCount == 0)
            {
                SetStatusLocked("マイク再試行待ち");
                ScheduleRetryLocked();
            }
        }
        TrackRelease(release);
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
        return normalized.Equals("ねえコマンダー", StringComparison.OrdinalIgnoreCase);
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
        if (_disposed || !_enabled || _interactionHeld || _foregroundSuspendCount > 0 || _retryTimer is not null) return;
        _retryTimer = new Timer(_ =>
        {
            lock (_gate)
            {
                _retryTimer?.Dispose();
                _retryTimer = null;
            }
            QueueStart();
        }, null, RetryDelay, Timeout.InfiniteTimeSpan);
    }

    private void CancelRetryLocked()
    {
        _retryTimer?.Dispose();
        _retryTimer = null;
    }

    private SpeechRecognitionEngine? DetachEngineLocked()
    {
        var engine = _engine;
        _engine = null;
        _listening = false;
        return engine;
    }

    private void TrackRelease(SpeechRecognitionEngine? engine)
    {
        if (engine is null) return;
        var releaseTask = ReleaseEngineAsync(engine);
        MicrophoneCoordinator.TrackBackgroundRelease(releaseTask);
    }

    private Task ReleaseEngineAsync(SpeechRecognitionEngine? engine)
    {
        if (engine is null) return Task.CompletedTask;
        return Task.Run(() =>
        {
            try { engine.SpeechRecognized -= Engine_SpeechRecognized; } catch { }
            try { engine.RecognizeCompleted -= Engine_RecognizeCompleted; } catch { }
            try { engine.RecognizeAsyncCancel(); } catch { }
            try { engine.Dispose(); } catch { }
        });
    }

    private void SetStatusLocked(string status) => _status = status;

    private void RaiseStatusChanged()
    {
        try { StatusChanged?.Invoke(this, EventArgs.Empty); } catch { }
    }

    public void Dispose()
    {
        MicrophoneCoordinator.ForegroundCaptureChanged -= MicrophoneCoordinator_ForegroundCaptureChanged;
        SpeechRecognitionEngine? release;
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            _enabled = false;
            _interactionHeld = false;
            _starting = false;
            CancelRetryLocked();
            release = DetachEngineLocked();
            SetStatusLocked("OFF");
        }
        TrackRelease(release);
    }
}
