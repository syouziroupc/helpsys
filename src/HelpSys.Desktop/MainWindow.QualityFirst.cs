using System.Windows;
using HelpSys.Models;
using HelpSys.Services;

namespace HelpSys;

public partial class MainWindow
{
    private const double MinimumQualityTargetConfidence = 0.80;
    private const double MinimumQualityDoneConfidence = 0.90;
    private const double MinimumVisualOnlyTargetConfidence = 0.92;

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

        // Quality is intentionally favored over minimum latency. The old planner used 40 s;
        // fused screen inspection, privacy scanning and a larger visual reasoning request get
        // a wider budget while stale-screen generation checks remain active throughout.
        using var planningCts = CancellationTokenSource.CreateLinkedTokenSource(_sessionCts.Token);
        planningCts.CancelAfter(TimeSpan.FromSeconds(70));
        var cancellationToken = planningCts.Token;

        try
        {
            SetState("画面そのものを確認し、Windowsの構造情報と照合しています…", speak: false);
            var systemContext = _systemContext.Capture();
            if (!HasUsableForeground(systemContext))
            {
                await Task.Delay(220, cancellationToken);
                if (!_sessionState.IsCurrent(generation)) return;
                systemContext = _systemContext.Capture();
            }

            if (!HasUsableForeground(systemContext))
            {
                StopWithMessage("今操作している画面を確認できません。画面を見ずに推測して先へ進まないため、案内を停止しました。");
                return;
            }

            await _liveWatcher.SetForegroundProcessAsync(systemContext.ForegroundProcessId, cancellationToken);
            var candidates = await _scanner.CaptureCandidatesForProcessAsync(systemContext.ForegroundProcessId, 420, cancellationToken);
            if (!_sessionState.IsCurrent(generation)) return;

            // Unlike the legacy route, an empty UIA tree is not a reason to guess or to skip
            // the screen. The screenshot remains the primary evidence and UIA may be empty.
            var frame = await CaptureQualityFrameAsync(candidates, cancellationToken);
            if (!_sessionState.IsCurrent(generation)) return;

            var afterCaptureContext = _systemContext.Capture();
            if (HasSystemTransitionV3(systemContext, afterCaptureContext))
            {
                StopWithMessage("画面を確認している途中で操作対象が切り替わりました。古い画像やUI情報で先へ進まず、案内を破棄しました。");
                return;
            }

            if (!_sessionState.TryTransition(generation, GuidanceSessionState.Planning)) return;
            SetState("画面画像を主な根拠にして、構造情報と照らし合わせながら次の1手を考えています…", speak: false);

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
                if (_sessionState.IsCurrent(generation)) StopWithGuideFailure(error);
                return;
            }

            if (!_sessionState.IsCurrent(generation)) return;
            if (HasSystemTransitionV3(systemContext, _systemContext.Capture()))
            {
                StopWithMessage("判断が終わる前に画面が変わりました。返答速度より現在画面の正しさを優先し、古い判断を破棄しました。");
                return;
            }

            if (quality.Status.Equals("done", StringComparison.OrdinalIgnoreCase))
            {
                if (!quality.ScreenConfirmed || quality.Confidence < MinimumQualityDoneConfidence || string.IsNullOrWhiteSpace(quality.VisualEvidence))
                {
                    StopWithMessage("画面上で目的達成を確認できていないため、完了扱いにしませんでした。");
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
                StopWithMessage(string.IsNullOrWhiteSpace(quality.Instruction)
                    ? "画面画像とWindowsの構造情報を照合しましたが、次の操作を十分な確度で決められませんでした。推測では先へ進みません。"
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
                StopWithMessage("画面上で次の操作は確認できましたが、実際に案内する対象を確定できませんでした。推測では枠を出しません。");
                return;
            }

            var target = candidates.FirstOrDefault(x => string.Equals(x.Id, decision.TargetId, StringComparison.Ordinal));
            if (target is null || !target.Interactable || target.Bounds.IsEmpty)
            {
                StopWithMessage("画像で見えている場所とWindowsの操作対象を一致させられませんでした。誤った場所を案内しないため停止しました。");
                return;
            }

            var freshTarget = await _scanner.RevalidateCandidateAsync(target, systemContext.ForegroundProcessId, cancellationToken);
            if (!_sessionState.IsCurrent(generation)) return;
            if (HasSystemTransitionV3(systemContext, _systemContext.Capture()))
            {
                StopWithMessage("案内を表示する直前に画面が変わりました。古い青枠を表示せず破棄しました。");
                return;
            }
            if (freshTarget is null)
            {
                StopWithMessage("画面画像で確認した場所が表示直前には無くなっていました。古い位置を使わず停止しました。");
                return;
            }

            ShowStructuredTarget(decision, freshTarget, candidates, systemContext, generation);
        }
        catch (OperationCanceledException)
        {
            if (_sessionState.IsCurrent(generation) && _sessionCts is { IsCancellationRequested: false })
                StopWithMessage("高精度な画面確認が規定時間内に完了しませんでした。情報を減らして推測するのではなく、現在画面からやり直してください。");
        }
        catch (InvalidOperationException ex)
        {
            if (_sessionState.IsCurrent(generation))
                StopWithMessage($"画面そのものを安全に確認できないため、UI情報だけで推測せず案内を停止しました: {ex.Message}");
        }
        catch (Exception ex)
        {
            if (_sessionState.IsCurrent(generation)) StopWithMessage($"画面と構造情報の照合中に問題が起きたため、推測せず停止しました: {ex.Message}");
        }
        finally
        {
            _sessionState.EndOperation(generation);
            GuideButton.IsEnabled = !_planning;
        }
    }

    private async Task<ScreenCaptureFrame> CaptureQualityFrameAsync(
        IReadOnlyList<UiElementCandidate> candidates,
        CancellationToken cancellationToken)
    {
        var passwordBounds = candidates.Where(x => x.Password).Select(x => x.Bounds).ToArray();
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
        // A >=0.92 fused screen decision may intentionally refer to a custom-rendered
        // control with no useful UIA node. Mark this exact instruction as already visually
        // validated so the live watcher does not immediately discard it merely for lacking UIA.
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
