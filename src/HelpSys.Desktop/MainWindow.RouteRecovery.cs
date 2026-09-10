using System.Windows;
using HelpSys.Models;
using HelpSys.Services;

namespace HelpSys;

public partial class MainWindow
{
    private const int MaximumRouteRecoveryAttempts = 3;

    private async Task RecoverFromObserverFailureAsync(string routeIssue)
    {
        if (_activeRequest is null || _sessionCts is null || _sessionCts.IsCancellationRequested) return;
        var generation = _sessionState.Generation;
        if (!_sessionState.IsCurrent(generation)) return;

        ClearCurrentGuidanceV3();
        using var recoveryCts = CancellationTokenSource.CreateLinkedTokenSource(_sessionCts.Token);
        recoveryCts.CancelAfter(TimeSpan.FromSeconds(22));
        try
        {
            await TryRouteRecoveryAsync(routeIssue, generation, recoveryCts.Token);
        }
        catch (OperationCanceledException)
        {
            if (_sessionState.IsCurrent(generation))
                WaitForClarification("現在の画面の大きな見出しか、目立つボタン名を1つ教えてください。そこから案内を続けます。", generation);
        }
        catch
        {
            if (_sessionState.IsCurrent(generation))
                WaitForClarification("現在の画面の大きな見出しか、目立つボタン名を1つ教えてください。そこから案内を続けます。", generation);
        }
    }

    private async Task<bool> TryRouteRecoveryAsync(
        string routeIssue,
        long generation,
        CancellationToken cancellationToken)
    {
        if (_activeRequest is null || !_sessionState.IsCurrent(generation)) return false;

        var issue = string.IsNullOrWhiteSpace(routeIssue) ? "現在の状態が想定経路と一致しない" : routeIssue.Trim();

        for (var attempt = 1; attempt <= MaximumRouteRecoveryAttempts; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!_sessionState.IsCurrent(generation) || _activeRequest is null) return false;

            if (!EnsureRecoveryCapturingState(generation)) return false;

            _overlay.Hide();
            _keyHint.Hide();
            _currentDecision = null;
            _currentTarget = null;
            _guidedBounds = null;

            SetState(
                attempt == 1
                    ? "現在位置を取り直し、目的へ戻るための次の1手を探しています…"
                    : $"現在位置を別の情報でも照合しています… ({attempt}/{MaximumRouteRecoveryAttempts})",
                speak: false);

            if (attempt > 1) await Task.Delay(160 * attempt, cancellationToken);

            var context = _systemContext.Capture();
            if (!HasUsableForeground(context))
            {
                await Task.Delay(260, cancellationToken);
                context = _systemContext.Capture();
            }
            if (!HasUsableForeground(context)) continue;

            try { await _liveWatcher.SetForegroundProcessAsync(context.ForegroundProcessId, cancellationToken); }
            catch (OperationCanceledException) { throw; }
            catch { }

            IReadOnlyList<UiElementCandidate> candidates;
            try
            {
                candidates = await _scanner.CaptureCandidatesForProcessAsync(context.ForegroundProcessId, 420, cancellationToken);
            }
            catch (OperationCanceledException) { throw; }
            catch
            {
                continue;
            }

            var privacy = _cloudGuide.PreflightPrivacy(context, candidates);
            if (!privacy.CanSend) return false;

            ScreenCaptureFrame frame;
            try
            {
                frame = await CaptureQualityFrameAsync(candidates, cancellationToken);
            }
            catch (OperationCanceledException) { throw; }
            catch
            {
                if (!_sessionState.TryTransition(generation, GuidanceSessionState.Planning)) continue;
                if (await TryStructuredFallbackAsync(candidates, context, generation, cancellationToken)) return true;
                continue;
            }

            if (!_sessionState.IsCurrent(generation)) return false;
            if (HasSystemTransitionV3(context, _systemContext.Capture())) continue;
            if (!_sessionState.TryTransition(generation, GuidanceSessionState.Planning)) continue;

            var evidence = GuidanceEvidenceService.Build(true, candidates, _history, context);
            SetState($"{GuidanceEvidenceService.BuildProgressText(evidence)} 復帰経路を選定中…", speak: false);

            QualityGuideDecision recovery;
            try
            {
                recovery = await _cloudGuide.PlanRecoveryAsync(
                    _activeRequest,
                    issue,
                    frame,
                    candidates,
                    _history,
                    context,
                    cancellationToken);
            }
            catch (OperationCanceledException) { throw; }
            catch (GuideServiceException error) when (error.Kind == GuideFailureKind.ContextChanged)
            {
                continue;
            }
            catch
            {
                if (await TryStructuredFallbackAsync(candidates, context, generation, cancellationToken)) return true;
                continue;
            }

            if (!_sessionState.IsCurrent(generation)) return false;
            if (HasSystemTransitionV3(context, _systemContext.Capture())) continue;

            if (recovery.Status.Equals("done", StringComparison.OrdinalIgnoreCase) &&
                recovery.ScreenConfirmed &&
                recovery.Confidence >= MinimumQualityDoneConfidence &&
                !string.IsNullOrWhiteSpace(recovery.VisualEvidence))
            {
                StopWithMessage(string.IsNullOrWhiteSpace(recovery.Instruction)
                    ? "目的の状態になったことを確認しました。"
                    : recovery.Instruction);
                return true;
            }

            if (recovery.Status.Equals("clarify", StringComparison.OrdinalIgnoreCase) &&
                !string.IsNullOrWhiteSpace(recovery.Question))
            {
                WaitForClarification(recovery.Question, generation);
                return true;
            }

            if (!recovery.Status.Equals("target", StringComparison.OrdinalIgnoreCase) ||
                recovery.Confidence < MinimumQualityTargetConfidence)
            {
                RememberRecoveryAttempt(issue, attempt, recovery.Instruction);
                continue;
            }

            var decision = new GuideDecision(
                "target",
                recovery.TargetId,
                recovery.Action,
                recovery.Instruction,
                recovery.Question,
                recovery.Key,
                recovery.Confidence);

            if (decision.Action.Equals("press_key", StringComparison.OrdinalIgnoreCase) &&
                string.IsNullOrWhiteSpace(decision.TargetId))
            {
                if (!recovery.ScreenConfirmed) continue;
                ShowKeyboardGuide(decision, candidates, context, generation);
                return true;
            }

            if (string.Equals(decision.TargetId, "vision-target", StringComparison.Ordinal))
            {
                if (!recovery.ScreenConfirmed || recovery.Confidence < MinimumVisualOnlyTargetConfidence) continue;
                if (await TryPresentRecoveryVisionTargetAsync(recovery, decision, frame, candidates, context, generation, cancellationToken))
                    return true;
                continue;
            }

            var requiredConfidence = recovery.ScreenConfirmed
                ? MinimumQualityTargetConfidence
                : MinimumStructuredFallbackConfidence;
            if (recovery.Confidence < requiredConfidence || string.IsNullOrWhiteSpace(decision.TargetId)) continue;

            var target = candidates.FirstOrDefault(x => string.Equals(x.Id, decision.TargetId, StringComparison.Ordinal));
            if (target is null || !target.Interactable || !target.Enabled || target.Bounds.IsEmpty) continue;

            var freshTarget = await _scanner.RevalidateCandidateAsync(target, context.ForegroundProcessId, cancellationToken);
            if (!_sessionState.IsCurrent(generation) || freshTarget is null) continue;
            if (HasSystemTransitionV3(context, _systemContext.Capture())) continue;

            ShowStructuredTarget(decision, freshTarget, candidates, context, generation);
            return true;
        }

        if (!_sessionState.IsCurrent(generation)) return false;
        RememberRecoveryAttempt(issue, MaximumRouteRecoveryAttempts, "自動復帰経路を確定できなかったため、現在位置の追加情報を求める。");
        WaitForClarification(
            "現在位置を特定するため、今いちばん手前に出ている画面の大きな見出しか、目立つボタン名を1つだけ教えてください。そこから目的へ戻る次の操作を続けます。",
            generation);
        return true;
    }

    private bool EnsureRecoveryCapturingState(long generation)
    {
        if (!_sessionState.IsCurrent(generation)) return false;
        if (_sessionState.State == GuidanceSessionState.Capturing) return true;
        return _sessionState.TryTransition(generation, GuidanceSessionState.Capturing);
    }

    private void RememberRecoveryAttempt(string issue, int attempt, string? result)
    {
        _history.Add(new GuideHistoryItem(
            _stepNumber,
            "route_recovery",
            issue,
            string.IsNullOrWhiteSpace(result)
                ? $"復帰経路の探索 {attempt} 回目。現在状態を再取得して別経路を検討した。"
                : $"復帰経路の探索 {attempt} 回目: {result}"));
        if (_history.Count > 12) _history.RemoveAt(0);
    }

    private async Task<bool> TryPresentRecoveryVisionTargetAsync(
        QualityGuideDecision recovery,
        GuideDecision decision,
        ScreenCaptureFrame frame,
        IReadOnlyList<UiElementCandidate> candidates,
        SystemContextSnapshot context,
        long generation,
        CancellationToken cancellationToken)
    {
        var bounds = frame.MapNormalizedBounds(recovery.X, recovery.Y, recovery.Width, recovery.Height);
        if (bounds.IsEmpty || bounds.Width < 8 || bounds.Height < 8) return false;

        Rect? snapped = null;
        try { snapped = await _scanner.SnapToAccessibleBoundsAsync(bounds, cancellationToken); }
        catch (OperationCanceledException) { throw; }
        catch { }

        if (!_sessionState.IsCurrent(generation) || HasSystemTransitionV3(context, _systemContext.Capture())) return false;
        if (snapped is { } accessible && !accessible.IsEmpty) bounds = accessible;
        else if (recovery.Confidence < MinimumVisualOnlyTargetConfidence) return false;

        var instruction = string.IsNullOrWhiteSpace(decision.Instruction)
            ? "青い枠で囲まれた場所で、マウスの左ボタンを1回押してください。"
            : decision.Instruction;

        if (!_sessionState.TryTransition(generation, GuidanceSessionState.Presenting)) return false;
        _currentDecision = new GuideDecision("target", "vision-target", decision.Action, instruction, null, null, recovery.Confidence);
        _currentTarget = null;
        _stepBaseline = candidates;
        _stepSystemBaseline = context;
        _guidedBounds = bounds;
        _validatedVisionInstruction = instruction;
        _overlay.ShowTarget(bounds, instruction);
        if (!_sessionState.TryTransition(generation, GuidanceSessionState.AwaitingUserAction))
        {
            _overlay.Hide();
            return false;
        }
        ShowInstruction(instruction);
        return true;
    }
}
