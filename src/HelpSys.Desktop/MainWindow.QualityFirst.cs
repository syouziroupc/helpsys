using System.Diagnostics;
using System.Windows;
using HelpSys.Models;
using HelpSys.Services;

namespace HelpSys;

public partial class MainWindow
{
    private const double MinimumQualityTargetConfidence = 0.60;
    private const double MinimumQualityDoneConfidence = 0.65;
    private const double MinimumVisualOnlyTargetConfidence = 0.60;
    private const double MinimumStructuredFallbackConfidence = 0.60;

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
        planningCts.CancelAfter(TimeSpan.FromSeconds(35));
        var cancellationToken = planningCts.Token;

        try
        {
            SetState("今の画面と操作できる場所を確認しています…", speak: false);
            ObservationSnapshot snapshot;
            try
            {
                snapshot = await _observationBroker.CaptureAsync(1200, cancellationToken);
            }
            catch (ObservationChangedException)
            {
                HandleTechnicalPlanningUncertainty("観測中に前面画面が切り替わった", generation);
                return;
            }
            catch (InvalidOperationException)
            {
                HandleTechnicalPlanningUncertainty("前面ウィンドウを特定できない", generation);
                return;
            }

            if (!_sessionState.IsCurrent(generation)) return;
            _lastObservationFingerprint = snapshot.Fingerprint;
            var systemContext = snapshot.System;
            var candidates = snapshot.Elements;
            await _liveWatcher.SetForegroundProcessAsync(systemContext.ForegroundProcessId, cancellationToken);

            if (_diagnosticMode.Enabled && systemContext.Browser is not null && systemContext.ForegroundWindowHandle != nint.Zero)
            {
                try
                {
                    var diagnostic = await _scanner.CaptureWindowDiagnosticsAsync(
                        systemContext.ForegroundWindowHandle,
                        systemContext.ForegroundProcessId,
                        cancellationToken);
                    Trace.WriteLine($"[HelpSys:UIA] processCandidates={candidates.Count};{diagnostic}");
                }
                catch (OperationCanceledException) { throw; }
                catch { }
            }

            var structuralEvidence = GuidanceEvidenceService.Build(false, candidates, _history, systemContext);
            SetState(GuidanceEvidenceService.BuildProgressText(structuralEvidence), speak: false);

            var imagePrivacyEpoch = CurrentPrivacyEgressEpoch;
            ScreenCaptureFrame frame;
            var visualCaptureStartedUtc = DateTime.UtcNow;
            DateTime visualCaptureCompletedUtc;
            try
            {
                frame = await CaptureQualityFrameAsync(candidates, systemContext, cancellationToken);
                visualCaptureCompletedUtc = DateTime.UtcNow;
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                if (!_sessionState.TryTransition(generation, GuidanceSessionState.Planning)) return;
                if (candidates.Count > 0 && await TryFastStructuredPlanAsync(candidates, systemContext, generation, cancellationToken)) return;
                HandleTechnicalPlanningUncertainty("Privacy Gateにより画像を使えず、構造情報でも次の操作を確定できない", generation);
                return;
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch
            {
                if (!_sessionState.TryTransition(generation, GuidanceSessionState.Planning)) return;
                if (candidates.Count > 0 && await TryFastStructuredPlanAsync(candidates, systemContext, generation, cancellationToken)) return;
                HandleTechnicalPlanningUncertainty("画像を取得できず、構造情報でも次の操作を確定できない", generation);
                return;
            }

            if (!_sessionState.IsCurrent(generation)) return;
            if (!IsPrivacyEgressEpochCurrent(imagePrivacyEpoch))
            {
                HandleTechnicalPlanningUncertainty("画面画像の取得中に安全状態が更新された", generation);
                return;
            }

            var captureChangeUtc = _liveWatcher.LastChangeUtc;
            if (captureChangeUtc is { } duringCapture &&
                duringCapture >= visualCaptureStartedUtc &&
                duringCapture <= visualCaptureCompletedUtc)
            {
                LocalLogService.Write(
                    OutlawModePolicy.Enabled ? "outlaw_visual_capture_changed" : "visual_capture_changed",
                    $"changed={duringCapture:O};start={visualCaptureStartedUtc:O};end={visualCaptureCompletedUtc:O}");
                HandleTechnicalPlanningUncertainty("画面画像取得中に内容が変化した", generation);
                return;
            }

            if (!_observationBroker.IsCurrent(snapshot))
            {
                LocalLogService.Write(
                    OutlawModePolicy.Enabled ? "outlaw_stale_observation" : "stale_observation",
                    "phase=after_screenshot;discarding planner observation because foreground identity changed");
                HandleTechnicalPlanningUncertainty("確認中に画面切替が続いている", generation);
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
                if (error.Kind == GuideFailureKind.ContextChanged)
                {
                    HandleTechnicalPlanningUncertainty("通常計画中に画面状態が変化した", generation);
                    return;
                }
                if (_sessionState.IsCurrent(generation) && error.Kind is GuideFailureKind.Network or GuideFailureKind.ServiceUnavailable or GuideFailureKind.InvalidResponse)
                {
                    var reason = IsLocalSmokeEndpoint()
                        ? $"案内サービスの一時的な応答失敗: {error.Message}"
                        : "案内サービスの一時的な応答失敗";
                    HandleTechnicalPlanningUncertainty(reason, generation);
                    return;
                }
                if (_sessionState.IsCurrent(generation)) StopWithGuideFailure(error);
                return;
            }

            if (!_sessionState.IsCurrent(generation)) return;
            if (!_observationBroker.IsCurrent(snapshot))
            {
                LocalLogService.Write(
                    OutlawModePolicy.Enabled ? "outlaw_stale_observation" : "stale_observation",
                    "phase=after_planner;discarding planner result because foreground identity changed");
                HandleTechnicalPlanningUncertainty("判断中の画面変化が続いている", generation);
                return;
            }

            if (quality.Status.Equals("done", StringComparison.OrdinalIgnoreCase))
            {
                if (quality.Confidence < MinimumQualityDoneConfidence)
                {
                    HandleTechnicalPlanningUncertainty("完了判定の確度が不足している", generation);
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
                quality.Confidence < MinimumQualityTargetConfidence)
            {
                HandleTechnicalPlanningUncertainty(
                    string.IsNullOrWhiteSpace(quality.Instruction)
                        ? "次の操作を十分な信頼度で確定できない"
                        : "次の操作候補を安全に確定できない",
                    generation);
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

            if (string.Equals(decision.TargetId, "vision-target", StringComparison.Ordinal))
            {
                var lastVisualChangeUtc = _liveWatcher.LastChangeUtc;
                if (lastVisualChangeUtc is { } afterCapture && afterCapture > visualCaptureCompletedUtc)
                {
                    LocalLogService.Write(
                        "outlaw_stale_vision_target",
                        $"changed={afterCapture:O};capture={visualCaptureCompletedUtc:O};rejecting planner coordinates");
                    HandleTechnicalPlanningUncertainty("画像判断後に画面内容が変化した", generation);
                    return;
                }
            }

            if (decision.Action.Equals("press_key", StringComparison.OrdinalIgnoreCase) && string.IsNullOrWhiteSpace(decision.TargetId))
            {
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
                HandleTechnicalPlanningUncertainty("操作内容は候補になったが対象を特定できない", generation);
                return;
            }

            var target = candidates.FirstOrDefault(x => string.Equals(x.Id, decision.TargetId, StringComparison.Ordinal));
            if (target is null || !target.Interactable || !target.Enabled || target.Bounds.IsEmpty)
            {
                HandleTechnicalPlanningUncertainty("選ばれた対象を現在画面で操作できない", generation);
                return;
            }

            var freshTarget = await _scanner.RevalidateCandidateAsync(
                target,
                OutlawModePolicy.Enabled ? target.ProcessId : systemContext.ForegroundProcessId,
                cancellationToken);
            if (!_sessionState.IsCurrent(generation)) return;
            if (!_observationBroker.IsCurrent(snapshot))
            {
                LocalLogService.Write(
                    OutlawModePolicy.Enabled ? "outlaw_stale_observation" : "stale_observation",
                    "phase=before_structured_overlay;discarding target because foreground identity changed");
                HandleTechnicalPlanningUncertainty("案内表示直前の画面変化が続いている", generation);
                return;
            }
            if (freshTarget is null)
            {
                LocalLogService.Write(
                    OutlawModePolicy.Enabled ? "outlaw_target_revalidation_failed" : "target_revalidation_failed",
                    $"target={target.Id};action={decision.Action};rejecting stale pre-plan bounds");
                HandleTechnicalPlanningUncertainty("案内対象を表示直前に再確認できない", generation);
                return;
            }

            ShowStructuredTarget(decision, freshTarget, candidates, systemContext, generation);
        }
        catch (OperationCanceledException)
        {
            if (_sessionState.IsCurrent(generation) && _sessionCts is { IsCancellationRequested: false })
                HandleTechnicalPlanningUncertainty("通常の画面確認が時間内に完了しなかった", generation);
        }
        catch (InvalidOperationException ex)
        {
            if (_sessionState.IsCurrent(generation) && _sessionCts is { IsCancellationRequested: false })
                HandleTechnicalPlanningUncertainty($"操作対象の構造確認に失敗: {ex.GetType().Name}", generation);
        }
        catch (Exception ex)
        {
            if (_sessionState.IsCurrent(generation) && _sessionCts is { IsCancellationRequested: false })
                HandleTechnicalPlanningUncertainty($"案内処理の例外: {ex.GetType().Name}", generation);
        }
        finally
        {
            _sessionState.EndOperation(generation);
            GuideButton.IsEnabled = !_planning;
        }
    }

    private async Task<bool> TryFastStructuredPlanAsync(
        IReadOnlyList<UiElementCandidate> candidates,
        SystemContextSnapshot expectedContext,
        long generation,
        CancellationToken cancellationToken)
    {
        if (_activeRequest is null || candidates.Count == 0 || !_sessionState.IsCurrent(generation)) return false;

        SetState("画面上の文字と操作できる場所から、次の手順を確認しています…", speak: false);
        GuideDecision quick;
        try
        {
            quick = await _cloudGuide.PlanAsync(_activeRequest, candidates, _history, expectedContext, cancellationToken);
        }
        catch (OperationCanceledException) { throw; }
        catch (GuideServiceException)
        {
            // The multimodal path is an independent fallback. Do not turn one fast-path failure
            // into a session stop or a second long retry of the same request.
            return false;
        }
        catch
        {
            return false;
        }

        if (!_sessionState.IsCurrent(generation)) return true;
        var current = _systemContext.Capture();
        if (!HasSameCaptureIdentity(expectedContext, current)) return false;

        if (quick.Status.Equals("clarify", StringComparison.OrdinalIgnoreCase) && !string.IsNullOrWhiteSpace(quick.Question))
        {
            WaitForClarification(quick.Question, generation);
            return true;
        }

        if (quick.Status.Equals("done", StringComparison.OrdinalIgnoreCase) &&
            quick.Confidence >= 0.98 && IsSimpleForegroundGoalSatisfied(_activeRequest, expectedContext))
        {
            StopWithMessage(string.IsNullOrWhiteSpace(quick.Instruction) ? "目的のアプリまたはサイトを開けました。" : quick.Instruction);
            return true;
        }

        if (!quick.Status.Equals("target", StringComparison.OrdinalIgnoreCase) || quick.Confidence < 0.93)
            return false;

        if (quick.Action.Equals("press_key", StringComparison.OrdinalIgnoreCase) && string.IsNullOrWhiteSpace(quick.TargetId))
        {
            ShowKeyboardGuide(quick, candidates, expectedContext, generation);
            return true;
        }

        if (string.IsNullOrWhiteSpace(quick.TargetId)) return false;
        var target = candidates.FirstOrDefault(x => string.Equals(x.Id, quick.TargetId, StringComparison.Ordinal));
        if (target is null || !target.Interactable || !target.Enabled || target.Bounds.IsEmpty) return false;

        var fresh = await _scanner.RevalidateCandidateAsync(
            target,
            OutlawModePolicy.Enabled ? target.ProcessId : expectedContext.ForegroundProcessId,
            cancellationToken);
        if (!_sessionState.IsCurrent(generation) || fresh is null) return false;
        if (!HasSameCaptureIdentity(expectedContext, _systemContext.Capture())) return false;

        ShowStructuredTarget(quick, fresh, candidates, expectedContext, generation);
        return true;
    }

    private static bool IsSimpleForegroundGoalSatisfied(string request, SystemContextSnapshot context)
    {
        var goal = request.Trim();
        var process = context.ForegroundProcess ?? string.Empty;
        if (process.Equals("excel", StringComparison.OrdinalIgnoreCase) &&
            (goal.Contains("Excel", StringComparison.OrdinalIgnoreCase) || goal.Contains("エクセル", StringComparison.OrdinalIgnoreCase))) return true;
        if (process.Equals("winword", StringComparison.OrdinalIgnoreCase) &&
            (goal.Contains("Word", StringComparison.OrdinalIgnoreCase) || goal.Contains("ワード", StringComparison.OrdinalIgnoreCase))) return true;
        if (process.Equals("powerpnt", StringComparison.OrdinalIgnoreCase) &&
            (goal.Contains("PowerPoint", StringComparison.OrdinalIgnoreCase) || goal.Contains("パワーポイント", StringComparison.OrdinalIgnoreCase) || goal.Contains("パワポ", StringComparison.OrdinalIgnoreCase))) return true;

        var domain = context.Browser?.Domain ?? string.Empty;
        if (domain.Contains("youtube.com", StringComparison.OrdinalIgnoreCase) &&
            (goal.Contains("YouTube", StringComparison.OrdinalIgnoreCase) || goal.Contains("ユーチューブ", StringComparison.OrdinalIgnoreCase))) return true;
        return false;
    }

    private async Task<ScreenCaptureFrame> CaptureQualityFrameAsync(
        IReadOnlyList<UiElementCandidate> candidates,
        SystemContextSnapshot expectedContext,
        CancellationToken cancellationToken)
    {
        if (!HasUsableForeground(expectedContext) || expectedContext.ForegroundWindowHandle == nint.Zero)
            throw new InvalidOperationException("操作対象ウィンドウを特定できません。");

        _speechInput.HideOverlay();
        _overlay.Hide();
        _keyHint.Hide();

        LocalLogService.Write(
            "outlaw_capture",
            $"pid={expectedContext.ForegroundProcessId} hwnd={expectedContext.ForegroundWindowHandle} candidates={candidates.Count}");

        var frame = await _screenCapture.CaptureAsync(
            Array.Empty<Rect>(),
            expectedContext.ForegroundProcessId,
            expectedContext.ForegroundWindowHandle,
            cancellationToken);
        LocalLogService.SaveDataUriImage("outlaw-screen", frame.ImageDataUri);
        return frame;
    }

    private static bool HasSameCaptureIdentity(SystemContextSnapshot expected, SystemContextSnapshot current)
    {
        if (expected.ForegroundProcessId <= 0 || expected.ForegroundWindowHandle == nint.Zero) return false;
        if (current.ForegroundProcessId != expected.ForegroundProcessId) return false;
        if (current.ForegroundWindowHandle != expected.ForegroundWindowHandle) return false;
        return current.ForegroundProcess.Equals(expected.ForegroundProcess, StringComparison.OrdinalIgnoreCase);
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
            HandleTechnicalPlanningUncertainty("画像上の候補位置が有効な操作領域にならない", generation);
            return;
        }

        // Snap when Windows exposes a useful accessibility node, but never require it in Outlaw.
        Rect? snapped = null;
        try { snapped = await _scanner.SnapToAccessibleBoundsAsync(bounds, cancellationToken); }
        catch (OperationCanceledException) { throw; }
        catch { }

        if (!_sessionState.IsCurrent(generation)) return;
        if (snapped is { } accessible && !accessible.IsEmpty) bounds = accessible;

        var visualAction = decision.Action.Equals("double_click", StringComparison.OrdinalIgnoreCase)
            ? "double_click"
            : "left_click";
        var instruction = string.IsNullOrWhiteSpace(decision.Instruction)
            ? visualAction == "double_click"
                ? "青い枠で囲まれた場所で、マウスの左ボタンを間をあけずに2回押してください。"
                : "青い枠で囲まれた場所で、マウスの左ボタンを1回押してください。"
            : decision.Instruction;

        LocalLogService.Write(
            "outlaw_visual_target",
            $"confidence={quality.Confidence:F2} bounds={bounds} evidence={quality.VisualEvidence}");

        var visualDecision = new GuideDecision(
            "target",
            "vision-target",
            visualAction,
            instruction,
            null,
            null,
            quality.Confidence);
        if (!TryAcceptOutlawGuidance(visualDecision, null, candidates, systemContext, bounds, generation)) return;
        if (!_sessionState.TryTransition(generation, GuidanceSessionState.Presenting)) return;
        if (!OutlawModePolicy.Enabled)
        {
            ResetCurrentStateReplanBudget();
            ResetResilienceRecovery();
        }
        _currentDecision = visualDecision;
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
