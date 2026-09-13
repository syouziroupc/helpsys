using System.Windows;
using System.Windows.Automation;
using HelpSys.Models;
using HelpSys.Services;

namespace HelpSys;

public partial class MainWindow
{
    private bool _deepAuditGuardsAttached;
    private int _typeTextFocusRecoveryInFlight;

    private void AttachDeepAuditGuards()
    {
        if (_deepAuditGuardsAttached) return;
        _deepAuditGuardsAttached = true;

        // KeyReleased was originally subscribed in the constructor. Reorder it once so this
        // synchronous focus guard sees the finishing key before the normal verifier can mark a
        // type_text step as complete. The guard never consumes normal typing or other key actions.
        _actionObserver.KeyReleased -= OnObservedKeyReleasedV3;
        _actionObserver.KeyReleased += ObserveTypeTextSubmitDeepAudit;
        _actionObserver.KeyReleased += OnObservedKeyReleasedV3;
    }

    private void DetachDeepAuditGuards()
    {
        if (!_deepAuditGuardsAttached) return;
        _deepAuditGuardsAttached = false;
        _actionObserver.KeyReleased -= ObserveTypeTextSubmitDeepAudit;
        Interlocked.Exchange(ref _typeTextFocusRecoveryInFlight, 0);
    }

    private void ObserveTypeTextSubmitDeepAudit(KeyObservation observation)
    {
        if (_sessionState.State != GuidanceSessionState.AwaitingUserAction ||
            _currentDecision is null ||
            _currentTarget is null ||
            _activeRequest is null ||
            _sessionCts is null ||
            _sessionCts.IsCancellationRequested ||
            !_currentDecision.Action.Equals("type_text", StringComparison.OrdinalIgnoreCase))
            return;

        var expected = string.IsNullOrWhiteSpace(_currentDecision.Key) ? "Enter" : _currentDecision.Key;
        if (!MatchesKeySpecV3(expected, observation)) return;
        if (IsCurrentTextTargetFocusedDeepAudit(_currentTarget)) return;
        if (Interlocked.Exchange(ref _typeTextFocusRecoveryInFlight, 1) != 0) return;

        var generation = _sessionState.Generation;
        _history.Add(new GuideHistoryItem(
            _stepNumber,
            "type_target_lost_focus",
            DisplayName(_currentTarget.Name, _currentTarget.ControlType),
            "入力を確定するキーが押された時点で案内対象の入力欄にフォーカスが無かったため、このキー操作を成功扱いにせず現在状態から復帰する。"));
        if (_history.Count > 12) _history.RemoveAt(0);

        // Clear synchronously before the normal KeyReleased subscriber runs. It will then observe
        // no current decision and cannot turn this Enter key into a false successful type_text step.
        _speechOutput.Stop();
        ClearCurrentGuidanceV3();
        SetState("入力する場所が変わったため、今の画面から正しい入力欄を確認し直しています…", speak: false);
        _ = RecoverTypeTextFocusDeviationAsync(generation);
    }

    private async Task RecoverTypeTextFocusDeviationAsync(long generation)
    {
        try
        {
            if (_sessionCts is null || _sessionCts.IsCancellationRequested || !_sessionState.IsCurrent(generation)) return;
            await TryRouteRecoveryAsync("入力確定時に案内対象の入力欄からフォーカスが外れている", generation, _sessionCts.Token);
        }
        catch (OperationCanceledException) { }
        catch (ObjectDisposedException) { }
        catch
        {
            try { await RecoverFromObserverFailureAsync("入力欄の再確認で現在状態を確定できない"); }
            catch { }
        }
        finally
        {
            Interlocked.Exchange(ref _typeTextFocusRecoveryInFlight, 0);
        }
    }

    private static bool IsCurrentTextTargetFocusedDeepAudit(UiElementCandidate target)
    {
        try
        {
            var element = AutomationElement.FocusedElement;
            var walker = TreeWalker.ControlViewWalker;
            for (var depth = 0; element is not null && depth < 7; depth++)
            {
                try
                {
                    if (MatchesTextTargetDeepAudit(target, element.Current)) return true;
                    element = walker.GetParent(element);
                }
                catch (ElementNotAvailableException)
                {
                    return false;
                }
                catch (InvalidOperationException)
                {
                    return false;
                }
            }
        }
        catch (ElementNotAvailableException) { }
        catch (InvalidOperationException) { }
        return false;
    }

    private static bool MatchesTextTargetDeepAudit(UiElementCandidate target, AutomationElement.AutomationElementInformation current)
    {
        if (target.ProcessId > 0 && current.ProcessId != target.ProcessId) return false;

        var type = (current.ControlType?.ProgrammaticName ?? string.Empty).Replace("ControlType.", string.Empty);
        if (!type.Equals(target.ControlType, StringComparison.OrdinalIgnoreCase)) return false;

        if (!string.IsNullOrWhiteSpace(target.AutomationId))
            return string.Equals(current.AutomationId ?? string.Empty, target.AutomationId, StringComparison.Ordinal);

        if (!string.IsNullOrWhiteSpace(target.Name) &&
            string.Equals(current.Name ?? string.Empty, target.Name, StringComparison.OrdinalIgnoreCase))
            return true;

        if (!string.IsNullOrWhiteSpace(target.ClassName) &&
            string.Equals(current.ClassName ?? string.Empty, target.ClassName, StringComparison.Ordinal))
        {
            var rect = current.BoundingRectangle;
            if (rect.IsEmpty) return false;
            var oldCenter = new Point(target.X + target.Width / 2d, target.Y + target.Height / 2d);
            var newCenter = new Point(rect.X + rect.Width / 2d, rect.Y + rect.Height / 2d);
            return (oldCenter - newCenter).Length <= 90;
        }

        return false;
    }


}
