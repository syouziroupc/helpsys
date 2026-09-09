using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Automation;
using HelpSys.Models;
using HelpSys.Services;

namespace HelpSys;

public partial class MainWindow
{
    private bool _deepAuditGuardsAttached;
    private int _offRouteRecoveryInFlight;
    private int _typeTextFocusRecoveryInFlight;

    private void AttachDeepAuditGuards()
    {
        if (_deepAuditGuardsAttached) return;
        _deepAuditGuardsAttached = true;
        _actionObserver.LeftClick += ObserveOffRouteClickDeepAudit;

        _actionObserver.KeyReleased -= OnObservedKeyReleasedV3;
        _actionObserver.KeyReleased += ObserveTypeTextSubmitDeepAudit;
        _actionObserver.KeyReleased += OnObservedKeyReleasedV3;
    }

    private void DetachDeepAuditGuards()
    {
        if (!_deepAuditGuardsAttached) return;
        _deepAuditGuardsAttached = false;
        _actionObserver.LeftClick -= ObserveOffRouteClickDeepAudit;
        _actionObserver.KeyReleased -= ObserveTypeTextSubmitDeepAudit;
        Interlocked.Exchange(ref _offRouteRecoveryInFlight, 0);
        Interlocked.Exchange(ref _typeTextFocusRecoveryInFlight, 0);
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

        var expectedKey = string.IsNullOrWhiteSpace(_currentDecision.Key) ? "Enter" : _currentDecision.Key;
        if (!MatchesKeySpecV3(expectedKey, observation)) return;
        if (Interlocked.Exchange(ref _typeTextFocusRecoveryInFlight, 1) != 0) return;

        var generation = _sessionState.Generation;
        var decision = _currentDecision;
        var target = _currentTarget;
        _currentDecision = null;
        _ = ValidateTypeTextSubmissionDeepAuditAsync(decision, target, generation);
    }

    private async Task ValidateTypeTextSubmissionDeepAuditAsync(
        GuideDecision decision,
        UiElementCandidate target,
        long generation)
    {
        try
        {
            if (_sessionCts is null || _sessionCts.IsCancellationRequested || !_sessionState.IsCurrent(generation)) return;

            if (!IsCurrentTextTargetFocusedDeepAudit(target))
            {
                RecordTypeTextDeviation("type_target_lost_focus", target,
                    "入力を確定するキーが押された時点で案内対象の入力欄にフォーカスが無かったため、このキー操作を成功扱いにせず現在状態から復帰する。");
                ClearCurrentGuidanceV3();
                SetState("入力する場所が変わったため、今の画面から正しい入力欄を確認し直しています…", speak: false);
                await TryRouteRecoveryAsync("入力確定時に案内対象の入力欄からフォーカスが外れている", generation, _sessionCts.Token);
                return;
            }

            var rootProcessId = _stepSystemBaseline?.ForegroundProcessId ?? target.ProcessId;
            var fresh = rootProcessId > 0
                ? await _scanner.RevalidateCandidateAsync(target, rootProcessId, _sessionCts.Token)
                : await _scanner.RevalidateCandidateAsync(target, _sessionCts.Token);
            if (!_sessionState.IsCurrent(generation) || _sessionCts.IsCancellationRequested) return;

            if (fresh is null || !fresh.Focused)
            {
                RecordTypeTextDeviation("type_target_not_revalidated", target,
                    "入力確定直前の再検証で案内対象の入力欄を確認できなかったため、成功扱いにしない。");
                ClearCurrentGuidanceV3();
                SetState("入力欄を確認し直しています…", speak: false);
                await TryRouteRecoveryAsync("入力確定直前に案内対象の入力欄を再確認できない", generation, _sessionCts.Token);
                return;
            }

            if (target.Password || fresh.Password)
            {
                RecordTypeTextDeviation("secret_input_target", fresh,
                    "秘密入力欄の内容はHelpSysが取得・照合しないため、具体的なtype_text操作を成功扱いにしない。");
                ClearCurrentGuidanceV3();
                SetState("パスワードなどの秘密入力欄は内容を確認しません。秘密情報をHelpSysへ渡さずに続けられる次の操作を確認します…", speak: false);
                await TryRouteRecoveryAsync("秘密入力欄へのtype_text案内を破棄して安全な経路を選び直す", generation, _sessionCts.Token);
                return;
            }

            var expectedText = NormalizeExpectedInputTextDeepAudit(decision.InputText)
                               ?? NormalizeExpectedInputTextDeepAudit(_cloudGuide.ResolveExpectedInputText(decision))
                               ?? ExtractExpectedInputTextDeepAudit(decision.Instruction);
            if (string.IsNullOrWhiteSpace(expectedText) || fresh.Value is null)
            {
                RecordTypeTextDeviation("type_text_unverifiable", fresh,
                    "入力内容そのものをローカルで照合できなかったため、Enter操作を成功扱いにせず現在状態から再計画する。");
                ClearCurrentGuidanceV3();
                SetState("入力内容を安全に確認できないため、現在の画面から次の操作を確認し直しています…", speak: false);
                await TryRouteRecoveryAsync("入力内容をローカルで照合できない", generation, _sessionCts.Token);
                return;
            }

            if (!InputTextMatchesDeepAudit(fresh.Value, expectedText))
            {
                _currentDecision = decision;
                _currentTarget = fresh;
                _guidedBounds = fresh.Bounds;
                _v3TrackedDecision = null;
                _v3TypeActivityObserved = false;
                _overlay.ShowTarget(fresh.Bounds, decision.Instruction);
                SetState($"入力内容が案内と一致していません。青い枠の欄を「{expectedText}」に直してから、もう一度「Enter」と書かれたキーを1回押してください。", speak: true);
                return;
            }

            _currentDecision = decision;
            _currentTarget = fresh;
            _guidedBounds = fresh.Bounds;
            _speechOutput.Stop();
            await CompleteCurrentStepV3Async();
        }
        catch (OperationCanceledException) { }
        catch (ObjectDisposedException) { }
        catch
        {
            try
            {
                ClearCurrentGuidanceV3();
                await RecoverFromObserverFailureAsync("入力内容の確定検証で現在状態を確認できない");
            }
            catch { }
        }
        finally
        {
            Interlocked.Exchange(ref _typeTextFocusRecoveryInFlight, 0);
        }
    }

    private void RecordTypeTextDeviation(string action, UiElementCandidate target, string instruction)
    {
        _history.Add(new GuideHistoryItem(
            _stepNumber,
            action,
            DisplayName(target.Name, target.ControlType),
            instruction));
        if (_history.Count > 12) _history.RemoveAt(0);
    }

    private static string? NormalizeExpectedInputTextDeepAudit(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        var text = value.Trim();
        return text.Length <= 160 ? text : null;
    }

    private static string? ExtractExpectedInputTextDeepAudit(string? instruction)
    {
        if (string.IsNullOrWhiteSpace(instruction)) return null;
        var match = Regex.Match(
            instruction,
            "[「『](?<text>[^」』\\r\\n]{1,160})[」』].{0,40}(?:と)?入力",
            RegexOptions.CultureInvariant);
        if (!match.Success) return null;
        return NormalizeExpectedInputTextDeepAudit(match.Groups["text"].Value);
    }

    private static bool InputTextMatchesDeepAudit(string actual, string expected)
    {
        static string Normalize(string value) => value.Trim().Normalize().Replace('　', ' ');
        return string.Equals(Normalize(actual), Normalize(expected), StringComparison.OrdinalIgnoreCase);
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
