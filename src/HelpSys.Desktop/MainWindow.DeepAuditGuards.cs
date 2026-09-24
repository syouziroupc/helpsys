using System.Windows;
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

        // In Outlaw, the finishing key often starts navigation immediately. By the time an
        // asynchronous UIA revalidation returns, the address/search field can legitimately lose
        // focus. Let the normal post-action transition verifier decide success instead of
        // rejecting that expected navigation as a focus error.
        if (OutlawModePolicy.Enabled)
        {
            LocalLogService.Write("outlaw_type_submit_focus_guard_skipped", $"target={_currentTarget.Id};key={expected}");
            return;
        }

        if (Interlocked.Exchange(ref _typeTextFocusRecoveryInFlight, 1) != 0) return;

        // Do not query AutomationElement from the desktop process. The isolated observer performs
        // the focus revalidation; the normal KeyReleased verifier is suppressed until it returns.
        var generation = _sessionState.Generation;
        var target = _currentTarget;
        _ = ValidateTypeTextSubmitFocusAsync(target, generation);
    }

    private async Task ValidateTypeTextSubmitFocusAsync(UiElementCandidate target, long generation)
    {
        try
        {
            if (_sessionCts is null || _sessionCts.IsCancellationRequested || !_sessionState.IsCurrent(generation))
                return;

            UiElementCandidate? fresh;
            try
            {
                fresh = await _scanner.RevalidateCandidateAsync(
                    target,
                    target.ProcessId,
                    _sessionCts.Token);
            }
            catch (OperationCanceledException) { return; }
            catch
            {
                HandleTechnicalPlanningUncertainty("入力確定時のフォーカス再確認に失敗", generation);
                return;
            }

            if (!_sessionState.IsCurrent(generation) || _sessionCts is null || _sessionCts.IsCancellationRequested)
                return;

            if (fresh is not null && fresh.Focused)
            {
                _currentTarget = fresh;
                _guidedBounds = fresh.Bounds;
                Interlocked.Exchange(ref _typeTextFocusRecoveryInFlight, 0);
                await CompleteCurrentStepV3Async();
                return;
            }

            _history.Add(new GuideHistoryItem(
                _stepNumber,
                "type_target_lost_focus",
                DisplayName(target.Name, target.ControlType),
                "入力を確定するキーが押された時点で案内対象の入力欄にフォーカスが無かったため、このキー操作を成功扱いにせず現在状態から復帰する。"));
            if (_history.Count > 12) _history.RemoveAt(0);

            _speechOutput.Stop();
            ClearCurrentGuidanceV3();
            SetState("入力する場所が変わったため、今の画面から正しい入力欄を確認し直しています…", speak: false);
            HandleTechnicalPlanningUncertainty("入力確定時に案内対象の入力欄からフォーカスが外れている", generation);
        }
        catch (ObjectDisposedException) { }
        catch
        {
            try { HandleTechnicalPlanningUncertainty("入力欄の再確認で現在状態を確定できない", generation); }
            catch { }
        }
        finally
        {
            Interlocked.Exchange(ref _typeTextFocusRecoveryInFlight, 0);
        }
    }

}
