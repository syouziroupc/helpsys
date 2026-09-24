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
    private ObservationSnapshot? _lastOutlawObservation;
    private ScreenCaptureFrame? _lastOutlawFrame;
    private bool _reuseLastOutlawObservationOnce;

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
        planningCts.CancelAfter(TimeSpan.FromSeconds(75));
        var cancellationToken = planningCts.Token;

        var operationStopwatch = Stopwatch.StartNew();
        void LogOutlawPhase(string phase, string? detail = null)
        {
            if (!OutlawModePolicy.Enabled) return;
            LocalLogService.Write(
                "outlaw_phase_timing",
                $"phase={phase};elapsedMs={operationStopwatch.ElapsedMilliseconds}" +
                (string.IsNullOrWhiteSpace(detail) ? string.Empty : $";{detail}"));
        }

        try
        {
            SetState("今の画面と操作できる場所を確認しています…", speak: false);
            ObservationSnapshot snapshot;
            var reuseOutlawObservation = OutlawModePolicy.Enabled &&
                _reuseLastOutlawObservationOnce &&
                _lastOutlawObservation is not null &&
                _lastOutlawFrame is not null &&
                _observationBroker.IsCurrent(_lastOutlawObservation);
            _reuseLastOutlawObservationOnce = false;

            if (reuseOutlawObservation)
            {
                snapshot = _lastOutlawObservation!;
                LocalLogService.Write(
                    "outlaw_observation_reused",
                    $"sequence={snapshot.Sequence};fingerprint={snapshot.Fingerprint};reason=verified_no_effect");
            }
            else
            {
                try
                {
                    snapshot = await _observationBroker.CaptureAsync(4000, cancellationToken);
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
            }

            if (!_sessionState.IsCurrent(generation)) return;
            _lastObservationFingerprint = snapshot.Fingerprint;
            var systemContext = snapshot.System;
            var candidates = snapshot.Elements;
            LogOutlawPhase("observation", $"candidates={candidates.Count};sequence={snapshot.Sequence}");
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

            if (OutlawModePolicy.Enabled &&
                TryAutoSelectOutlawIdentityChoice(candidates, systemContext, generation))
            {
                LocalLogService.Write("outlaw_local_fast_path", "reason=identity_auto_select");
                LogOutlawPhase("local_identity");
                return;
            }

            if (OutlawModePolicy.Enabled &&
                TryOutlawBrowserSearchFastPath(candidates, systemContext, generation))
            {
                LocalLogService.Write("outlaw_local_fast_path", "reason=browser_search");
                LogOutlawPhase("local_browser_search");
                return;
            }

            var imagePrivacyEpoch = CurrentPrivacyEgressEpoch;
            ScreenCaptureFrame frame;
            var visualCaptureStartedUtc = DateTime.UtcNow;
            DateTime visualCaptureCompletedUtc;
            if (reuseOutlawObservation)
            {
                frame = _lastOutlawFrame!;
                visualCaptureCompletedUtc = visualCaptureStartedUtc;
            }
            else
            {
                try
                {
                    frame = await CaptureQualityFrameAsync(candidates, systemContext, cancellationToken);
                    visualCaptureCompletedUtc = DateTime.UtcNow;
                    LogOutlawPhase("screenshot", $"width={frame.ImageWidth};height={frame.ImageHeight}");
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

                if (OutlawModePolicy.Enabled)
                {
                    _lastOutlawObservation = snapshot;
                    _lastOutlawFrame = frame;
                }
            }

            if (!_sessionState.IsCurrent(generation)) return;
            if (!IsPrivacyEgressEpochCurrent(imagePrivacyEpoch))
            {
                HandleTechnicalPlanningUncertainty("画面画像の取得中に安全状態が更新された", generation);
                return;
            }

            var captureChangeUtc = _liveWatcher.LastChangeUtc;
            if (!reuseOutlawObservation &&
                captureChangeUtc is { } duringCapture &&
                duringCapture >= visualCaptureStartedUtc &&
                duringCapture <= visualCaptureCompletedUtc)
            {
                LocalLogService.Write(
                    OutlawModePolicy.Enabled ? "outlaw_visual_capture_stale" : "visual_capture_changed",
                    $"changed={duringCapture:O};start={visualCaptureStartedUtc:O};end={visualCaptureCompletedUtc:O}");
                if (OutlawModePolicy.Enabled &&
                    TryQueueCurrentStateReplan("画像取得中に画面内容が変化したため、古い画像を破棄する", generation))
                    return;

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
                LogOutlawPhase("planner", $"status={quality.Status};action={quality.Action};confidence={quality.Confidence:F3}");
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
                if (OutlawModePolicy.Enabled)
                {
                    if (TryAutoSelectOutlawVisibleChoice(candidates, systemContext, generation)) return;
                    if (TryPresentOutlawVisibleChoiceButtons(candidates, systemContext, generation)) return;
                    HandleTechnicalPlanningUncertainty("モデルが選択を要求したが現在画面から直接選択肢を構成できない", generation);
                    return;
                }

                WaitForClarification(quality.Question ?? "画面上に複数の選択肢があります。どれを使うか教えてください。", generation);
                return;
            }

            var structuredFusionTarget =
                quality.Status.Equals("target", StringComparison.OrdinalIgnoreCase) &&
                !quality.ScreenConfirmed &&
                (OutlawModePolicy.Enabled || quality.Confidence >= MinimumStructuredFallbackConfidence) &&
                !string.IsNullOrWhiteSpace(quality.TargetId) &&
                !string.Equals(quality.TargetId, "vision-target", StringComparison.Ordinal);

            if (!quality.Status.Equals("target", StringComparison.OrdinalIgnoreCase) ||
                (!OutlawModePolicy.Enabled && quality.Confidence < MinimumQualityTargetConfidence))
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
                        OutlawModePolicy.Enabled ? "outlaw_stale_vision_target" : "stale_vision_target",
                        $"changed={afterCapture:O};capture={visualCaptureCompletedUtc:O};reason=post_capture_change");
                    if (OutlawModePolicy.Enabled &&
                        TryQueueCurrentStateReplan("画像判断中に画面が変化したため、古い座標を破棄して再取得する", generation))
                        return;

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
                await ShowQualityVisualTargetAsync(quality, decision, frame, candidates, systemContext, visualCaptureCompletedUtc, generation, cancellationToken);
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
                    OutlawModePolicy.Enabled ? "outlaw_target_revalidation_fallback" : "target_revalidation_failed",
                    $"target={target.Id};action={decision.Action};sameForeground={_observationBroker.IsCurrent(snapshot)}");
                if (!OutlawModePolicy.Enabled || !_observationBroker.IsCurrent(snapshot))
                {
                    HandleTechnicalPlanningUncertainty("案内対象を表示直前に再確認できない", generation);
                    return;
                }

                freshTarget = target;
            }

            LogOutlawPhase("present", $"target={freshTarget.Id};action={decision.Action}");
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
            if (OutlawModePolicy.Enabled)
            {
                if (TryAutoSelectOutlawVisibleChoice(candidates, expectedContext, generation)) return true;
                if (TryPresentOutlawVisibleChoiceButtons(candidates, expectedContext, generation)) return true;
                HandleTechnicalPlanningUncertainty("高速構造判断が選択を要求したが現在画面から直接選択肢を構成できない", generation);
                return true;
            }

            WaitForClarification(quick.Question, generation);
            return true;
        }

        if (quick.Status.Equals("done", StringComparison.OrdinalIgnoreCase) &&
            quick.Confidence >= 0.98 && IsSimpleForegroundGoalSatisfied(_activeRequest, expectedContext))
        {
            StopWithMessage(string.IsNullOrWhiteSpace(quick.Instruction) ? "目的のアプリまたはサイトを開けました。" : quick.Instruction);
            return true;
        }

        if (!quick.Status.Equals("target", StringComparison.OrdinalIgnoreCase) ||
            (!OutlawModePolicy.Enabled && quick.Confidence < 0.93))
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

        var previousOpacity = Opacity;
        try
        {
            Opacity = 0;
            await Task.Delay(35, cancellationToken);
            var frame = await _screenCapture.CaptureAsync(
                Array.Empty<Rect>(),
                expectedContext.ForegroundProcessId,
                expectedContext.ForegroundWindowHandle,
                cancellationToken);
            LocalLogService.SaveDataUriImage("outlaw-screen", frame.ImageDataUri);
            return frame;
        }
        finally
        {
            Opacity = previousOpacity;
        }
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
        DateTime visualCaptureCompletedUtc,
        long generation,
        CancellationToken cancellationToken)
    {
        if (OutlawModePolicy.Enabled &&
            _liveWatcher.LastChangeUtc is { } staleChange &&
            staleChange > visualCaptureCompletedUtc)
        {
            LocalLogService.Write(
                "outlaw_stale_vision_target",
                $"changed={staleChange:O};capture={visualCaptureCompletedUtc:O};reason=pre_present_change");
            if (TryQueueCurrentStateReplan("青枠表示直前に画面が変化したため、古い画像座標を破棄する", generation))
                return;
            HandleTechnicalPlanningUncertainty("画像座標を表示する直前に画面が変化した", generation);
            return;
        }

        Rect bounds;
        if (OutlawModePolicy.Enabled)
        {
            if (!string.Equals(quality.CoordinateSpace, "image_px", StringComparison.OrdinalIgnoreCase) ||
                quality.CoordinateImageWidth != frame.ImageWidth ||
                quality.CoordinateImageHeight != frame.ImageHeight)
            {
                LocalLogService.Write(
                    "outlaw_visual_coordinate_contract_rejected",
                    $"space={quality.CoordinateSpace ?? "null"};declared={quality.CoordinateImageWidth}x{quality.CoordinateImageHeight};actual={frame.ImageWidth}x{frame.ImageHeight}");
                HandleTechnicalPlanningUncertainty("画像座標系が現在のスクリーンショットと一致しない", generation);
                return;
            }

            if (!quality.VisualConsensus)
            {
                LocalLogService.Write(
                    "outlaw_visual_consensus_rejected",
                    $"screenConfirmed={quality.ScreenConfirmed};confidence={quality.Confidence:F3};reason=server_consensus_missing");
                HandleTechnicalPlanningUncertainty("画像位置の二重確認結果を確認できない", generation);
                return;
            }

            bounds = frame.MapImagePixelBounds(
                quality.X,
                quality.Y,
                quality.Width,
                quality.Height,
                quality.CoordinateImageWidth,
                quality.CoordinateImageHeight);
        }
        else
        {
            bounds = frame.MapNormalizedBounds(quality.X, quality.Y, quality.Width, quality.Height);
        }

        if (bounds.IsEmpty || bounds.Width < 8 || bounds.Height < 8)
        {
            HandleTechnicalPlanningUncertainty("画像上の候補位置が有効な操作領域にならない", generation);
            return;
        }

        var surfaceVerified = IsVisualTargetOnCurrentSurface(bounds, candidates, systemContext);
        UiElementCandidate? snapped = null;
        try { snapped = await _scanner.SnapToAccessibleCandidateAsync(bounds, cancellationToken); }
        catch (OperationCanceledException) { throw; }
        catch { }

        if (!_sessionState.IsCurrent(generation)) return;
        if (snapped is { } accessible && !accessible.Bounds.IsEmpty)
        {
            if (!IsCompatibleVisualSnap(bounds, accessible.Bounds) ||
                !IsCurrentSurfaceCandidate(accessible, systemContext))
            {
                LocalLogService.Write(
                    "outlaw_visual_snap_rejected",
                    $"requested={bounds};accessible={accessible.Bounds};pid={accessible.ProcessId};process={accessible.ProcessName};reason=geometry_or_surface_disagreement");
                if (OutlawModePolicy.Enabled &&
                    TryQueueCurrentStateReplan("画像座標と現在画面の押下候補が一致しないため再取得する", generation))
                    return;
                HandleTechnicalPlanningUncertainty("画像座標と実際の操作候補が一致しない", generation);
                return;
            }
            bounds = accessible.Bounds;
            surfaceVerified = true;
        }

        if (OutlawModePolicy.Enabled &&
            (!surfaceVerified ||
             !quality.ScreenConfirmed ||
             !quality.VisualConsensus ||
             quality.Confidence < 0.72 ||
             string.IsNullOrWhiteSpace(quality.VisualEvidence)))
        {
            LocalLogService.Write(
                "outlaw_visual_target_rejected",
                $"confidence={quality.Confidence:F3};screenConfirmed={quality.ScreenConfirmed};visualConsensus={quality.VisualConsensus};surfaceVerified={surfaceVerified};reason=visual_validation_incomplete");
            if (TryQueueCurrentStateReplan("画像上の操作位置を現在画面で十分に検証できないため再取得する", generation))
                return;
            HandleTechnicalPlanningUncertainty("画像上の操作位置を二重確認できない", generation);
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

        LocalLogService.Write(
            "outlaw_visual_target",
            $"confidence={quality.Confidence:F2} bounds={bounds} coordinateSpace={quality.CoordinateSpace ?? "legacy"} image={quality.CoordinateImageWidth}x{quality.CoordinateImageHeight} visualConsensus={quality.VisualConsensus} evidence={quality.VisualEvidence}");

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


    private static bool IsVisualTargetOnCurrentSurface(
        Rect bounds,
        IReadOnlyList<UiElementCandidate> candidates,
        SystemContextSnapshot systemContext)
    {
        if (bounds.IsEmpty) return false;
        var center = new Point(
            bounds.Left + bounds.Width / 2d,
            bounds.Top + bounds.Height / 2d);

        return candidates.Any(candidate =>
            IsCurrentSurfaceCandidate(candidate, systemContext) &&
            !candidate.Bounds.IsEmpty &&
            candidate.Bounds.Contains(center) &&
            candidate.ControlType is "Window" or "Document" or "Pane" or "Group" or "Custom");
    }

    private static bool IsCurrentSurfaceCandidate(
        UiElementCandidate candidate,
        SystemContextSnapshot systemContext)
    {
        if (candidate.ProcessId > 0 && systemContext.ForegroundProcessId > 0 &&
            candidate.ProcessId == systemContext.ForegroundProcessId)
            return true;

        return !string.IsNullOrWhiteSpace(candidate.ProcessName) &&
               !string.IsNullOrWhiteSpace(systemContext.ForegroundProcess) &&
               candidate.ProcessName.Equals(systemContext.ForegroundProcess, StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsCompatibleVisualSnap(Rect requested, Rect accessible)
    {
        if (requested.IsEmpty || accessible.IsEmpty) return false;

        var requestedCenter = new Point(
            requested.Left + requested.Width / 2d,
            requested.Top + requested.Height / 2d);
        var accessibleCenter = new Point(
            accessible.Left + accessible.Width / 2d,
            accessible.Top + accessible.Height / 2d);

        var distance = Math.Sqrt(
            Math.Pow(requestedCenter.X - accessibleCenter.X, 2) +
            Math.Pow(requestedCenter.Y - accessibleCenter.Y, 2));
        var requestedScale = Math.Max(24d, Math.Max(requested.Width, requested.Height));
        var widthRatio = Math.Max(requested.Width, accessible.Width) /
                         Math.Max(1d, Math.Min(requested.Width, accessible.Width));
        var heightRatio = Math.Max(requested.Height, accessible.Height) /
                          Math.Max(1d, Math.Min(requested.Height, accessible.Height));

        var intersection = Rect.Intersect(requested, accessible);
        var overlap = intersection.IsEmpty
            ? 0d
            : intersection.Width * intersection.Height /
              Math.Max(1d, requested.Width * requested.Height);

        return (overlap >= 0.18 || distance <= Math.Max(48d, requestedScale * 0.9)) &&
               widthRatio <= 4.0 &&
               heightRatio <= 4.0;
    }

}
