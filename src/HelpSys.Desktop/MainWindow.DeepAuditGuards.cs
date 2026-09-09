using System.Windows;
using HelpSys.Models;
using HelpSys.Services;

namespace HelpSys;

public partial class MainWindow
{
    private bool _deepAuditGuardsAttached;
    private int _offRouteRecoveryInFlight;

    private void AttachDeepAuditGuards()
    {
        if (_deepAuditGuardsAttached) return;
        _deepAuditGuardsAttached = true;
        _actionObserver.LeftClick += ObserveOffRouteClickDeepAudit;
    }

    private void DetachDeepAuditGuards()
    {
        if (!_deepAuditGuardsAttached) return;
        _deepAuditGuardsAttached = false;
        _actionObserver.LeftClick -= ObserveOffRouteClickDeepAudit;
        Interlocked.Exchange(ref _offRouteRecoveryInFlight, 0);
    }

    private async void ObserveOffRouteClickDeepAudit(Point point)
    {
        if (_sessionState.State != GuidanceSessionState.AwaitingUserAction ||
            _currentDecision is null ||
            _guidedBounds is null ||
            _activeRequest is null ||
            _sessionCts is null ||
            _sessionCts.IsCancellationRequested)
            return;

        var action = _currentDecision.Action;
        if (!action.Equals("left_click", StringComparison.OrdinalIgnoreCase) &&
            !action.Equals("double_click", StringComparison.OrdinalIgnoreCase))
            return;

        var expectedBounds = _guidedBounds.Value;
        expectedBounds.Inflate(10, 10);
        if (expectedBounds.Contains(point) || IsPointInsideHelpSysWindow(point)) return;
        if (Interlocked.Exchange(ref _offRouteRecoveryInFlight, 1) != 0) return;

        try
        {
            var generation = _sessionState.Generation;
            if (!_sessionState.IsCurrent(generation)) return;

            _history.Add(new GuideHistoryItem(
                _stepNumber,
                "off_route_click",
                "案内枠以外の場所",
                "案内していた青い枠とは別の場所が操作されたため、現在状態を取り直して目的への復帰経路を選ぶ。"));
            if (_history.Count > 12) _history.RemoveAt(0);

            _speechOutput.Stop();
            ClearCurrentGuidanceV3();
            SetState("案内とは別の場所が操作されたため、現在の画面から目的への戻り方を確認しています…", speak: false);
            await TryRouteRecoveryAsync("案内枠以外の場所が操作された", generation, _sessionCts.Token);
        }
        catch (OperationCanceledException) { }
        catch (ObjectDisposedException) { }
        catch
        {
            try { await RecoverFromObserverFailureAsync("案内外の操作後に現在状態を確定できない"); }
            catch { }
        }
        finally
        {
            Interlocked.Exchange(ref _offRouteRecoveryInFlight, 0);
        }
    }

    private bool IsPointInsideHelpSysWindow(Point screenPoint)
    {
        if (!IsVisible || ActualWidth <= 0 || ActualHeight <= 0) return false;
        try
        {
            var topLeft = PointToScreen(new Point(0, 0));
            var bottomRight = PointToScreen(new Point(ActualWidth, ActualHeight));
            var rect = new Rect(topLeft, bottomRight);
            return rect.Contains(screenPoint);
        }
        catch
        {
            return false;
        }
    }
}
