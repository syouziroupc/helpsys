using System.Windows;
using HelpSys.Models;
using HelpSys.Services;

namespace HelpSys;

public partial class MainWindow
{
    private const double MinimumQualityTargetConfidence = 0.80;
    private const double MinimumQualityDoneConfidence = 0.90;
    private const double MinimumVisualOnlyTargetConfidence = 0.92;
    private const double MinimumStructuredFallbackConfidence = 0.88;

    private async Task AdvanceGuideAsync()
    {
        if (_activeRequest is null || _sessionCts is null || _sessionCts.IsCancellationRequested) return;
        if (!_sessionState.TryBeginOperation(out var generation, GuidanceSessionState.Capturing)) return;

        _currentDecision = null;
        _currentTarget = null;
        _stepBaseline = [];
        _stepSystemBaseline = null;
        _guidedBounds = null;
        _doubleClickCount = 0;
        _forceVisionNext = false;
        _overlay.Hide();
        _keyHint.Hide();
        GuideButton.IsEnabled = false;

        using var planningCts = CancellationTokenSource.CreateLinkedTokenSource(_sessionCts.Token);
        planningCts.CancelAfter(TimeSpan.FromSeconds(70));
        var cancellationToken = planningCts.Token;

        try
        {
            SetState("今の画面と操作できる場所を確認しています…", speak: false);
            var systemContext = _systemContext.Capture();
            if (!HasUsableForeground(systemContext))
            {
                await Task.Delay(220, cancellationToken);
                if (!_sessionState.IsCurrent(generation)) return;
                systemContext = _systemContext.Capture();
            }

            if (!HasUsableForeground(systemContext))
            {
                await Task.Delay(480, cancellationToken);
                if (!_sessionState.IsCurrent(generation)) return;
                systemContext = _systemContext.Capture();
            }

            if (!HasUsableForeground(systemContext))
            {
                await TryRouteRecoveryAsync("前面ウィンドウを一時的に特定できない", generation, cancellationToken);
                return;
            }

            await _liveWatcher.SetForegroundProcessAsync(systemContext.ForegroundProcessId, cancellationToken);
            var candidates = await _scanner.CaptureCandidatesForProcessAsync(systemContext.ForegroundProcessId, 420, cancellationToken);
            if (!_sessionState.IsCurrent(generation)) return;

            var structuralEvidence = GuidanceEvidenceService.Build(false, candidates, _history, systemContext);
            SetState(GuidanceEvidenceService.BuildProgressText(structuralEvidence), speak: false);

            ScreenCaptureFrame frame;
            try
            {
                frame = await CaptureQualityFrameAsync(candidates, cancellationToken);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch
            {
                if (!_sessionState.TryTransition(generation, GuidanceSessionState.Planning)) return;
                if (await TryStructuredFallbackAsync(candidates, systemContext, generation, cancellationToken)) return;
                await TryRouteRecoveryAsync("画面画像を取得できないため他の情報源から現在位置を復元する", generation, cancellationToken);
                return;
            }

            if (!_sessionState.IsCurrent(generation)) return;

            var afterCaptureContext = _systemContext.Capture();
            if (HasSystemTransitionV3(systemContext, afterCaptureContext))
            {
                await TryRouteRecoveryAsync("確認中に画面が切り替わった", generation, cancellationToken);
                return;
            }

            if (!_sessionState.TryTransition(generation, GuidanceSessionState.Planning)) return;
            var fusedEvidence = GuidanceEvidenceService.Build(true, candidates, _history, systemContext);
            SetState(GuidanceEvidenceService.BuildProgressText(fusedEvidence), speak: false);

            QualityGuideDecision quality;
            try
            {
                quality = await _cloudGuide.PlanQualityAsync(
                    _activeRequest,
                    frame,
                    candidates,
                    _history,
                    systemContext,
                    cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (GuideServiceException error)
            {
                if (_sessionState.IsCurrent(generation) && await TryStructuredFallbackAsync(candidates, systemContext, generation, cancellationToken)) return;
                if (_sessionState.IsCurrent(generation))
                    await TryRouteRecoveryAsync($"通常計画を継続できない: {error.Kind}", generation, cancellationToken);
                return;
            }

            if (!_sessionState.IsCurrent(generation)) return;
            if (HasSystemTransitionV3(systemContext, _systemContext.Capture()))
            {
                await TryRouteRecoveryAsync("判断中に画面が変化した", generation, cancellationToken);
                return;
            }

            if (quality.Status.Equals("done", StringComparison.OrdinalIgnoreCase))
            {
                if (!quality.ScreenConfirmed || quality.Confidence < MinimumQualityDoneConfidence || string.IsNullOrWhiteSpace(quality.VisualEvidence))
                {
                    await TryRouteRecoveryAsync("完了を現在状態から確認できない", generation, cancellationToken);
                    return;
                }

                StopWithMessage(string.IsNullOrWhiteSpace(quality.Instruction)
                    ? "画面上で目的の状態になったことを確認しました。"
                    : quality.Instruction);
                return;
            }

            if (quality.Status.Equals("clarify", StringComparison.OrdinalIgnoreCase))
            {
                WaitForClarification(quality.Question ?? "画面上に複数の選択肢があります。どれを使うか教えてください。", generation);
                return;
            }

            var structuredFusionTarget =
                quality.Status.Equals("target", StringComparison.OrdinalIgnoreCase) &&
                !quality.ScreenConfirmed &&
                quality.Confidence >= MinimumStructuredFallbackConfidence &&
                !string.IsNullOrWhiteSpace(quality.TargetId) &&
                !string.Equals(quality.TargetId, "vision-target", StringComparison.Ordinal);

            if (!quality.Status.Equals("target", StringComparison.OrdinalIgnoreCase) ||
                quality.Confidence < MinimumQualityTargetConfidence ||
                (!quality.ScreenConfirmed && !structuredFusionTarget))
            {
                if (await TryStructuredFallbackAsync(candidates, systemContext, generation, cancellationToken)) return;
                await TryRouteRecoveryAsync(
                    string.IsNullOrWhiteSpace(quality.Instruction)
                        ? "通常ルート上の次操作を確定できない"
                        : quality.Instruction,
                    generation,
                    cancellationToken);
                return;
            }

            var decision = new GuideDecision(
                "target",
                quality.TargetId,
                quality.Action,
                quality.Instruction,
                quality.Question,
                quality.Key,
                quality.Confidence);

            if (decision.Action.Equals("press_key", StringComparison.OrdinalIgnoreCase) && string.IsNullOrWhiteSpace(decision.TargetId))
            {
                if (!quality.ScreenConfirmed)
                {
                    await TryRouteRecoveryAsync("対象なしのキー操作を画面情報で確認できない", generation, cancellationToken);
                    return;
                }
                ShowKeyboardGuide(decision, candidates, systemContext, generation);
                return;
            }

            if (string.Equals(decision.TargetId, "vision-target", StringComparison.Ordinal))
            {
                await ShowQualityVisualTargetAsync(quality, decision, frame, candidates, systemContext, generation, cancellationToken);
                return;
            }

            if (string.IsNullOrWhiteSpace(decision.TargetId))
            {
                if (await TryStructuredFallbackAsync(candidates, systemContext, generation, cancellationToken)) return;
                await TryRouteRecoveryAsync("操作内容は候補になったが対象を特定できない", generation, cancellationToken);
                return;
            }

            var target = candidates.FirstOrDefault(x => string.Equals(x.Id, decision.TargetId, StringComparison.Ordinal));
            if (target is null || !target.Interactable || !target.Enabled || target.Bounds.IsEmpty)
            {
                if (await TryStructuredFallbackAsync(candidates, systemContext, generation, cancellationToken)) return;
                await TryRouteRecoveryAsync("選ばれた対象が現在は操作できない", generation, cancellationToken);
                return;
            }

            var freshTarget = await _scanner.RevalidateCandidateAsync(target, systemContext.ForegroundProcessId, cancellationToken);
            if (!_sessionState.IsCurrent(generation)) return;
            if (HasSystemTransitionV3(systemContext, _systemContext.Capture()))
            {
                await TryRouteRecoveryAsync("案内表示の直前に画面が変わった", generation, cancellationToken);
                return;
            }
            if (freshTarget is null)
            {
                if (await TryStructuredFallbackAsync(candidates, systemContext, generation, cancellationToken)) return;
                await TryRouteRecoveryAsync("案内対象が表示直前に消えた", generation, cancellationToken);
                return;
            }

            ShowStructuredTarget(decision, freshTarget, candidates, systemContext, generation);
        }
        catch (OperationCanceledException)
        {
            if (_sessionState.IsCurrent(generation) && _sessionCts is { IsCancellationRequested: false })
            {
                using var recoveryCts = CancellationTokenSource.CreateLinkedTokenSource(_sessionCts.Token);
                recoveryCts.CancelAfter(TimeSpan.FromSeconds(22));
                try { await TryRouteRecoveryAsync("通常の画面確認が時間内に完了しなかった", generation, recoveryCts.Token); }
                catch (OperationCanceledException)
                {
                    if (_sessionState.IsCurrent(generation))
                        WaitForClarification("現在位置を特定するため、今いちばん手前に見えている画面の大きな見出しを1つ教えてください。そこから案内を続けます。", generation);
                }
            }
        }
        catch (InvalidOperationException ex)
        {
            if (_sessionState.IsCurrent(generation) && _sessionCts is { IsCancellationRequested: false })
            {
                using var recoveryCts = CancellationTokenSource.CreateLinkedTokenSource(_sessionCts.Token);
                recoveryCts.CancelAfter(TimeSpan.FromSeconds(22));
                try { await TryRouteRecoveryAsync($"操作対象の構造確認に失敗: {ex.GetType().Name}", generation, recoveryCts.Token); }
                catch { WaitForClarification("現在の画面の大きな見出しか、目立つボタン名を1つ教えてください。そこから案内を続けます。", generation); }
            }
        }
        catch (Exception ex)
        {
            if (_sessionState.IsCurrent(generation) && _sessionCts is { IsCancellationRequested: false })
            {
                using var recoveryCts = CancellationTokenSource.CreateLinkedTokenSource(_sessionCts.Token);
                recoveryCts.CancelAfter(TimeSpan.FromSeconds(22));
                try { await TryRouteRecoveryAsync($"案内処理を現在状態から再構成: {ex.GetType().Name}", generation, recoveryCts.Token); }
                catch { WaitForClarification("現在の画面の大きな見出しか、目立つボタン名を1つ教えてください。そこから案内を続けます。", generation); }
            }
        }
        finally
        {
            _sessionState.EndOperation(generation);
            GuideButton.IsEnabled = !_planning;
        }
    }

    private async Task<bool> TryStructuredFallbackAsync(
        IReadOnlyList<UiElementCandidate> previousCandidates,
        SystemContextSnapshot expectedContext,
        long generation,
        CancellationToken cancellationToken)
    {
        if (_activeRequest is null || previousCandidates.Count == 0 || !_sessionState.IsCurrent(generation)) return false;
        if (HasSystemTransitionV3(expectedContext, _systemContext.Capture())) return false;

        SetState("画像だけでは確定できないため、Windowsの構造情報から次の操作を再確認しています…", speak: false);

        IReadOnlyList<UiElementCandidate> candidates;
        try
        {
            candidates = await _scanner.CaptureCandidatesForProcessAsync(expectedContext.ForegroundProcessId, 420, cancellationToken);
        }
        catch (OperationCanceledException) { throw; }
        catch { return false; }

        if (!_sessionState.IsCurrent(generation) || candidates.Count == 0) return false;
        if (HasSystemTransitionV3(expectedContext, _systemContext.Capture())) return false;

        GuideDecision fallback;
        try
        {
            fallback = await _cloudGuide.PlanAsync(_activeRequest, candidates, _history, expectedContext, cancellationToken);
        }
        catch (OperationCanceledException) { throw; }
        catch { return false; }

        if (!_sessionState.IsCurrent(generation) || HasSystemTransitionV3(expectedContext, _systemContext.Capture())) return false;

        if (fallback.Status.Equals("clarify", StringComparison.OrdinalIgnoreCase) && !string.IsNullOrWhiteSpace(fallback.Question))
        {
            WaitForClarification(fallback.Question, generation);
            return true;
        }

        if (!fallback.Status.Equals("target", StringComparison.OrdinalIgnoreCase) ||
            fallback.Confidence < MinimumStructuredFallbackConfidence ||
            string.IsNullOrWhiteSpace(fallback.TargetId))
            return false;

        var target = candidates.FirstOrDefault(x => string.Equals(x.Id, fallback.TargetId, StringComparison.Ordinal));
        if (target is null || !target.Interactable || !target.Enabled || target.Bounds.IsEmpty) return false;

        var freshTarget = await _scanner.RevalidateCandidateAsync(target, expectedContext.ForegroundProcessId, cancellationToken);
        if (!_sessionState.IsCurrent(generation) || freshTarget is null) return false;
        if (HasSystemTransitionV3(expectedContext, _systemContext.Capture())) return false;

        ShowStructuredTarget(fallback, freshTarget, candidates, expectedContext, generation);
        return true;
    }

    private async Task<ScreenCaptureFrame> CaptureQualityFrameAsync(
        IReadOnlyList<UiElementCandidate> candidates,
        CancellationToken cancellationToken)
    {
        var privacyContext = _systemContext.Capture();
        var privacy = _cloudGuide.PreflightPrivacy(privacyContext, candidates);
        if (!privacy.CanSend)
            throw new OperationCanceledException("Privacy Gate blocked screenshot creation.", cancellationToken);

        var passwordBounds = candidates.Where(x => x.Password).Select(x => x.Bounds).ToArray();
        _speechInput.HideOverlay();
        _overlay.Hide();
        _keyHint.Hide();
        var previousOpacity = Opacity;
        try
        {
            Opacity = 0;
            await Task.Delay(130, cancellationToken);
            return await _screenCapture.CaptureAsync(passwordBounds, cancellationToken);
        }
        finally
        {
            Opacity = previousOpacity;
        }
    }

    private async Task ShowQualityVisualTargetAsync(
        QualityGuideDecision quality,
        GuideDecision decision,
        ScreenCaptureFrame frame,
        IReadOnlyList<UiElementCandidate> candidates,
        SystemContextSnapshot systemContext,
        long generation,
        CancellationToken cancellationToken)
    {
        var bounds = frame.MapNormalizedBounds(quality.X, quality.Y, quality.Width, quality.Height);
        if (bounds.IsEmpty || bounds.Width < 8 || bounds.Height < 8)
        {
            await TryRouteRecoveryAsync("画像上の候補位置が有効な操作領域にならない", generation, cancellationToken);
            return;
        }

        Rect? snapped = null;
        try { snapped = await _scanner.SnapToAccessibleBoundsAsync(bounds, cancellationToken); }
        catch (OperationCanceledException) { throw; }

        if (!_sessionState.IsCurrent(generation)) return;
        if (HasSystemTransitionV3(systemContext, _systemContext.Capture()))
        {
            await TryRouteRecoveryAsync("画像上の候補を確認中に画面が変わった", generation, cancellationToken);
            return;
        }

        if (snapped is { } accessible && !accessible.IsEmpty)
        {
            bounds = accessible;
        }
        else if (quality.Confidence < MinimumVisualOnlyTargetConfidence)
        {
            await TryRouteRecoveryAsync("画像候補とWindows構造が一致しない", generation, cancellationToken);
            return;
        }

        var visualAction = decision.Action.Equals("double_click", StringComparison.OrdinalIgnoreCase)
            ? "double_click"
            : "left_click";
        var instruction = string.IsNullOrWhiteSpace(decision.Instruction)
            ? visualAction == "double_click"
                ? "青い枠で囲まれた場所で、マウスの左ボタンを間をあけずに2回押してください。"
                : "青い枠で囲まれた場所で、マウスの左ボタンを1回押してください。"
            : decision.Instruction;

        if (!_sessionState.TryTransition(generation, GuidanceSessionState.Presenting)) return;
        _currentDecision = new GuideDecision("target", "vision-target", visualAction, instruction, null, null, quality.Confidence);
        _currentTarget = null;
        _stepBaseline = candidates;
        _stepSystemBaseline = systemContext;
        _guidedBounds = bounds;
        _validatedVisionInstruction = instruction;
        _overlay.ShowTarget(bounds, instruction);
        if (!_sessionState.TryTransition(generation, GuidanceSessionState.AwaitingUserAction))
        {
            _overlay.Hide();
            return;
        }
        ShowInstruction(instruction);
    }
}
