using System.Windows.Automation;

namespace HelpSys.Services;

public sealed class GuidanceStateWatcher : IDisposable
{
    private static readonly TimeSpan QuietPeriod = TimeSpan.FromMilliseconds(650);
    private static readonly TimeSpan HeartbeatPeriod = TimeSpan.FromMilliseconds(1400);

    private readonly SemaphoreSlim _signal = new(0, 1);
    private readonly AutomationFocusChangedEventHandler _focusHandler;
    private readonly StructureChangedEventHandler _structureHandler;
    private CancellationTokenSource? _cts;
    private Task? _pumpTask;
    private int _queued;
    private long _lastSignalTicks;
    private bool _subscribed;
    private bool _disposed;

    public event EventHandler? Pulse;

    public GuidanceStateWatcher()
    {
        _focusHandler = (_, _) => Signal();
        _structureHandler = (_, _) => Signal();
    }

    public void Start()
    {
        if (_disposed) throw new ObjectDisposedException(nameof(GuidanceStateWatcher));
        if (_cts is not null) return;

        _cts = new CancellationTokenSource();
        Interlocked.Exchange(ref _queued, 0);
        Interlocked.Exchange(ref _lastSignalTicks, 0);

        try
        {
            Automation.AddAutomationFocusChangedEventHandler(_focusHandler);
            Automation.AddStructureChangedEventHandler(AutomationElement.RootElement, TreeScope.Subtree, _structureHandler);
            _subscribed = true;
        }
        catch
        {
            _subscribed = false;
        }

        _pumpTask = Task.Run(() => PumpAsync(_cts.Token));
    }

    private void Signal()
    {
        if (_disposed) return;
        Interlocked.Exchange(ref _lastSignalTicks, DateTime.UtcNow.Ticks);
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
                // A single timed WaitAsync avoids leaving an abandoned semaphore waiter behind
                // every time the heartbeat wins a Task.WhenAny race. Abandoned waiters could
                // consume later UI signals without clearing _queued and silently break debounce.
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

            var lastSignalUtc = new DateTime(ticks, DateTimeKind.Utc);
            var quietFor = DateTime.UtcNow - lastSignalUtc;
            if (quietFor >= QuietPeriod) return;

            var remaining = QuietPeriod - quietFor;
            var delay = remaining < TimeSpan.FromMilliseconds(120) ? remaining : TimeSpan.FromMilliseconds(120);
            await Task.Delay(delay, cancellationToken).ConfigureAwait(false);
        }
    }

    public void Stop()
    {
        var cts = _cts;
        var pump = _pumpTask;
        _cts = null;
        _pumpTask = null;
        if (cts is null) return;

        cts.Cancel();
        if (_subscribed)
        {
            try { Automation.RemoveAutomationFocusChangedEventHandler(_focusHandler); } catch { }
            try { Automation.RemoveStructureChangedEventHandler(AutomationElement.RootElement, _structureHandler); } catch { }
            _subscribed = false;
        }

        // Do not dispose synchronization primitives while PumpAsync can still be inside them.
        // The pump contains no UI-thread dependency, so joining it here cannot deadlock WPF.
        if (pump is not null && (!Task.CurrentId.HasValue || pump.Id != Task.CurrentId.Value))
        {
            try { pump.GetAwaiter().GetResult(); }
            catch (OperationCanceledException) { }
            catch (ObjectDisposedException) { }
        }

        Interlocked.Exchange(ref _queued, 0);
        cts.Dispose();
    }

    public void Dispose()
    {
        if (_disposed) return;
        Stop();
        _disposed = true;
        _signal.Dispose();
    }
}
