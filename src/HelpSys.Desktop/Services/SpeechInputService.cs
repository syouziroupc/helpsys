using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Windows;
using System.Windows.Threading;
using NAudio.Wave;

namespace HelpSys.Services;

public sealed class SpeechInputService : IDisposable
{
    private const string DefaultApiBase = "https://helpsys.syouziroupc.workers.dev";
    private const string CloudflareCompatibleUserAgent = "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/142.0.0.0 Safari/537.36";
    private static readonly TimeSpan InitialSilenceTimeout = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan EndSilenceTimeout = TimeSpan.FromMilliseconds(900);
    private static readonly TimeSpan MaximumCaptureTime = TimeSpan.FromSeconds(12);
    private static readonly TimeSpan TranscriptionTimeout = TimeSpan.FromSeconds(18);
    private const double MinimumVoiceLevel = 0.018;

    private readonly HttpClient _http = new() { Timeout = Timeout.InfiniteTimeSpan };
    private readonly string _apiBase;
    private readonly string? _apiKey;
    private readonly JsonSerializerOptions _jsonOptions = new() { PropertyNameCaseInsensitive = true };
    private readonly ListeningOverlayWindow _listeningOverlay = new();
    private DispatcherTimer? _overlayHideTimer;
    private int _captureActive;
    private bool _disposed;

    public SpeechInputService()
    {
        _apiBase = (Environment.GetEnvironmentVariable("HELPSYS_API_BASE") ?? DefaultApiBase).TrimEnd('/');
        _apiKey = Environment.GetEnvironmentVariable("HELPSYS_API_KEY");
        _http.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent", CloudflareCompatibleUserAgent);
        _http.DefaultRequestHeaders.TryAddWithoutValidation("x-helpsys-client", "desktop-speech");
    }

    public async Task<string?> RecognizeOnceAsync(CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
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

            var level = CalculatePeak(e.Buffer, e.BytesRecorded);
            var elapsed = stopwatch.Elapsed;
            if (level >= MinimumVoiceLevel)
            {
                Interlocked.Exchange(ref speechHeard, 1);
                lastVoice = elapsed;
            }
            ShowListening(level);

            if (elapsed >= MaximumCaptureTime ||
                (Volatile.Read(ref speechHeard) == 0 && elapsed >= InitialSilenceTimeout) ||
                (Volatile.Read(ref speechHeard) != 0 && elapsed - lastVoice >= EndSilenceTimeout))
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
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(TranscriptionTimeout);

        using var request = new HttpRequestMessage(HttpMethod.Post, $"{_apiBase}/v1/transcribe");
        request.Content = new ByteArrayContent(wave);
        request.Content.Headers.ContentType = new MediaTypeHeaderValue("audio/wav");
        request.Headers.TryAddWithoutValidation("x-helpsys-request-id", Guid.NewGuid().ToString("N"));
        if (!string.IsNullOrWhiteSpace(_apiKey)) request.Headers.TryAddWithoutValidation("x-helpsys-key", _apiKey);

        HttpResponseMessage response;
        string body;
        try
        {
            response = await _http.SendAsync(request, timeoutCts.Token).ConfigureAwait(false);
            body = await response.Content.ReadAsStringAsync(timeoutCts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException ex) when (!cancellationToken.IsCancellationRequested && timeoutCts.IsCancellationRequested)
        {
            throw new InvalidOperationException("音声認識サービスの応答が時間内に返りませんでした。", ex);
        }

        using (response)
        {
            if (!response.IsSuccessStatusCode)
                throw new InvalidOperationException($"音声認識サービスが応答できませんでした ({(int)response.StatusCode})。");

            try
            {
                var result = JsonSerializer.Deserialize<TranscriptionResponse>(body, _jsonOptions);
                return string.IsNullOrWhiteSpace(result?.Text) ? null : result.Text;
            }
            catch (JsonException ex)
            {
                throw new InvalidOperationException("音声認識サービスの応答形式が不正です。", ex);
            }
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

    private static double CalculatePeak(byte[] buffer, int count)
    {
        var peak = 0;
        for (var i = 0; i + 1 < count; i += 2)
        {
            var sample = (short)(buffer[i] | (buffer[i + 1] << 8));
            var value = Math.Abs(sample == short.MinValue ? short.MaxValue : sample);
            if (value > peak) peak = value;
        }
        return Math.Clamp(peak / 32767d, 0d, 1d);
    }

    private void DispatchOverlay(Action action)
    {
        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher is null || dispatcher.HasShutdownStarted || dispatcher.HasShutdownFinished) return;
        dispatcher.BeginInvoke(new Action(() =>
        {
            if (_disposed) return;
            action();
        }));
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        try { _overlayHideTimer?.Stop(); } catch { }
        try { _listeningOverlay.Close(); } catch { }
        _http.Dispose();
    }

    private sealed record TranscriptionResponse(string? Text, string? Model);
}
