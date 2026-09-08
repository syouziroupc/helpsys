using System.Windows.Automation;

namespace HelpSys.Services;

public sealed class GuidanceStateWatcher : IDisposable
{
    private static readonly TimeSpan QuietPeriod = TimeSpan.FromMilliseconds(650);
    private static readonly TimeSpan HeartbeatPeriod = TimeSpan.FromMilliseconds(1400);

    private readonly SemaphoreSlim _signal = new(0, 1);
    private readonly object _subscriptionGate = new();
    private readonly AutomationFocusChangedEventHandler _focusHandler;
    private readonly StructureChangedEventHandler _structureHandler;
    private CancellationTokenSource? _cts;
    private Task? _pumpTask;
    private AutomationElement? _structureRoot;
    private int _scopeProcessId;
    private int _queued;
    private long _lastSignalTicks;
    private bool _focusSubscribed;
    private bool _structureSubscribed;
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

        lock (_subscriptionGate)
        {
            try
            {
                Automation.AddAutomationFocusChangedEventHandler(_focusHandler);
                _focusSubscribed = true;
            }
            catch
            {
                _focusSubscribed = false;
            }
        }

        _pumpTask = Task.Run(() => PumpAsync(_cts.Token));
    }

    public Task SetForegroundProcessAsync(int processId, CancellationToken cancellationToken = default)
    {
        if (_disposed || processId == _scopeProcessId) return Task.CompletedTask;
        return Task.Run(() => SetForegroundProcess(processId, cancellationToken), cancellationToken);
    }

    private void SetForegroundProcess(int processId, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (_disposed) return;

        lock (_subscriptionGate)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (processId == _scopeProcessId) return;

            RemoveStructureSubscriptionLocked();
            _scopeProcessId = processId;
            if (processId <= 0) return;

            AutomationElement? root = null;
            try
            {
                var condition = new PropertyCondition(AutomationElement.ProcessIdProperty, processId);
                var candidates = AutomationElement.RootElement.FindAll(TreeScope.Children, condition);
                foreach (AutomationElement candidate in candidates)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    try
                    {
                        var current = candidate.Current;
                        if (current.IsOffscreen) continue;
                        var bounds = current.BoundingRectangle;
                        if (bounds.IsEmpty || bounds.Width < 80 || bounds.Height < 60) continue;
                        root = candidate;
                        break;
                    }
                    catch (ElementNotAvailableException) { }
                    catch (InvalidOperationException) { }
                }
            }
            catch (ElementNotAvailableException) { }
            catch (InvalidOperationException) { }

            if (root is null) return;

            try
            {
                Automation.AddStructureChangedEventHandler(root, TreeScope.Subtree, _structureHandler);
                _structureRoot = root;
                _structureSubscribed = true;
            }
            catch
            {
                _structureRoot = null;
                _structureSubscribed = false;
            }
        }
    }

    private void RemoveStructureSubscriptionLocked()
    {
        if (!_structureSubscribed || _structureRoot is null)
        {
            _structureRoot = null;
            _structureSubscribed = false;
            return;
        }

        try { Automation.RemoveStructureChangedEventHandler(_structureRoot, _structureHandler); } catch { }
        _structureRoot = null;
        _structureSubscribed = false;
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
        lock (_subscriptionGate)
        {
            RemoveStructureSubscriptionLocked();
            _scopeProcessId = 0;
            if (_focusSubscribed)
            {
                try { Automation.RemoveAutomationFocusChangedEventHandler(_focusHandler); } catch { }
                _focusSubscribed = false;
            }
        }

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
