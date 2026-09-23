using System.Runtime.InteropServices;

namespace HelpSys.Services;

public sealed class GuidanceStateWatcher : IDisposable
{
    private const uint EventObjectShow = 0x8002;
    private const uint EventObjectValueChange = 0x800E;
    private const uint WineventOutofcontext = 0x0000;
    private const uint WineventSkipownprocess = 0x0002;

    private static readonly TimeSpan QuietPeriod = TimeSpan.FromMilliseconds(320);
    private static readonly TimeSpan HeartbeatPeriod = TimeSpan.FromSeconds(5);

    private readonly SemaphoreSlim _signal = new(0, 1);
    private readonly WinEventDelegate _eventDelegate;
    private CancellationTokenSource? _cts;
    private Task? _pumpTask;
    private nint _eventHook;
    private int _scopeProcessId;
    private int _queued;
    private long _lastSignalTicks;
    private bool _disposed;

    public event EventHandler? Pulse;
    public event EventHandler? Changed;

    public GuidanceStateWatcher()
    {
        _eventDelegate = OnWinEvent;
    }

    public void Start()
    {
        if (_disposed) throw new ObjectDisposedException(nameof(GuidanceStateWatcher));
        if (_cts is not null) return;

        _eventHook = SetWinEventHook(
            EventObjectShow,
            EventObjectValueChange,
            nint.Zero,
            _eventDelegate,
            0,
            0,
            WineventOutofcontext | WineventSkipownprocess);

        var owner = new CancellationTokenSource();
        _cts = owner;
        Interlocked.Exchange(ref _queued, 0);
        Interlocked.Exchange(ref _lastSignalTicks, 0);
        _pumpTask = Task.Run(() => PumpAsync(owner.Token));
    }

    public Task SetForegroundProcessAsync(int processId, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (_disposed) return Task.CompletedTask;

        var previous = Interlocked.Exchange(ref _scopeProcessId, Math.Max(0, processId));
        if (previous != processId) Signal();
        return Task.CompletedTask;
    }

    private void OnWinEvent(
        nint hook,
        uint eventType,
        nint hwnd,
        int idObject,
        int idChild,
        uint eventThread,
        uint eventTime)
    {
        if (_disposed || hwnd == nint.Zero) return;
        var scope = Volatile.Read(ref _scopeProcessId);

        _ = GetWindowThreadProcessId(hwnd, out var pid);
        if (!OutlawModePolicy.Enabled)
        {
            if (scope <= 0) return;
            if (unchecked((int)pid) != scope) return;
        }

        Signal();
    }

    private void Signal()
    {
        if (_disposed) return;
        Interlocked.Exchange(ref _lastSignalTicks, DateTime.UtcNow.Ticks);
        try { Changed?.Invoke(this, EventArgs.Empty); } catch { }

        if (Interlocked.Exchange(ref _queued, 1) != 0) return;
        try { _signal.Release(); }
        catch (SemaphoreFullException) { }
        catch (ObjectDisposedException) { }
    }

    private async Task PumpAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                var signaled = await _signal.WaitAsync(HeartbeatPeriod, cancellationToken).ConfigureAwait(false);
                if (signaled)
                {
                    await WaitUntilQuietAsync(cancellationToken).ConfigureAwait(false);
                    Interlocked.Exchange(ref _queued, 0);
                }

                Pulse?.Invoke(this, EventArgs.Empty);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (ObjectDisposedException)
            {
                break;
            }
            catch
            {
                try { await Task.Delay(HeartbeatPeriod, cancellationToken).ConfigureAwait(false); }
                catch (OperationCanceledException) { break; }
            }
        }
    }

    private async Task WaitUntilQuietAsync(CancellationToken cancellationToken)
    {
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var ticks = Volatile.Read(ref _lastSignalTicks);
            if (ticks <= 0) return;

            var quietFor = DateTime.UtcNow - new DateTime(ticks, DateTimeKind.Utc);
            if (quietFor >= QuietPeriod) return;

            var remaining = QuietPeriod - quietFor;
            await Task.Delay(
                remaining < TimeSpan.FromMilliseconds(80) ? remaining : TimeSpan.FromMilliseconds(80),
                cancellationToken).ConfigureAwait(false);
        }
    }

    public void Stop()
    {
        var cts = _cts;
        _cts = null;
        _pumpTask = null;
        Interlocked.Exchange(ref _scopeProcessId, 0);
        Interlocked.Exchange(ref _queued, 0);

        if (_eventHook != nint.Zero)
        {
            try { UnhookWinEvent(_eventHook); } catch { }
            _eventHook = nint.Zero;
        }

        if (cts is null) return;
        try { cts.Cancel(); } catch { }
        cts.Dispose();
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        Stop();
        _signal.Dispose();
        GC.SuppressFinalize(this);
    }

    private delegate void WinEventDelegate(
        nint hWinEventHook,
        uint eventType,
        nint hwnd,
        int idObject,
        int idChild,
        uint idEventThread,
        uint eventTime);

    [DllImport("user32.dll")]
    private static extern nint SetWinEventHook(
        uint eventMin,
        uint eventMax,
        nint hmodWinEventProc,
        WinEventDelegate lpfnWinEventProc,
        uint idProcess,
        uint idThread,
        uint flags);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool UnhookWinEvent(nint hWinEventHook);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(nint hWnd, out uint lpdwProcessId);
}
