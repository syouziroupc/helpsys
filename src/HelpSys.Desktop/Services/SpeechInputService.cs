using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Threading;
using HelpSys.Shared;
using NAudio.Wave;

namespace HelpSys.Services;

public sealed class SpeechInputService : IDisposable
{
    private static readonly TimeSpan InitialSilenceTimeout = TimeSpan.FromSeconds(6);
    private static readonly TimeSpan EndSilenceTimeout = TimeSpan.FromMilliseconds(900);
    private static readonly TimeSpan MaximumCaptureTime = TimeSpan.FromSeconds(12);
    private static readonly TimeSpan MinimumSpeechTime = TimeSpan.FromMilliseconds(240);
    private static readonly TimeSpan TranscriptionTimeout = TimeSpan.FromSeconds(18);
    private const double InitialNoiseFloor = 0.0025;
    private const double MinimumStartThreshold = 0.010;
    private const double MaximumStartThreshold = 0.065;

    private readonly CloudAiAdapter _adapter = new();
    private readonly JsonSerializerOptions _jsonOptions = new() { PropertyNameCaseInsensitive = true };
    private readonly ListeningOverlayWindow _listeningOverlay = new();
    private DispatcherTimer? _overlayHideTimer;
    private int _captureActive;
    private bool _disposed;

    public bool CloudTranscriptionAllowed
    {
        get
        {
#if HELPSYS_SAFE_BUILD
            return false;
#else
            return true;
#endif
        }
    }

    public async Task<string?> RecognizeOnceAsync(CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (!CloudTranscriptionAllowed)
            throw new InvalidOperationException("安全版では音声データをクラウドへ送信しません。文字入力を使用してください。");

        if (Interlocked.Exchange(ref _captureActive, 1) != 0)
            throw new InvalidOperationException("別の音声入力がまだ終了していません。");

        using var microphoneLease = MicrophoneCoordinator.BeginForegroundCapture();
        try
        {
            if (!await MicrophoneCoordinator.WaitForBackgroundReleaseAsync(cancellationToken).ConfigureAwait(false))
                throw new InvalidOperationException("コマンダーのマイク待機を切り替えられませんでした。数秒後にもう一度試してください。");

            ShowListening(0);
            var wave = await CaptureUtteranceAsync(cancellationToken).ConfigureAwait(false);
            if (wave is null || wave.Length < 256)
            {
                ShowTransientResult("聞き取れませんでした");
                return null;
            }

            ShowTranscribing();
            var text = await TranscribeAsync(wave, cancellationToken).ConfigureAwait(false);
            if (string.IsNullOrWhiteSpace(text))
            {
                ShowTransientResult("聞き取れませんでした");
                return null;
            }

            text = text.Trim();
            ShowTransientResult(text, TimeSpan.FromSeconds(1.8));
            return text;
        }
        catch (OperationCanceledException)
        {
            HideOverlay();
            throw;
        }
        catch
        {
            HideOverlay();
            throw;
        }
        finally
        {
            Interlocked.Exchange(ref _captureActive, 0);
        }
    }

    public void HideOverlay()
    {
        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher is null || dispatcher.HasShutdownStarted || dispatcher.HasShutdownFinished) return;
        dispatcher.BeginInvoke(new Action(() =>
        {
            _overlayHideTimer?.Stop();
            if (_listeningOverlay.IsVisible) _listeningOverlay.Hide();
        }));
    }

    private async Task<byte[]?> CaptureUtteranceAsync(CancellationToken cancellationToken)
    {
        using var waveIn = new WaveIn
        {
            WaveFormat = new WaveFormat(16000, 16, 1),
            BufferMilliseconds = 100,
            NumberOfBuffers = 3
        };
        using var stream = new MemoryStream();
        var writer = new WaveFileWriter(stream, waveIn.WaveFormat);
        var completion = new TaskCompletionSource<byte[]?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var stopwatch = Stopwatch.StartNew();
        var lastVoice = TimeSpan.Zero;
        var speechStartedAt = TimeSpan.Zero;
        var noiseFloor = InitialNoiseFloor;
        var possibleVoiceFrames = 0;
        var speechHeard = 0;
        var stopRequested = 0;
        var cancelled = 0;
        var writerDisposed = 0;

        void DisposeWriter()
        {
            if (Interlocked.Exchange(ref writerDisposed, 1) != 0) return;
            try { writer.Dispose(); } catch { }
        }

        void RequestStop()
        {
            if (Interlocked.Exchange(ref stopRequested, 1) != 0) return;
            try { waveIn.StopRecording(); }
            catch (Exception ex) { completion.TrySetException(ex); }
        }

        void OnDataAvailable(object? sender, WaveInEventArgs e)
        {
            if (e.BytesRecorded <= 0 || Volatile.Read(ref writerDisposed) != 0) return;
            try
            {
                writer.Write(e.Buffer, 0, e.BytesRecorded);
            }
            catch (Exception ex)
            {
                completion.TrySetException(ex);
                RequestStop();
                return;
            }

            var level = CalculateRms(e.Buffer, e.BytesRecorded);
            var elapsed = stopwatch.Elapsed;
            var startThreshold = Math.Clamp(noiseFloor * 2.6 + 0.004, MinimumStartThreshold, MaximumStartThreshold);
            var continueThreshold = Math.Clamp(
                Math.Max(noiseFloor * 1.55 + 0.0025, startThreshold * 0.55),
                0.006,
                startThreshold * 0.82);

            if (Volatile.Read(ref speechHeard) == 0)
            {
                if (level >= startThreshold)
                {
                    possibleVoiceFrames++;
                }
                else
                {
                    possibleVoiceFrames = Math.Max(0, possibleVoiceFrames - 1);
                    noiseFloor = UpdateNoiseFloor(noiseFloor, level, 0.08);
                }

                // Two consecutive 100 ms frames prevent clicks/fan transients from starting a turn.
                if (possibleVoiceFrames >= 2)
                {
                    Interlocked.Exchange(ref speechHeard, 1);
                    speechStartedAt = elapsed;
                    lastVoice = elapsed;
                }
            }
            else
            {
                // Hysteresis: once speech starts, use a lower threshold so quiet syllables do not
                // terminate the utterance, while steady microphone noise still falls below it.
                if (level >= continueThreshold)
                {
                    lastVoice = elapsed;
                }
                else
                {
                    noiseFloor = UpdateNoiseFloor(noiseFloor, level, 0.015);
                }
            }

            var displayReference = Volatile.Read(ref speechHeard) == 0 ? startThreshold : continueThreshold;
            ShowListening(Math.Clamp(level / Math.Max(displayReference * 2.2, 0.02), 0d, 1d));

            if (elapsed >= MaximumCaptureTime ||
                (Volatile.Read(ref speechHeard) == 0 && elapsed >= InitialSilenceTimeout) ||
                (Volatile.Read(ref speechHeard) != 0 &&
                 elapsed - speechStartedAt >= MinimumSpeechTime &&
                 elapsed - lastVoice >= EndSilenceTimeout))
            {
                RequestStop();
            }
        }

        void OnRecordingStopped(object? sender, StoppedEventArgs e)
        {
            try
            {
                DisposeWriter();
                if (Volatile.Read(ref cancelled) != 0)
                    completion.TrySetCanceled(cancellationToken);
                else if (e.Exception is not null)
                    completion.TrySetException(e.Exception);
                else if (Volatile.Read(ref speechHeard) == 0)
                    completion.TrySetResult(null);
                else
                    completion.TrySetResult(stream.ToArray());
            }
            catch (Exception ex)
            {
                completion.TrySetException(ex);
            }
        }

        waveIn.DataAvailable += OnDataAvailable;
        waveIn.RecordingStopped += OnRecordingStopped;
        using var registration = cancellationToken.Register(() =>
        {
            Interlocked.Exchange(ref cancelled, 1);
            RequestStop();
        });

        try
        {
            waveIn.StartRecording();
            return await completion.Task.ConfigureAwait(false);
        }
        finally
        {
            waveIn.DataAvailable -= OnDataAvailable;
            waveIn.RecordingStopped -= OnRecordingStopped;
            DisposeWriter();
        }
    }

    private async Task<string?> TranscribeAsync(byte[] wave, CancellationToken cancellationToken)
    {
        var response = await _adapter.PostBytesAsync(
            "/v1/transcribe",
            wave,
            "audio/wav",
            TranscriptionTimeout,
            cancellationToken).ConfigureAwait(false);

        if (!response.IsSuccessStatusCode)
            throw new InvalidOperationException($"音声認識サービスが応答できませんでした ({response.StatusCode})。");

        try
        {
            var result = JsonSerializer.Deserialize<TranscriptionResponse>(response.Body, _jsonOptions);
            return string.IsNullOrWhiteSpace(result?.Text) ? null : result.Text;
        }
        catch (JsonException ex)
        {
            throw new InvalidOperationException("音声認識サービスの応答形式が不正です。", ex);
        }
    }

    private void ShowListening(double level) => DispatchOverlay(() => _listeningOverlay.ShowListening(level));
    private void ShowTranscribing() => DispatchOverlay(_listeningOverlay.ShowTranscribing);

    private void ShowTransientResult(string text, TimeSpan? duration = null)
    {
        DispatchOverlay(() =>
        {
            _listeningOverlay.ShowTranscript(text);
            _overlayHideTimer ??= new DispatcherTimer();
            _overlayHideTimer.Stop();
            _overlayHideTimer.Interval = duration ?? TimeSpan.FromSeconds(1.2);
            _overlayHideTimer.Tick -= OverlayHideTimer_Tick;
            _overlayHideTimer.Tick += OverlayHideTimer_Tick;
            _overlayHideTimer.Start();
        });
    }

    private void OverlayHideTimer_Tick(object? sender, EventArgs e)
    {
        _overlayHideTimer?.Stop();
        if (_listeningOverlay.IsVisible) _listeningOverlay.Hide();
    }

    private static double CalculateRms(byte[] buffer, int count)
    {
        if (count < 2) return 0d;
        double sumSquares = 0d;
        var samples = 0;
        for (var i = 0; i + 1 < count; i += 2)
        {
            var sample = (short)(buffer[i] | (buffer[i + 1] << 8));
            var normalized = sample / 32768d;
            sumSquares += normalized * normalized;
            samples++;
        }
        return samples == 0 ? 0d : Math.Clamp(Math.Sqrt(sumSquares / samples), 0d, 1d);
    }

    private static double UpdateNoiseFloor(double current, double observed, double alpha)
    {
        if (!double.IsFinite(observed) || observed < 0) return current;
        var bounded = Math.Clamp(observed, 0d, MaximumStartThreshold);
        return Math.Clamp(current * (1d - alpha) + bounded * alpha, 0.0005, 0.04);
    }

    private void DispatchOverlay(Action action)
    {
        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher is null || dispatcher.HasShutdownStarted || dispatcher.HasShutdownFinished) return;
        dispatcher.BeginInvoke(new Action(() =>
        {
            if (_disposed) return;
            _overlayHideTimer?.Stop();
            action();
        }));
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _adapter.Dispose();
        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher is null || dispatcher.HasShutdownStarted || dispatcher.HasShutdownFinished) return;
        dispatcher.BeginInvoke(new Action(() =>
        {
            _overlayHideTimer?.Stop();
            _overlayHideTimer = null;
            try { _listeningOverlay.Close(); } catch { }
        }));
    }

    private sealed record TranscriptionResponse(string? Text);
}
