using System.Security.Cryptography;
using System.Text;
using System.Windows;
using HelpSys.Models;
using HelpSys.Services;

namespace HelpSys;

public partial class MainWindow
{
    private const int MaximumOutlawFailedGuidanceKeys = 64;
    private readonly HashSet<string> _outlawFailedGuidanceKeys = new(StringComparer.Ordinal);
    private readonly Queue<string> _outlawFailedGuidanceOrder = new();
    private string _outlawLastPresentedGuidanceKey = string.Empty;

    private bool TryAcceptOutlawGuidance(
        GuideDecision decision,
        UiElementCandidate? target,
        IReadOnlyList<UiElementCandidate> candidates,
        SystemContextSnapshot systemContext,
        Rect? bounds,
        long generation)
    {
        if (!OutlawModePolicy.Enabled) return true;

        var key = BuildOutlawGuidanceKey(decision, target, candidates, systemContext, bounds);
        if (_outlawFailedGuidanceKeys.Contains(key))
        {
            var label = target is null
                ? (decision.TargetId ?? decision.Key ?? "画面上の場所")
                : DisplayName(target.Name, target.ControlType);

            _history.Add(new GuideHistoryItem(
                _stepNumber,
                "outlaw_repeat_blocked",
                label,
                "同じ観測状態で既に効果が無かった操作をPlannerが再提示したため、ローカル反復ガードで破棄した。別の操作経路を選ぶ必要がある。"));
            if (_history.Count > 64) _history.RemoveAt(0);

            LocalLogService.Write(
                "outlaw_repeat_blocked",
                $"action={decision.Action};target={label};key={key}");

            if (!TryQueueCurrentStateReplan("同じ画面で効果が無かった同一操作を再提示しようとした", generation))
            {
                _liveReplanPending = false;
                SetState(
                    "同じ画面で効果がなかった同じ操作が再び選ばれたため、自動反復を停止しました。画面が変化すると現在位置から再判定します。",
                    speak: false);
            }

            return false;
        }

        _outlawLastPresentedGuidanceKey = key;
        LocalLogService.Write(
            "outlaw_guidance_presented",
            $"action={decision.Action};target={target?.Id ?? decision.TargetId ?? decision.Key ?? "none"};key={key}");
        return true;
    }

    private void RecordOutlawGuidanceOutcome(
        GuideDecision decision,
        UiElementCandidate? target,
        IReadOnlyList<UiElementCandidate> candidates,
        SystemContextSnapshot? systemContext,
        Rect? bounds,
        bool changed)
    {
        if (!OutlawModePolicy.Enabled) return;

        var context = systemContext ?? _systemContext.Capture();
        var key = BuildOutlawGuidanceKey(decision, target, candidates, context, bounds);

        if (changed)
        {
            // Keep failed state-action keys for the lifetime of this guidance session.
            // Their keys already include the semantic UI state, so progress naturally moves to
            // different keys while returning to a previously failed state remains protected.
            _outlawLastPresentedGuidanceKey = string.Empty;
            ResetCurrentStateReplanBudget();
            ResetResilienceRecovery();
            LocalLogService.Write(
                "outlaw_progress_verified",
                $"action={decision.Action};key={key};retainedFailedKeys={_outlawFailedGuidanceKeys.Count}");
            return;
        }

        RememberOutlawFailedGuidanceKey(key);
        _outlawLastPresentedGuidanceKey = key;
        LocalLogService.Write(
            "outlaw_no_effect_recorded",
            $"action={decision.Action};key={key};failedKeys={_outlawFailedGuidanceKeys.Count}");
    }

    private void RememberOutlawFailedGuidanceKey(string key)
    {
        if (string.IsNullOrWhiteSpace(key) || !_outlawFailedGuidanceKeys.Add(key)) return;

        _outlawFailedGuidanceOrder.Enqueue(key);
        while (_outlawFailedGuidanceOrder.Count > MaximumOutlawFailedGuidanceKeys)
        {
            var oldest = _outlawFailedGuidanceOrder.Dequeue();
            _outlawFailedGuidanceKeys.Remove(oldest);
        }
    }

    private void ResetOutlawLoopGuard()
    {
        _outlawFailedGuidanceKeys.Clear();
        _outlawFailedGuidanceOrder.Clear();
        _outlawLastPresentedGuidanceKey = string.Empty;
    }

    private string BuildOutlawGuidanceKey(
        GuideDecision decision,
        UiElementCandidate? target,
        IReadOnlyList<UiElementCandidate> candidates,
        SystemContextSnapshot systemContext,
        Rect? bounds)
    {
        var watchedProcesses = new HashSet<int>();
        if (systemContext.ForegroundProcessId > 0) watchedProcesses.Add(systemContext.ForegroundProcessId);
        if (target is not null && target.ProcessId > 0) watchedProcesses.Add(target.ProcessId);

        var semanticState = candidates
            .Where(x => x.Interactable && (watchedProcesses.Count == 0 || watchedProcesses.Contains(x.ProcessId)))
            .OrderByDescending(x => x.Focused)
            .ThenBy(x => x.ProcessId)
            .ThenBy(x => x.ControlType, StringComparer.Ordinal)
            .ThenBy(x => x.AutomationId, StringComparer.Ordinal)
            .ThenBy(x => x.Name, StringComparer.Ordinal)
            .Take(160)
            .Select(x =>
                $"{x.ProcessId}:{x.ControlType}:{x.AutomationId}:{x.Name}:{Bucket(x.X)},{Bucket(x.Y)},{Bucket(x.Width)},{Bucket(x.Height)}:{x.ToggleState}:{x.Selected}:{x.ExpandCollapseState}:{x.Focused}");

        var targetIdentity = target is null
            ? $"{decision.TargetId}:{decision.Key}"
            : $"{target.ProcessId}:{target.ControlType}:{target.AutomationId}:{target.Name}:{target.ClassName}";

        var targetBounds = bounds ?? target?.Bounds;
        var boundsIdentity = targetBounds is { } rect
            ? $"{Bucket(rect.X)},{Bucket(rect.Y)},{Bucket(rect.Width)},{Bucket(rect.Height)}"
            : "none";

        var raw = string.Join(
            "|",
            _activeRequest ?? string.Empty,
            systemContext.ForegroundProcessId,
            systemContext.ForegroundWindowHandle.ToInt64(),
            systemContext.ForegroundProcess ?? string.Empty,
            systemContext.Browser?.Domain ?? string.Empty,
            decision.Action ?? string.Empty,
            targetIdentity,
            boundsIdentity,
            string.Join("\n", semanticState));

        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(raw)));
    }

    private static long Bucket(double value) => (long)Math.Round(value / 16d);
}
