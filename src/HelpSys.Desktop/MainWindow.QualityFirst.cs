using System.Diagnostics;
using System.Windows;
using HelpSys.Models;
using HelpSys.Services;

namespace HelpSys;

public partial class MainWindow
{
    private const double MinimumQualityTargetConfidence = 0.80;
    private const double MinimumQualityDoneConfidence = 0.90;
    private const double MinimumVisualOnlyTargetConfidence = 0.92;
    private const double MinimumStructuredFallbackConfidence = 0.93;

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
        planningCts.CancelAfter(TimeSpan.FromSeconds(38));
        var cancellationToken = planningCts.Token;

        try
        {
            SetState("今の画面と操作できる場所を確認しています…", speak: false);
            ObservationSnapshot snapshot;
            try
            {
                snapshot = await _observationBroker.CaptureAsync(240, cancellationToken);
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
            try
            {
                frame = await CaptureQualityFrameAsync(candidates, systemContext, cancellationToken);
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

            if (!_observationBroker.IsCurrent(snapshot))
            {
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
                    HandleTechnicalPlanningUncertainty("案内サービスの一時的な応答失敗", generation);
                    return;
                }
                if (_sessionState.IsCurrent(generation)) StopWithGuideFailure(error);
                return;
            }

            if (!_sessionState.IsCurrent(generation)) return;
            if (!_observationBroker.IsCurrent(snapshot))
            {
                HandleTechnicalPlanningUncertainty("判断中の画面変化が続いている", generation);
                return;
            }

            if (quality.Status.Equals("done", StringComparison.OrdinalIgnoreCase))
            {
                if (!quality.ScreenConfirmed || quality.Confidence < MinimumQualityDoneConfidence || string.IsNullOrWhiteSpace(quality.VisualEvidence))
                {
                    HandleTechnicalPlanningUncertainty("完了を現在状態から確認できない", generation);
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

            if (decision.Action.Equals("press_key", StringComparison.OrdinalIgnoreCase) && string.IsNullOrWhiteSpace(decision.TargetId))
            {
                if (!quality.ScreenConfirmed)
                {
                    HandleTechnicalPlanningUncertainty("対象なしのキー操作を画面情報で確認できない", generation);
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
                HandleTechnicalPlanningUncertainty("操作内容は候補になったが対象を特定できない", generation);
                return;
            }

            var target = candidates.FirstOrDefault(x => string.Equals(x.Id, decision.TargetId, StringComparison.Ordinal));
            if (target is null || !target.Interactable || !target.Enabled || target.Bounds.IsEmpty)
            {
                HandleTechnicalPlanningUncertainty("選ばれた対象を現在画面で操作できない", generation);
                return;
            }

            var freshTarget = await _scanner.RevalidateCandidateAsync(target, systemContext.ForegroundProcessId, cancellationToken);
            if (!_sessionState.IsCurrent(generation)) return;
            if (!_observationBroker.IsCurrent(snapshot))
            {
                HandleTechnicalPlanningUncertainty("案内表示直前の画面変化が続いている", generation);
                return;
            }
            if (freshTarget is null)
            {
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

        var fresh = await _scanner.RevalidateCandidateAsync(target, expectedContext.ForegroundProcessId, cancellationToken);
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

    private async Task<bool> TryStructuredFallbackAsync(
        IReadOnlyList<UiElementCandidate> previousCandidates,
        SystemContextSnapshot expectedContext,
        long generation,
        CancellationToken cancellationToken)
    {
        if (_activeRequest is null || previousCandidates.Count == 0 || !_sessionState.IsCurrent(generation)) return false;
        var currentBeforeScan = _systemContext.Capture();
        if (!HasSameCaptureIdentity(expectedContext, currentBeforeScan)) return false;

        SetState("画像だけでは確定できないため、Windowsの構造情報から次の操作を再確認しています…", speak: false);

        IReadOnlyList<UiElementCandidate> candidates;
        try
        {
            candidates = await _scanner.CaptureCandidatesForProcessAsync(expectedContext.ForegroundProcessId, 420, cancellationToken);
        }
        catch (OperationCanceledException) { throw; }
        catch { return false; }

        if (!_sessionState.IsCurrent(generation) || candidates.Count == 0) return false;
        var currentAfterScan = _systemContext.Capture();
        if (!HasSameCaptureIdentity(expectedContext, currentAfterScan)) return false;

        GuideDecision fallback;
        try
        {
            fallback = await _cloudGuide.PlanAsync(_activeRequest, candidates, _history, expectedContext, cancellationToken);
        }
        catch (OperationCanceledException) { throw; }
        catch { return false; }

        if (!_sessionState.IsCurrent(generation)) return false;
        var currentAfterPlan = _systemContext.Capture();
        if (!HasSameCaptureIdentity(expectedContext, currentAfterPlan)) return false;

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
        var currentBeforePresent = _systemContext.Capture();
        if (!HasSameCaptureIdentity(expectedContext, currentBeforePresent)) return false;

        ShowStructuredTarget(fallback, freshTarget, candidates, expectedContext, generation);
        return true;
    }

    private async Task<ScreenCaptureFrame> CaptureQualityFrameAsync(
        IReadOnlyList<UiElementCandidate> candidates,
        SystemContextSnapshot expectedContext,
        CancellationToken cancellationToken)
    {
        if (!HasUsableForeground(expectedContext) || expectedContext.ForegroundWindowHandle == nint.Zero)
            throw new InvalidOperationException("検証済み操作対象ウィンドウが無いため、画面画像を送信しません。");

        var privacyContext = _systemContext.Capture();
        if (!HasSameCaptureIdentity(expectedContext, privacyContext))
            throw new InvalidOperationException("操作対象ウィンドウが変わったため、古い画面画像を送信しません。");

        var privacy = _cloudGuide.PreflightPrivacy(privacyContext, candidates);
        if (!privacy.CanSend)
            throw new OperationCanceledException("Privacy Gate blocked screenshot creation.", cancellationToken);

        var candidateProcessIds = candidates
            .Select(x => x.ProcessId)
            .Where(x => x > 0)
            .Distinct()
            .Take(2)
            .ToArray();
        if (candidateProcessIds.Length > 1)
            throw new InvalidOperationException("操作対象候補が複数プロセスに分かれているため、画面画像を送信しません。");

        var expectedProcessId = candidateProcessIds.Length == 1
            ? candidateProcessIds[0]
            : expectedContext.ForegroundProcessId;
        if (expectedProcessId <= 0 || expectedProcessId != expectedContext.ForegroundProcessId)
            throw new InvalidOperationException("操作対象プロセスと前面ウィンドウを安全に対応付けできないため、画面画像を送信しません。");

        var sensitiveBounds = ScreenshotRedactionPolicy.Build(candidates);
        _speechInput.HideOverlay();
        _overlay.Hide();
        _keyHint.Hide();

        // Keep the HelpSys window visually stable while capturing. ScreenCaptureService already
        // redacts windows that occlude the verified target, including HelpSys itself, before any
        // image can leave the machine. Making this window transparent caused visible flicker and
        // also created unnecessary foreground-transition races.
        var captureContext = _systemContext.Capture();
        if (!HasSameCaptureIdentity(expectedContext, captureContext))
            throw new InvalidOperationException("撮影直前に操作対象ウィンドウが変わったため、画面画像を送信しません。");

        var secondPrivacy = _cloudGuide.PreflightPrivacy(captureContext, candidates);
        if (!secondPrivacy.CanSend)
            throw new OperationCanceledException("Privacy Gate blocked screenshot creation.", cancellationToken);

        return await _screenCapture.CaptureAsync(
            sensitiveBounds,
            expectedProcessId,
            expectedContext.ForegroundWindowHandle,
            cancellationToken);
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

        UiElementCandidate? snappedTarget = null;
        try { snappedTarget = await _scanner.SnapToAccessibleCandidateAsync(bounds, cancellationToken); }
        catch (OperationCanceledException) { throw; }

        if (!_sessionState.IsCurrent(generation)) return;
        var currentContext = _systemContext.Capture();
        if (!HasSameCaptureIdentity(systemContext, currentContext))
        {
            HandleTechnicalPlanningUncertainty("画像候補確認中の画面変化が続いている", generation);
            return;
        }

        if (snappedTarget is null ||
            snappedTarget.Password ||
            !snappedTarget.Interactable ||
            !snappedTarget.Enabled ||
            snappedTarget.Bounds.IsEmpty ||
            snappedTarget.ProcessId != systemContext.ForegroundProcessId)
        {
            // A visual coordinate is never enough by itself. Require the exact current foreground
            // process and concrete accessibility metadata before exposing a click instruction.
            HandleTechnicalPlanningUncertainty("画像候補を現在のWindows操作要素へ対応付けできない", generation);
            return;
        }

        var proposedVisualAction = decision.Action.Equals("double_click", StringComparison.OrdinalIgnoreCase)
            ? "double_click"
            : "left_click";
        var normalizedVisual = NormalizeStructuredDecisionForTarget(
            new GuideDecision("target", "vision-target", proposedVisualAction, string.Empty, null, null, quality.Confidence),
            snappedTarget);
        if (normalizedVisual is null)
        {
            HandleTechnicalPlanningUncertainty("画像候補の操作方法をWindows要素種別と整合できない", generation);
            return;
        }

        bounds = snappedTarget.Bounds;
        var visualAction = normalizedVisual.Action;
        var instruction = normalizedVisual.Instruction;

        if (!_sessionState.TryTransition(generation, GuidanceSessionState.Presenting)) return;
        ResetCurrentStateReplanBudget();
        ResetResilienceRecovery();
        _currentDecision = new GuideDecision("target", "vision-target", visualAction, instruction, null, null, quality.Confidence);
        _currentTarget = snappedTarget;
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
