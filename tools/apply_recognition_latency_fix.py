from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]


def read(rel):
    return (ROOT / rel).read_text(encoding="utf-8")


def write(rel, text):
    (ROOT / rel).write_text(text, encoding="utf-8", newline="\n")


def replace_once(text, old, new, label):
    count = text.count(old)
    if count != 1:
        raise RuntimeError(f"{label}: expected exactly 1 match, found {count}")
    return text.replace(old, new, 1)


def replace_all_checked(text, old, new, minimum, label):
    count = text.count(old)
    if count < minimum:
        raise RuntimeError(f"{label}: expected at least {minimum} matches, found {count}")
    return text.replace(old, new), count


# 1) Richer visible-text evidence from Windows UI Automation / TextPattern.
path = "src/HelpSys.Desktop/Services/UiAutomationScanner.cs"
s = read(path)
s = replace_once(
    s,
    "while (queue.Count > 0 && visited < 7500 && stopwatch.ElapsedMilliseconds < 2700)",
    "while (queue.Count > 0 && visited < 7000 && stopwatch.ElapsedMilliseconds < 2200)",
    "scanner bounded latency")
s = replace_once(
    s,
    """                    var rawName = current.Name ?? string.Empty;\n                    var name = isPassword ? \"[password field]\" : isInput ? \"[input field]\" : rawName;\n                    var automationId = current.AutomationId ?? string.Empty;""",
    """                    var rawName = current.Name ?? string.Empty;\n                    var readableText = isPassword || isInput\n                        ? rawName\n                        : ReadBoundedVisibleText(element, typeName, rawName);\n                    var name = isPassword ? \"[password field]\" : isInput ? \"[input field]\" : readableText;\n                    var automationId = current.AutomationId ?? string.Empty;""",
    "visible TextPattern evidence")
s = replace_once(
    s,
    """                        context.Add(new UiElementCandidate(\n                            $\"c{context.Count + 1}\", Trim(name, 180), Trim(automationId, 120), Trim(className, 120),""",
    """                        context.Add(new UiElementCandidate(\n                            $\"c{context.Count + 1}\", Trim(name, 420), Trim(automationId, 120), Trim(className, 120),""",
    "context text budget")
insert_anchor = "    private static ActionState ReadActionState(AutomationElement element, string typeName, bool isPassword)\n"
helper = r'''    private static string ReadBoundedVisibleText(AutomationElement element, string typeName, string fallback)
    {
        var normalizedFallback = NormalizeReadableText(fallback, 420);
        var textBearing = typeName.EndsWith("Document", StringComparison.Ordinal) ||
                          typeName.EndsWith("Text", StringComparison.Ordinal) ||
                          typeName.EndsWith("DataItem", StringComparison.Ordinal) ||
                          typeName.EndsWith("Table", StringComparison.Ordinal) ||
                          typeName.EndsWith("List", StringComparison.Ordinal) ||
                          typeName.EndsWith("Pane", StringComparison.Ordinal) ||
                          typeName.EndsWith("Group", StringComparison.Ordinal);
        if (!textBearing) return normalizedFallback;

        try
        {
            if (element.TryGetCurrentPattern(TextPattern.Pattern, out var pattern) && pattern is TextPattern textPattern)
            {
                var extracted = NormalizeReadableText(textPattern.DocumentRange.GetText(520), 420);
                if (!string.IsNullOrWhiteSpace(extracted))
                {
                    if (string.IsNullOrWhiteSpace(normalizedFallback)) return extracted;
                    if (extracted.Equals(normalizedFallback, StringComparison.OrdinalIgnoreCase)) return normalizedFallback;
                    return NormalizeReadableText($"{normalizedFallback} | {extracted}", 420);
                }
            }
        }
        catch (ElementNotAvailableException) { }
        catch (InvalidOperationException) { }
        catch (NotSupportedException) { }

        return normalizedFallback;
    }

    private static string NormalizeReadableText(string? value, int max)
    {
        if (string.IsNullOrWhiteSpace(value)) return string.Empty;
        var normalized = string.Join(' ', value.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        return normalized.Length <= max ? normalized : normalized[..max];
    }

'''
if insert_anchor not in s:
    raise RuntimeError("TextPattern helper anchor not found")
s = s.replace(insert_anchor, helper + insert_anchor, 1)
s = replace_once(
    s,
    """        return typeName.EndsWith(\"Text\", StringComparison.Ordinal) || typeName.EndsWith(\"Window\", StringComparison.Ordinal) ||\n               typeName.EndsWith(\"Pane\", StringComparison.Ordinal) || typeName.EndsWith(\"Group\", StringComparison.Ordinal) ||\n               typeName.EndsWith(\"TitleBar\", StringComparison.Ordinal) || typeName.EndsWith(\"Document\", StringComparison.Ordinal);""",
    """        return typeName.EndsWith(\"Text\", StringComparison.Ordinal) || typeName.EndsWith(\"Window\", StringComparison.Ordinal) ||\n               typeName.EndsWith(\"Pane\", StringComparison.Ordinal) || typeName.EndsWith(\"Group\", StringComparison.Ordinal) ||\n               typeName.EndsWith(\"TitleBar\", StringComparison.Ordinal) || typeName.EndsWith(\"Document\", StringComparison.Ordinal) ||\n               typeName.EndsWith(\"DataItem\", StringComparison.Ordinal) || typeName.EndsWith(\"Table\", StringComparison.Ordinal) ||\n               typeName.EndsWith(\"Header\", StringComparison.Ordinal) || typeName.EndsWith(\"HeaderItem\", StringComparison.Ordinal) ||\n               typeName.EndsWith(\"List\", StringComparison.Ordinal);""",
    "Office/context control types")
s = replace_once(
    s,
    """        if (process is \"chrome\" or \"msedge\" or \"firefox\" or \"brave\" or \"opera\" or \"vivaldi\") score += 320;\n        if (item.ControlType == \"Edit\") score += 160;\n        if (!string.IsNullOrWhiteSpace(item.Name)) score += 45;""",
    """        if (process is \"chrome\" or \"msedge\" or \"firefox\" or \"brave\" or \"opera\" or \"vivaldi\") score += 320;\n        if (process.Equals(\"excel\", StringComparison.OrdinalIgnoreCase) ||\n            process.Equals(\"winword\", StringComparison.OrdinalIgnoreCase) ||\n            process.Equals(\"powerpnt\", StringComparison.OrdinalIgnoreCase)) score += 240;\n        if (item.ControlType is \"Document\" or \"Text\" or \"DataItem\" or \"Hyperlink\") score += 180;\n        if (item.ControlType == \"Edit\") score += 160;\n        if (!string.IsNullOrWhiteSpace(item.Name)) score += 45;""",
    "context ranking")
write(path, s)

# 2) Preserve more sanitized non-input text in cloud payloads.
path = "src/HelpSys.Desktop/Services/PrivacyGate.cs"
s = read(path)
s = replace_once(
    s,
    "name = SanitizeOutboundText(x.Name, 140),",
    "name = SanitizeOutboundText(x.Name, x.Interactable ? 180 : 360),",
    "privacy compact visible text")
write(path, s)

# 3) Faster cloud failure behavior and tolerate same-domain browser navigation while planning.
path = "src/HelpSys.Desktop/Services/CloudGuideService.cs"
s = read(path)
s = replace_once(s,
    "private static readonly TimeSpan AttemptTimeout = TimeSpan.FromSeconds(9);",
    "private static readonly TimeSpan AttemptTimeout = TimeSpan.FromSeconds(6);",
    "cloud attempt timeout")
s = replace_once(s,
    "for (var attempt = 0; attempt < 2; attempt++)",
    "for (var attempt = 0; attempt < 1; attempt++)",
    "single bounded cloud attempt")
s = replace_once(s,
    "案内モデルの応答が9秒を超えました。",
    "案内モデルの応答が6秒を超えました。",
    "timeout message")
s = replace_once(
    s,
    """        var expectedUrl = expected.Browser?.Url ?? string.Empty;\n        var currentUrl = current.Browser?.Url ?? string.Empty;\n        var browserChanged = !string.IsNullOrWhiteSpace(expectedUrl) && !string.IsNullOrWhiteSpace(currentUrl) &&\n                             !expectedUrl.Equals(currentUrl, StringComparison.OrdinalIgnoreCase);""",
    """        var expectedDomain = expected.Browser?.Domain ?? string.Empty;\n        var currentDomain = current.Browser?.Domain ?? string.Empty;\n        var browserChanged = !string.IsNullOrWhiteSpace(expectedDomain) && !string.IsNullOrWhiteSpace(currentDomain) &&\n                             !expectedDomain.Equals(currentDomain, StringComparison.OrdinalIgnoreCase);""",
    "same-domain browser planning")
s = replace_once(
    s,
    """        var contextBudget = systemContext.Browser is null ? 45 : 80;\n        var interactiveBudget = 280 - contextBudget;""",
    """        var office = foregroundName.Equals(\"excel\", StringComparison.OrdinalIgnoreCase) ||\n                     foregroundName.Equals(\"winword\", StringComparison.OrdinalIgnoreCase) ||\n                     foregroundName.Equals(\"powerpnt\", StringComparison.OrdinalIgnoreCase);\n        var contextBudget = systemContext.Browser is not null ? 110 : office ? 100 : 60;\n        var interactiveBudget = 280 - contextBudget;""",
    "context selection budget")
s = replace_once(
    s,
    """            \"Document\" => 90,\n            \"Text\" => 80,\n            \"Group\" => 60,""",
    """            \"Document\" => 120,\n            \"Text\" => 105,\n            \"DataItem\" => 100,\n            \"Table\" => 90,\n            \"Hyperlink\" => 90,\n            \"Group\" => 60,""",
    "context priority")
write(path, s)

# 4) Do not treat same-window content/title updates as capture identity loss.
path = "src/HelpSys.Desktop/MainWindow.QualityFirst.cs"
s = read(path)
replacements = [
    ("!HasSameCaptureIdentity(systemContext, afterCaptureContext) || HasSystemTransitionV3(systemContext, afterCaptureContext)", "!HasSameCaptureIdentity(systemContext, afterCaptureContext)"),
    ("!HasSameCaptureIdentity(systemContext, postPlanContext) || HasSystemTransitionV3(systemContext, postPlanContext)", "!HasSameCaptureIdentity(systemContext, postPlanContext)"),
    ("!HasSameCaptureIdentity(systemContext, prePresentContext) || HasSystemTransitionV3(systemContext, prePresentContext)", "!HasSameCaptureIdentity(systemContext, prePresentContext)"),
    ("!HasSameCaptureIdentity(expectedContext, currentBeforeScan) || HasSystemTransitionV3(expectedContext, currentBeforeScan)", "!HasSameCaptureIdentity(expectedContext, currentBeforeScan)"),
    ("!HasSameCaptureIdentity(expectedContext, currentAfterScan) || HasSystemTransitionV3(expectedContext, currentAfterScan)", "!HasSameCaptureIdentity(expectedContext, currentAfterScan)"),
    ("!HasSameCaptureIdentity(expectedContext, currentAfterPlan) || HasSystemTransitionV3(expectedContext, currentAfterPlan)", "!HasSameCaptureIdentity(expectedContext, currentAfterPlan)"),
    ("!HasSameCaptureIdentity(expectedContext, currentBeforePresent) || HasSystemTransitionV3(expectedContext, currentBeforePresent)", "!HasSameCaptureIdentity(expectedContext, currentBeforePresent)"),
    ("!HasSameCaptureIdentity(systemContext, currentContext) || HasSystemTransitionV3(systemContext, currentContext)", "!HasSameCaptureIdentity(systemContext, currentContext)"),
]
for i, (old, new) in enumerate(replacements, 1):
    if old not in s:
        raise RuntimeError(f"quality identity guard {i} not found")
    s = s.replace(old, new, 1)

# Add structured/text-first planning before the expensive screenshot path.
anchor = """            var structuralEvidence = GuidanceEvidenceService.Build(false, candidates, _history, systemContext);\n            SetState(GuidanceEvidenceService.BuildProgressText(structuralEvidence), speak: false);\n\n            ScreenCaptureFrame frame;"""
replacement = """            var structuralEvidence = GuidanceEvidenceService.Build(false, candidates, _history, systemContext);\n            SetState(GuidanceEvidenceService.BuildProgressText(structuralEvidence), speak: false);\n\n            if (candidates.Count > 0)\n            {\n                if (!_sessionState.TryTransition(generation, GuidanceSessionState.Planning)) return;\n                if (await TryFastStructuredPlanAsync(candidates, systemContext, generation, cancellationToken)) return;\n                if (!_sessionState.TryTransition(generation, GuidanceSessionState.Capturing)) return;\n            }\n\n            ScreenCaptureFrame frame;"""
s = replace_once(s, anchor, replacement, "structured-first insertion")

helper_anchor = "    private async Task<bool> TryStructuredFallbackAsync(\n"
fast_helper = r'''    private async Task<bool> TryFastStructuredPlanAsync(
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

'''
if helper_anchor not in s:
    raise RuntimeError("structured helper anchor not found")
s = s.replace(helper_anchor, fast_helper + helper_anchor, 1)

# When both quality and structured fallback fail transiently, re-observe instead of killing the goal.
old = """                if (_sessionState.IsCurrent(generation) && await TryStructuredFallbackAsync(candidates, systemContext, generation, cancellationToken)) return;\n                if (_sessionState.IsCurrent(generation)) StopWithGuideFailure(error);\n                return;"""
new = """                if (_sessionState.IsCurrent(generation) && await TryStructuredFallbackAsync(candidates, systemContext, generation, cancellationToken)) return;\n                if (_sessionState.IsCurrent(generation) && error.Kind is GuideFailureKind.Network or GuideFailureKind.ServiceUnavailable or GuideFailureKind.InvalidResponse)\n                {\n                    HandleTechnicalPlanningUncertainty(\"案内サービスの一時的な応答失敗\", generation);\n                    return;\n                }\n                if (_sessionState.IsCurrent(generation)) StopWithGuideFailure(error);\n                return;"""
s = replace_once(s, old, new, "transient guide recovery")
write(path, s)

# 5) Live watcher: normal focus/document/search churn is not a hard route change.
path = "src/HelpSys.Desktop/MainWindow.StableGuidance.cs"
s = read(path)
insert = """            var hardChange = HasHardStableLiveChange(_liveSystem, nowSystem);\n            var semanticChange = HasSemanticLiveStateChanged(_liveElements, _liveSystem, nowElements, nowSystem);\n            var topologyChange = semanticChange || HasStableLiveTopologyChanged(_liveElements, _liveSystem, nowElements, nowSystem);\n\n            if (!hardChange && !topologyChange)"""
replace = """            var hardChange = HasHardStableLiveChange(_liveSystem, nowSystem);\n            var semanticChange = HasSemanticLiveStateChanged(_liveElements, _liveSystem, nowElements, nowSystem);\n            var topologyChange = semanticChange || HasStableLiveTopologyChanged(_liveElements, _liveSystem, nowElements, nowSystem);\n\n            // During planning, same-app focus/content churn is expected on browsers and Office.\n            // Keep the newest baseline but do not cancel an in-flight decision unless ownership/domain changed.\n            if (_sessionState.PlannerInFlight && !hardChange)\n            {\n                _liveElements = nowElements;\n                _liveSystem = nowSystem;\n                ClearStableLiveChangeCandidate();\n                return;\n            }\n\n            if (!hardChange && !topologyChange)"""
s = replace_once(s, insert, replace, "live planner churn guard")
s = replace_once(
    s,
    "SetState(\"操作中の画面が切り替わったため、古い案内を破棄しました。新しい画面が落ち着いてから案内を作り直します…\", speak: false);",
    "SetState(\"画面の内容が更新されたため、現在の状態を確認し直しています…\", speak: false);",
    "neutral live replan wording")
s = replace_once(
    s,
    """        var beforeUrl = before.Browser?.Url ?? string.Empty;\n        var afterUrl = after.Browser?.Url ?? string.Empty;\n        return !beforeUrl.Equals(afterUrl, StringComparison.OrdinalIgnoreCase) &&\n               (!string.IsNullOrWhiteSpace(beforeUrl) || !string.IsNullOrWhiteSpace(afterUrl));""",
    """        var beforeDomain = before.Browser?.Domain ?? string.Empty;\n        var afterDomain = after.Browser?.Domain ?? string.Empty;\n        return !string.IsNullOrWhiteSpace(beforeDomain) && !string.IsNullOrWhiteSpace(afterDomain) &&\n               !beforeDomain.Equals(afterDomain, StringComparison.OrdinalIgnoreCase);""",
    "live same-domain navigation")
s = replace_once(s,
    "if (Math.Abs(before.Count - after.Count) >= 10) return true;",
    "if (Math.Abs(before.Count - after.Count) >= 18) return true;",
    "live topology count threshold")
s = replace_once(s,
    "return similarity < 0.72;",
    "return similarity < 0.58;",
    "live topology similarity threshold")
s = replace_once(
    s,
    ".Where(x => x.Interactable || x.ControlType is \"Window\" or \"Pane\" or \"Document\" or \"Text\")",
    ".Where(x => x.Interactable || x.ControlType is \"Window\" or \"Pane\")",
    "live topology ignores document text churn")
s = replace_once(
    s,
    """    private static string SemanticLiveState(UiElementCandidate x) => x.Interactable\n        ? $\"focus={x.Focused};toggle={x.ToggleState ?? string.Empty};selected={x.Selected?.ToString() ?? string.Empty};expand={x.ExpandCollapseState ?? string.Empty}\"\n        : string.Empty;""",
    """    private static string SemanticLiveState(UiElementCandidate x) => x.Interactable\n        ? $\"toggle={x.ToggleState ?? string.Empty};selected={x.Selected?.ToString() ?? string.Empty};expand={x.ExpandCollapseState ?? string.Empty}\"\n        : string.Empty;""",
    "focus is not semantic route change")
write(path, s)

# 6) Shorter, useful transient-service wording for any legacy path still calling StopWithGuideFailure.
path = "src/HelpSys.Desktop/MainWindow.xaml.cs"
s = read(path)
s = replace_once(s,
    "GuideFailureKind.Network => \"ネットワークへの接続を確認できませんでした。画面認識の失敗とは区別して、案内を停止します。\",",
    "GuideFailureKind.Network => \"案内サービスへの通信に一時的に失敗しました。もう一度「案内」を押すと現在の目的から再開できます。\",",
    "network wording")
s = replace_once(s,
    "GuideFailureKind.ServiceUnavailable => \"案内サービスが一時的に応答できませんでした。通信回線が切れているとは断定せず、案内を停止します。\",",
    "GuideFailureKind.ServiceUnavailable => \"案内サービスの応答が一時的に遅れています。もう一度「案内」を押すと現在の目的から再開できます。\",",
    "service wording")
write(path, s)

# 7) Let the workers retain bounded visible text and explicitly treat it as current-screen evidence.
for path in ["worker/index.js", "worker/quality-guide.js"]:
    s = read(path)
    old = "name: text(value.name, 180),"
    if old not in s:
        raise RuntimeError(f"{path}: compact name budget anchor missing")
    s = s.replace(old, "name: text(value.name, 360),", 1)
    write(path, s)

path = "worker/quality-guide.js"
s = read(path)
s = replace_once(
    s,
    "- uiElements: evidence for control identity, Name, AutomationId, ControlType, input-presence/state, focus, actionability and exact Windows bounds. Raw input values are intentionally unavailable.",
    "- uiElements: evidence for control identity, Name, AutomationId, ControlType, input-presence/state, focus, actionability and exact Windows bounds. Context-only Text/Document/DataItem nodes may also contain a bounded snapshot of visible screen text from Windows accessibility APIs. Raw secret input values are intentionally unavailable.",
    "quality prompt visible text")
write(path, s)

# 8) Add source-level regression contract for the exact failures reported in the field.
test = r'''import fs from 'node:fs';

const scanner = fs.readFileSync('src/HelpSys.Desktop/Services/UiAutomationScanner.cs', 'utf8');
const cloud = fs.readFileSync('src/HelpSys.Desktop/Services/CloudGuideService.cs', 'utf8');
const quality = fs.readFileSync('src/HelpSys.Desktop/MainWindow.QualityFirst.cs', 'utf8');
const live = fs.readFileSync('src/HelpSys.Desktop/MainWindow.StableGuidance.cs', 'utf8');
const main = fs.readFileSync('src/HelpSys.Desktop/MainWindow.xaml.cs', 'utf8');
const privacy = fs.readFileSync('src/HelpSys.Desktop/Services/PrivacyGate.cs', 'utf8');

function must(cond, msg) { if (!cond) throw new Error(msg); }

must(scanner.includes('TextPattern.Pattern') && scanner.includes('ReadBoundedVisibleText'), 'visible TextPattern evidence must be collected');
must(scanner.includes('DataItem') && scanner.includes('Document'), 'Office/document context types must be retained');
must(privacy.includes('x.Interactable ? 180 : 360'), 'sanitized context text budget must be larger than control labels');
must(cloud.includes('TimeSpan.FromSeconds(6)') && cloud.includes('attempt < 1'), 'cloud attempt latency must be bounded');
must(cloud.includes('expected.Browser?.Domain') && !cloud.includes('var expectedUrl = expected.Browser?.Url'), 'planning staleness must use browser domain, not same-domain URL churn');
must(quality.includes('TryFastStructuredPlanAsync'), 'fast structured/text-first planning path must exist');
must(!quality.includes('!HasSameCaptureIdentity(systemContext, afterCaptureContext) || HasSystemTransitionV3(systemContext, afterCaptureContext)'), 'same-window content transitions must not invalidate capture identity');
must(live.includes('_sessionState.PlannerInFlight && !hardChange'), 'live same-app churn must not cancel an in-flight planner');
must(live.includes('before.Browser?.Domain') && !live.includes('var beforeUrl = before.Browser?.Url'), 'live hard change must not use same-domain URL changes');
must(!live.includes('focus={x.Focused}'), 'ordinary focus movement must not be treated as semantic route change');
must(!main.includes('通信回線が切れているとは断定せず'), 'confusing service failure wording must be removed');
console.log('recognition/latency/state regression contract passed');
'''
write("tests/recognition-latency-contract.mjs", test)

print("recognition/latency/state patch applied")
