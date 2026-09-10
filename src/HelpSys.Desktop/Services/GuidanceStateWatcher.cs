using System.Diagnostics;
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
    private readonly AutomationPropertyChangedEventHandler _propertyHandler;
    private CancellationTokenSource? _cts;
    private Task? _pumpTask;
    private AutomationElement? _structureRoot;
    private int _scopeProcessId;
    private int _queued;
    private long _lastSignalTimestamp;
    private long _scopeRequestVersion;
    private bool _focusSubscribed;
    private bool _structureSubscribed;
    private bool _propertySubscribed;
    private bool _disposed;

    public event EventHandler? Pulse;

    public GuidanceStateWatcher()
    {
        _focusHandler = (_, _) => Signal();
        _structureHandler = (_, _) => Signal();
        _propertyHandler = (_, _) => Signal();
    }

    public void Start()
    {
        if (_disposed) throw new ObjectDisposedException(nameof(GuidanceStateWatcher));
        if (_cts is not null) return;

        _cts = new CancellationTokenSource();
        Interlocked.Exchange(ref _queued, 0);
        Interlocked.Exchange(ref _lastSignalTimestamp, 0);

        lock (_subscriptionGate)
        {
            TryEnsureFocusSubscriptionLocked();
        }

        _pumpTask = Task.Run(() => PumpAsync(_cts.Token));
    }

    public Task SetForegroundProcessAsync(int processId, CancellationToken cancellationToken = default)
    {
        if (_disposed) return Task.CompletedTask;

        lock (_subscriptionGate)
        {
            if (_disposed) return Task.CompletedTask;
            TryEnsureFocusSubscriptionLocked();
            if (processId == _scopeProcessId &&
                (processId <= 0 || (_structureSubscribed && _propertySubscribed)))
                return Task.CompletedTask;
        }

        var requestVersion = Interlocked.Increment(ref _scopeRequestVersion);
        return Task.Run(() => SetForegroundProcess(processId, requestVersion, cancellationToken), cancellationToken);
    }

    private void TryEnsureFocusSubscriptionLocked()
    {
        if (_focusSubscribed || _disposed) return;
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

    private void SetForegroundProcess(int processId, long requestVersion, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (_disposed || requestVersion != Volatile.Read(ref _scopeRequestVersion)) return;

        lock (_subscriptionGate)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (_disposed || requestVersion != Volatile.Read(ref _scopeRequestVersion)) return;
            TryEnsureFocusSubscriptionLocked();
            if (processId == _scopeProcessId &&
                (processId <= 0 || (_structureSubscribed && _propertySubscribed))) return;

            RemoveStructureSubscriptionLocked();
            _scopeProcessId = processId;
            if (processId <= 0) return;

            AutomationElement? root = null;
            AutomationElement? fallbackRoot = null;
            try
            {
                var condition = new PropertyCondition(AutomationElement.ProcessIdProperty, processId);
                var candidates = AutomationElement.RootElement.FindAll(TreeScope.Children, condition);
                foreach (AutomationElement candidate in candidates)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (requestVersion != Volatile.Read(ref _scopeRequestVersion)) return;
                    try
                    {
                        var current = candidate.Current;
                        if (current.IsOffscreen) continue;
                        var bounds = current.BoundingRectangle;
                        if (bounds.IsEmpty || bounds.Width < 80 || bounds.Height < 60) continue;

                        fallbackRoot ??= candidate;
                        if (!current.HasKeyboardFocus) continue;
                        root = candidate;
                        break;
                    }
                    catch (ElementNotAvailableException) { }
                    catch (InvalidOperationException) { }
                }
            }
            catch (ElementNotAvailableException) { }
            catch (InvalidOperationException) { }

            if (requestVersion != Volatile.Read(ref _scopeRequestVersion)) return;
            root ??= fallbackRoot;

            if (root is null) return;

            try
            {
                Automation.AddStructureChangedEventHandler(root, TreeScope.Subtree, _structureHandler);
                if (requestVersion != Volatile.Read(ref _scopeRequestVersion))
                {
                    try { Automation.RemoveStructureChangedEventHandler(root, _structureHandler); } catch { }
                    return;
                }
                _structureRoot = root;
                _structureSubscribed = true;
            }
            catch
            {
                _structureRoot = null;
                _structureSubscribed = false;
            }

            if (_structureRoot is not null)
            {
                try
                {
                    Automation.AddAutomationPropertyChangedEventHandler(
                        _structureRoot,
                        TreeScope.Subtree,
                        _propertyHandler,
                        AutomationElement.HasKeyboardFocusProperty,
                        TogglePattern.ToggleStateProperty,
                        SelectionItemPattern.IsSelectedProperty,
                        ExpandCollapsePattern.ExpandCollapseStateProperty);
                    _propertySubscribed = true;
                }
                catch
                {
                    _propertySubscribed = false;
                }
            }
        }
    }

    private void RemoveStructureSubscriptionLocked()
    {
        if (_structureRoot is not null && _propertySubscribed)
        {
            try { Automation.RemoveAutomationPropertyChangedEventHandler(_structureRoot, _propertyHandler); } catch { }
        }
        _propertySubscribed = false;

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
        Interlocked.Exchange(ref _lastSignalTimestamp, Stopwatch.GetTimestamp());
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
            var timestamp = Volatile.Read(ref _lastSignalTimestamp);
            if (timestamp <= 0) return;

            var quietFor = Stopwatch.GetElapsedTime(timestamp);
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
        Interlocked.Increment(ref _scopeRequestVersion);
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

        Interlocked.Exchange(ref _queued, 0);
        Interlocked.Exchange(ref _lastSignalTimestamp, 0);

        _ = DrainStoppedPumpAsync(pump, cts);
    }

    private static async Task DrainStoppedPumpAsync(Task? pump, CancellationTokenSource cts)
    {
        try
        {
            if (pump is not null) await pump.ConfigureAwait(false);
        }
        catch (OperationCanceledException) { }
        catch (ObjectDisposedException) { }
        catch { }
        finally
        {
            try { cts.Dispose(); } catch { }
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        // Mark disposed first so a concurrent SetForegroundProcessAsync cannot install a new
        // subscription between Stop() removing the old handlers and the final dispose.
        _disposed = true;
        Stop();
        _signal.Dispose();
    }
}
