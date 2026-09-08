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

    public event EventHandler? Pulse;

    public GuidanceStateWatcher()
    {
        _focusHandler = (_, _) => Signal();
        _structureHandler = (_, _) => Signal();
    }

    public void Start()
    {
        if (_cts is not null) return;
        _cts = new CancellationTokenSource();

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
        Interlocked.Exchange(ref _lastSignalTicks, DateTime.UtcNow.Ticks);
        if (Interlocked.Exchange(ref _queued, 1) != 0) return;
        try { _signal.Release(); } catch (SemaphoreFullException) { }
    }

    private async Task PumpAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                var signalTask = _signal.WaitAsync(cancellationToken);
                var heartbeatTask = Task.Delay(HeartbeatPeriod, cancellationToken);
                var completed = await Task.WhenAny(signalTask, heartbeatTask).ConfigureAwait(false);

                if (completed == signalTask)
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
            catch
            {
                await Task.Delay(HeartbeatPeriod, cancellationToken).ConfigureAwait(false);
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
        _cts = null;
        if (cts is null) return;

        cts.Cancel();
        if (_subscribed)
        {
            try { Automation.RemoveAutomationFocusChangedEventHandler(_focusHandler); } catch { }
            try { Automation.RemoveStructureChangedEventHandler(AutomationElement.RootElement, _structureHandler); } catch { }
            _subscribed = false;
        }
        cts.Dispose();
    }

    public void Dispose()
    {
        Stop();
        _signal.Dispose();
    }
}
