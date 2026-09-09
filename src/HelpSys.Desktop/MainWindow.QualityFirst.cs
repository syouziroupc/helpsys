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
                StopWithMessage("操作中のウィンドウを特定できませんでした。操作したい画面を一度クリックしてから、もう一度「案内」を押してください。");
                return;
            }

            await _liveWatcher.SetForegroundProcessAsync(systemContext.ForegroundProcessId, cancellationToken);
            var candidates = await _scanner.CaptureCandidatesForProcessAsync(systemContext.ForegroundProcessId, 420, cancellationToken);
            if (!_sessionState.IsCurrent(generation)) return;

            ScreenCaptureFrame frame;
            try
            {
                frame = await CaptureQualityFrameAsync(candidates, cancellationToken);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception captureError)
            {
                if (!_sessionState.TryTransition(generation, GuidanceSessionState.Planning)) return;
                if (await TryStructuredFallbackAsync(candidates, systemContext, generation, cancellationToken)) return;
                StopWithMessage($"画面画像は取得できませんでした。Windows上の操作対象でも次の場所を確定できませんでした: {captureError.Message}");
                return;
            }

            if (!_sessionState.IsCurrent(generation)) return;

            var afterCaptureContext = _systemContext.Capture();
            if (HasSystemTransitionV3(systemContext, afterCaptureContext))
            {
                StopWithMessage("確認中に操作画面が切り替わりました。古い画面は使わず、現在の画面からやり直します。もう一度「案内」を押してください。");
                return;
            }

            if (!_sessionState.TryTransition(generation, GuidanceSessionState.Planning)) return;
            SetState("画面とWindowsの操作情報を照合して、次の1手を決めています…", speak: false);

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
                if (_sessionState.IsCurrent(generation)) StopWithGuideFailure(error);
                return;
            }

            if (!_sessionState.IsCurrent(generation)) return;
            if (HasSystemTransitionV3(systemContext, _systemContext.Capture()))
            {
                StopWithMessage("判断中に画面が変わりました。古い判断は使いません。現在の画面で、もう一度「案内」を押してください。");
                return;
            }

            if (quality.Status.Equals("done", StringComparison.OrdinalIgnoreCase))
            {
                if (!quality.ScreenConfirmed || quality.Confidence < MinimumQualityDoneConfidence || string.IsNullOrWhiteSpace(quality.VisualEvidence))
                {
                    StopWithMessage("目的達成を画面上で確認できていないため、完了扱いにはしません。現在の画面で案内を続けてください。");
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

            if (!quality.Status.Equals("target", StringComparison.OrdinalIgnoreCase) ||
                !quality.ScreenConfirmed ||
                quality.Confidence < MinimumQualityTargetConfidence)
            {
                if (await TryStructuredFallbackAsync(candidates, systemContext, generation, cancellationToken)) return;
                StopWithMessage(string.IsNullOrWhiteSpace(quality.Instruction)
                    ? "画像では次の操作を確定できず、Windows上の操作対象でも一致する場所が見つかりませんでした。"
                    : quality.Instruction);
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
                StopWithMessage("次の操作は候補になりましたが、実際に案内する場所を確定できませんでした。");
                return;
            }

            var target = candidates.FirstOrDefault(x => string.Equals(x.Id, decision.TargetId, StringComparison.Ordinal));
            if (target is null || !target.Interactable || target.Bounds.IsEmpty)
            {
                if (await TryStructuredFallbackAsync(candidates, systemContext, generation, cancellationToken)) return;
                StopWithMessage("画像の候補と、現在操作できるWindowsの場所を一致させられませんでした。");
                return;
            }

            var freshTarget = await _scanner.RevalidateCandidateAsync(target, systemContext.ForegroundProcessId, cancellationToken);
            if (!_sessionState.IsCurrent(generation)) return;
            if (HasSystemTransitionV3(systemContext, _systemContext.Capture()))
            {
                StopWithMessage("案内を表示する直前に画面が変わりました。古い青枠は表示しません。もう一度「案内」を押してください。");
                return;
            }
            if (freshTarget is null)
            {
                if (await TryStructuredFallbackAsync(candidates, systemContext, generation, cancellationToken)) return;
                StopWithMessage("案内しようとした場所が表示直前に変わりました。現在の場所を確定できませんでした。");
                return;
            }

            ShowStructuredTarget(decision, freshTarget, candidates, systemContext, generation);
        }
        catch (OperationCanceledException)
        {
            if (_sessionState.IsCurrent(generation) && _sessionCts is { IsCancellationRequested: false })
                StopWithMessage("画面確認が規定時間内に終わりませんでした。現在の画面で、もう一度「案内」を押してください。");
        }
        catch (InvalidOperationException ex)
        {
            if (_sessionState.IsCurrent(generation))
                StopWithMessage($"現在の操作対象を確定できませんでした: {ex.Message}");
        }
        catch (Exception ex)
        {
            if (_sessionState.IsCurrent(generation)) StopWithMessage($"画面と操作情報の照合中に問題が起きました: {ex.Message}");
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

        SetState("画像だけでは確定できなかったため、現在操作できるWindowsの部品を再確認しています…", speak: false);

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

        // Structured fallback is intentionally narrower than visual guidance: it may point only
        // to a concrete, current UI Automation node. It never declares completion, invents image
        // coordinates, or emits targetless keyboard actions without visual confirmation.
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
            StopWithMessage("画面上の押す場所を十分な大きさで特定できませんでした。");
            return;
        }

        Rect? snapped = null;
        try { snapped = await _scanner.SnapToAccessibleBoundsAsync(bounds, cancellationToken); }
        catch (OperationCanceledException) { throw; }

        if (!_sessionState.IsCurrent(generation)) return;
        if (HasSystemTransitionV3(systemContext, _systemContext.Capture()))
        {
            StopWithMessage("画像上の場所を確認している間に画面が変わりました。古い位置は使いません。");
            return;
        }

        if (snapped is { } accessible && !accessible.IsEmpty)
        {
            bounds = accessible;
        }
        else if (quality.Confidence < MinimumVisualOnlyTargetConfidence)
        {
            StopWithMessage("画面には候補が見えますがWindowsの構造情報と一致せず、画像だけで案内するには確度が足りませんでした。");
            return;
        }

        var instruction = string.IsNullOrWhiteSpace(decision.Instruction)
            ? "青い枠で囲まれた場所で、マウスの左ボタンを1回押してください。"
            : decision.Instruction;

        if (!_sessionState.TryTransition(generation, GuidanceSessionState.Presenting)) return;
        _currentDecision = new GuideDecision("target", "vision-target", "left_click", instruction, null, null, quality.Confidence);
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
