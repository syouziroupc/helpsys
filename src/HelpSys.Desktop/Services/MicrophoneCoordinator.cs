namespace HelpSys.Services;

public static class MicrophoneCoordinator
{
    private static readonly object Gate = new();
    private static int _foregroundCaptureCount;
    private static Task _backgroundReleaseTask = Task.CompletedTask;

    public static event EventHandler<bool>? ForegroundCaptureChanged;

    public static IDisposable BeginForegroundCapture()
    {
        bool changed;
        lock (Gate)
        {
            changed = _foregroundCaptureCount++ == 0;
        }
        if (changed) Raise(true);
        return new Lease();
    }

    public static void TrackBackgroundRelease(Task? releaseTask)
    {
        lock (Gate)
        {
            _backgroundReleaseTask = releaseTask ?? Task.CompletedTask;
        }
    }

    public static async Task<bool> WaitForBackgroundReleaseAsync(CancellationToken cancellationToken = default)
    {
        Task releaseTask;
        lock (Gate) releaseTask = _backgroundReleaseTask;
        if (releaseTask.IsCompleted) return true;

        try
        {
            await releaseTask.WaitAsync(TimeSpan.FromSeconds(2), cancellationToken).ConfigureAwait(false);
            return true;
        }
        catch (TimeoutException)
        {
            return false;
        }
    }

    private static void EndForegroundCapture()
    {
        bool changed = false;
        lock (Gate)
        {
            if (_foregroundCaptureCount <= 0) return;
            _foregroundCaptureCount--;
            changed = _foregroundCaptureCount == 0;
        }
        if (changed) Raise(false);
    }

    private static void Raise(bool active)
    {
        try { ForegroundCaptureChanged?.Invoke(null, active); } catch { }
    }

    private sealed class Lease : IDisposable
    {
        private int _disposed;
        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
            EndForegroundCapture();
        }
    }
}
