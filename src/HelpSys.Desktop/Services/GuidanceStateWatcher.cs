using System.Windows.Automation;

namespace HelpSys.Services;

public sealed class GuidanceStateWatcher : IDisposable
{
    private readonly SemaphoreSlim _signal = new(0, 1);
    private readonly AutomationFocusChangedEventHandler _focusHandler;
    private readonly StructureChangedEventHandler _structureHandler;
    private CancellationTokenSource? _cts;
    private Task? _pumpTask;
    private int _queued;
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
                var heartbeatTask = Task.Delay(TimeSpan.FromMilliseconds(900), cancellationToken);
                var completed = await Task.WhenAny(signalTask, heartbeatTask).ConfigureAwait(false);

                if (completed == signalTask)
                {
                    Interlocked.Exchange(ref _queued, 0);
                    await Task.Delay(180, cancellationToken).ConfigureAwait(false);
                    while (_signal.Wait(0)) Interlocked.Exchange(ref _queued, 0);
                }

                Pulse?.Invoke(this, EventArgs.Empty);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch
            {
                await Task.Delay(900, cancellationToken).ConfigureAwait(false);
            }
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
