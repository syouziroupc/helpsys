from pathlib import Path


def replace_once(path: str, old: str, new: str) -> None:
    p = Path(path)
    text = p.read_text(encoding='utf-8')
    count = text.count(old)
    if count != 1:
        raise RuntimeError(f'{path}: expected one match, got {count}: {old[:140]!r}')
    p.write_text(text.replace(old, new, 1), encoding='utf-8')


def remove_between(path: str, start_marker: str, end_marker: str) -> None:
    p = Path(path)
    text = p.read_text(encoding='utf-8')
    start = text.index(start_marker)
    end = text.index(end_marker, start)
    p.write_text(text[:start] + text[end:], encoding='utf-8')

# A) Operation verification: never rescan the whole desktop in the 7.5-second verification loop.
v3 = 'src/HelpSys.Desktop/MainWindow.ReliabilityV3.cs'
replace_once(
    v3,
    """                        _stepBaseline = await _scanner.CaptureCandidatesAsync(420, _sessionCts.Token);\n                        if (!_sessionState.IsCurrent(generation)) return;\n                        _stepSystemBaseline = _systemContext.Capture();\n""",
    """                        _stepSystemBaseline = _systemContext.Capture();\n                        if (!HasUsableForeground(_stepSystemBaseline))\n                        {\n                            StopWithMessage(\"現在操作しているアプリを確認できないため、操作結果を推測せず案内を停止しました。もう一度「案内」を押してください。\");\n                            return;\n                        }\n                        _stepBaseline = await _scanner.CaptureCandidatesForProcessAsync(_stepSystemBaseline.ForegroundProcessId, 420, _sessionCts.Token);\n                        if (!_sessionState.IsCurrent(generation)) return;\n""",
)
replace_once(
    v3,
    """        if (_currentTarget is not null)\n        {\n            var fresh = await _scanner.RevalidateCandidateAsync(_currentTarget, cancellationToken);\n""",
    """        if (_currentTarget is not null)\n        {\n            var rootProcessId = _stepSystemBaseline?.ForegroundProcessId ?? 0;\n            var fresh = rootProcessId > 0\n                ? await _scanner.RevalidateCandidateAsync(_currentTarget, rootProcessId, cancellationToken)\n                : await _scanner.RevalidateCandidateAsync(_currentTarget, cancellationToken);\n""",
)
replace_once(
    v3,
    """        var targetBefore = _currentTarget;\n\n        await Task.Delay(action.Equals(\"double_click\", StringComparison.OrdinalIgnoreCase) ? 900 : 450, cancellationToken);\n        var stopwatch = Stopwatch.StartNew();\n\n        while (stopwatch.Elapsed < TimeSpan.FromSeconds(7.5))\n        {\n            cancellationToken.ThrowIfCancellationRequested();\n            var first = await _scanner.CaptureCandidatesAsync(420, cancellationToken);\n            var firstSystem = _systemContext.Capture();\n""",
    """        var targetBefore = _currentTarget;\n        var rootProcessId = systemBefore?.ForegroundProcessId ?? 0;\n\n        await Task.Delay(action.Equals(\"double_click\", StringComparison.OrdinalIgnoreCase) ? 900 : 450, cancellationToken);\n        var stopwatch = Stopwatch.StartNew();\n\n        while (stopwatch.Elapsed < TimeSpan.FromSeconds(7.5))\n        {\n            cancellationToken.ThrowIfCancellationRequested();\n            var first = rootProcessId > 0\n                ? await _scanner.CaptureCandidatesForProcessAsync(rootProcessId, 420, cancellationToken)\n                : [];\n            var firstSystem = _systemContext.Capture();\n""",
)
replace_once(
    v3,
    """            await Task.Delay(strong ? 650 : 400, cancellationToken);\n            var second = await _scanner.CaptureCandidatesAsync(420, cancellationToken);\n            var secondSystem = _systemContext.Capture();\n""",
    """            await Task.Delay(strong ? 650 : 400, cancellationToken);\n            var second = rootProcessId > 0\n                ? await _scanner.CaptureCandidatesForProcessAsync(rootProcessId, 420, cancellationToken)\n                : [];\n            var secondSystem = _systemContext.Capture();\n""",
)
replace_once(
    v3,
    """    private static bool HasSystemTransitionV3(SystemContextSnapshot? before, SystemContextSnapshot after)\n    {\n        if (before is null) return false;\n        if (before.ForegroundProcessId > 0 && after.ForegroundProcessId > 0 && before.ForegroundProcessId != after.ForegroundProcessId) return true;\n""",
    """    private static bool HasSystemTransitionV3(SystemContextSnapshot? before, SystemContextSnapshot after)\n    {\n        if (before is null) return false;\n        // A transient failure to resolve the foreground window is not evidence that an action succeeded.\n        if (after.ForegroundProcessId <= 0 || string.IsNullOrWhiteSpace(after.ForegroundProcess)) return false;\n        if (before.ForegroundProcessId > 0 && after.ForegroundProcessId > 0 && before.ForegroundProcessId != after.ForegroundProcessId) return true;\n""",
)

# B) Main planning/Vision: preserve context-change classification and reject last-moment screen races.
main = 'src/HelpSys.Desktop/MainWindow.xaml.cs'
replace_once(
    main,
    """            var freshTarget = await _scanner.RevalidateCandidateAsync(target, systemContext.ForegroundProcessId, cancellationToken);\n            if (!_sessionState.IsCurrent(generation)) return;\n            if (freshTarget is null)\n""",
    """            var freshTarget = await _scanner.RevalidateCandidateAsync(target, systemContext.ForegroundProcessId, cancellationToken);\n            if (!_sessionState.IsCurrent(generation)) return;\n            if (HasSystemTransitionV3(systemContext, _systemContext.Capture()))\n            {\n                StopWithMessage(\"操作中の画面が切り替わったため、古い案内を表示せず破棄しました。現在の画面で、もう一度「案内」を押してください。\");\n                return;\n            }\n            if (freshTarget is null)\n""",
)
replace_once(
    main,
    """        catch (GuideServiceException)\n        {\n            return false;\n        }\n\n        if (!_sessionState.IsCurrent(generation)) return false;\n""",
    """        catch (GuideServiceException error)\n        {\n            if (_sessionState.IsCurrent(generation)) StopWithGuideFailure(error);\n            return true;\n        }\n\n        if (!_sessionState.IsCurrent(generation)) return false;\n""",
)
replace_once(
    main,
    """        var snapped = await _scanner.SnapToAccessibleBoundsAsync(bounds, cancellationToken);\n        if (!_sessionState.IsCurrent(generation)) return false;\n\n        if (snapped is not { } accessible || accessible.IsEmpty)\n""",
    """        var snapped = await _scanner.SnapToAccessibleBoundsAsync(bounds, cancellationToken);\n        if (!_sessionState.IsCurrent(generation)) return false;\n        if (HasSystemTransitionV3(systemContext, _systemContext.Capture()))\n        {\n            StopWithMessage(\"画像確認中に操作対象の画面が切り替わったため、古い画像案内を表示せず破棄しました。\");\n            return true;\n        }\n\n        if (snapped is not { } accessible || accessible.IsEmpty)\n""",
)
# Remove dead pre-V3 action handlers + old global verification helpers.
remove_between(
    main,
    "    private async void OnObservedLeftClick(Point point)\n",
    "    private static string DefaultInstruction(string action) => action.ToLowerInvariant() switch\n",
)

# C) Vision privacy/performance: bounded, monitor-scoped password preflight; incomplete scan means no upload.
capture = 'src/HelpSys.Desktop/Services/ScreenCaptureService.cs'
replace_once(capture, 'using System.IO;\n', 'using System.Diagnostics;\nusing System.IO;\n')
replace_once(
    capture,
    """        // The ranked guidance candidate list is finite, so it cannot be the privacy boundary.\n        // Scan independently before pixels are copied and again immediately afterwards.\n        var passwordRedactionsBefore = CapturePasswordBounds(cancellationToken);\n        var captureArea = ResolveCaptureArea();\n        if (captureArea.Width <= 0 || captureArea.Height <= 0) throw new InvalidOperationException(\"画面サイズを取得できませんでした。\");\n""",
    """        var captureArea = ResolveCaptureArea();\n        if (captureArea.Width <= 0 || captureArea.Height <= 0) throw new InvalidOperationException(\"画面サイズを取得できませんでした。\");\n\n        // The ranked guidance candidate list is finite, so it cannot be the privacy boundary.\n        // Independently inspect the UIA trees of windows that intersect only the monitor being captured.\n        // If this bounded privacy scan cannot finish, fail closed instead of sending a partial image.\n        var passwordRedactionsBefore = CapturePasswordBounds(captureArea, cancellationToken);\n""",
)
replace_once(capture, 'var passwordRedactionsAfter = CapturePasswordBounds(cancellationToken);', 'var passwordRedactionsAfter = CapturePasswordBounds(captureArea, cancellationToken);')
start = "    private IReadOnlyList<Rect> CapturePasswordBounds(CancellationToken cancellationToken)\n"
end = "    private static BitmapSource ScaleToLimit(BitmapSource source)\n"
p = Path(capture)
text = p.read_text(encoding='utf-8')
s = text.index(start)
e = text.index(end, s)
new_method = r'''    private IReadOnlyList<Rect> CapturePasswordBounds(CaptureArea captureArea, CancellationToken cancellationToken)
    {
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            var captureRect = new Rect(captureArea.X, captureArea.Y, captureArea.Width, captureArea.Height);
            var walker = TreeWalker.ControlViewWalker;
            var queue = new Queue<AutomationElement>();
            var roots = AutomationElement.RootElement.FindAll(TreeScope.Children, System.Windows.Automation.Condition.TrueCondition);

            foreach (AutomationElement root in roots)
            {
                cancellationToken.ThrowIfCancellationRequested();
                try
                {
                    var current = root.Current;
                    if (current.ProcessId == _selfProcessId || current.IsOffscreen) continue;
                    var bounds = current.BoundingRectangle;
                    if (!bounds.IsEmpty && captureRect.IntersectsWith(bounds)) queue.Enqueue(root);
                }
                catch (ElementNotAvailableException) { }
            }

            const int maxVisited = 12000;
            var stopwatch = Stopwatch.StartNew();
            var visited = 0;
            var result = new List<Rect>();

            while (queue.Count > 0)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (++visited > maxVisited || stopwatch.Elapsed > TimeSpan.FromSeconds(1.8))
                    throw new InvalidOperationException("パスワード欄の安全確認を規定範囲内で完了できませんでした。");

                var element = queue.Dequeue();
                try
                {
                    var current = element.Current;
                    if (current.ProcessId != _selfProcessId && !current.IsOffscreen)
                    {
                        var bounds = current.BoundingRectangle;
                        if (current.IsPassword && !bounds.IsEmpty && captureRect.IntersectsWith(bounds)) result.Add(bounds);
                    }

                    var child = walker.GetFirstChild(element);
                    while (child is not null)
                    {
                        queue.Enqueue(child);
                        child = walker.GetNextSibling(child);
                    }
                }
                catch (ElementNotAvailableException) { }
            }

            return result;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException("パスワード欄を安全に確認できないため、画面画像は送信しません。", ex);
        }
    }

'''
p.write_text(text[:s] + new_method + text[e:], encoding='utf-8')
# Remove virtual-screen leftovers now that all-monitor fallback is forbidden.
for line in [
    '    private const int SmXVirtualScreen = 76;\n',
    '    private const int SmYVirtualScreen = 77;\n',
    '    private const int SmCxVirtualScreen = 78;\n',
    '    private const int SmCyVirtualScreen = 79;\n',
    '    [DllImport("user32.dll")]\n    private static extern int GetSystemMetrics(int index);\n\n',
]:
    text = p.read_text(encoding='utf-8')
    if line in text:
        p.write_text(text.replace(line, '', 1), encoding='utf-8')

# D) Root app-state semantics: only foreground == opened for a simple "open app" goal.
wk = 'worker/windows-knowledge.js'
replace_once(
    wk,
    """function buildAppTask(app, goal, elements, systemContext, knowledge) {\n  const running = isAppRunning(app, elements, systemContext);\n  const justOpen = isSimpleOpenGoal(goal, app);\n  if (running && justOpen) {\n""",
    """function buildAppTask(app, goal, elements, systemContext, knowledge) {\n  const running = isAppRunning(app, elements, systemContext);\n  const foreground = String(systemContext?.foregroundProcess || '').toLowerCase();\n  const foregroundApp = app.processes.some(process => process.toLowerCase() === foreground);\n  const justOpen = isSimpleOpenGoal(goal, app);\n  if (foregroundApp && justOpen) {\n""",
)
replace_once(
    wk,
    """  if (running) {\n    return task('launch-app', `${knowledge}\\n\\n${app.display}は既に開いている。前面画面を確認して目的の続きへ進む。`, null, false, null, app, true);\n  }\n\n""",
    """  if (foregroundApp) {\n    return task('launch-app', `${knowledge}\\n\\n${app.display}は現在前面で開いている。前面画面を確認して目的の続きへ進む。`, null, false, null, app, true);\n  }\n\n""",
)
# Keep `running` available to existing downstream knowledge/heuristics; it must not determine completion.

# E) Worker hard guard: AI must never ask users to type secrets into HelpSys clarification.
guard = 'worker/reliability-v4-guard.js'
p = Path(guard)
text = p.read_text(encoding='utf-8')
text = text.replace(
    """    const override = preventBackgroundDone(body, decision);\n    return override ? replaceJson(response, override) : response;\n""",
    """    const secretOverride = guardSecretClarification(decision);\n    if (secretOverride) return replaceJson(response, secretOverride);\n\n    const override = isStructuredGuide(request) ? preventBackgroundDone(body, decision) : null;\n    return override ? replaceJson(response, override) : response;\n""",
    1,
)
# Rewrite fetch preamble so both structured and vision responses can be reviewed.
text = text.replace(
    """  async fetch(request, env, ctx) {\n    let bodyPromise = null;\n    try {\n      const url = new URL(request.url);\n      if (request.method === 'POST' && url.pathname === '/v1/guide') bodyPromise = request.clone().json();\n    } catch { }\n""",
    """  async fetch(request, env, ctx) {\n    let bodyPromise = null;\n    try {\n      const url = new URL(request.url);\n      if (request.method === 'POST' && (url.pathname === '/v1/guide' || url.pathname === '/v1/vision-guide'))\n        bodyPromise = request.clone().json();\n    } catch { }\n""",
    1,
)
insert = r'''
export function guardSecretClarification(decision) {
  if (!decision || String(decision.status || '').toLowerCase() !== 'clarify') return null;
  const question = String(decision.question || decision.instruction || '');
  const secret = /(password|passcode|パスワード|暗証|\bpin\b|otp|ワンタイム|認証コード|verification\s*code|recovery\s*key|リカバリ(?:ー)?キー|秘密鍵|secret\s*key|cvv|cvc|セキュリティコード)/i;
  if (!secret.test(question)) return null;
  return {
    status: 'not_found', targetId: null, action: 'none',
    instruction: 'パスワード、暗証番号、認証コードなどの秘密情報はHelpSysへ入力しないでください。秘密情報そのものを聞かずに続けられる画面から案内をやり直します。',
    question: null, key: null, confidence: 0
  };
}

function isStructuredGuide(request) {
  try { return new URL(request.url).pathname === '/v1/guide'; } catch { return false; }
}

'''
marker = 'function preventBackgroundDone(body, decision) {'
text = text.replace(marker, insert + marker, 1)
p.write_text(text, encoding='utf-8')

selftest = 'worker/reliability-v4-selftest.mjs'
p = Path(selftest)
text = p.read_text(encoding='utf-8')
text = text.replace("import guard from './reliability-v4-guard.js';", "import guard, { guardSecretClarification } from './reliability-v4-guard.js';", 1)
text += r'''

const secretClarify = guardSecretClarification({
  status: 'clarify', question: 'パスワードを入力してください。', instruction: '', confidence: 0.9
});
assert(secretClarify?.status === 'not_found', 'secret clarification must be blocked');
const normalClarify = guardSecretClarification({
  status: 'clarify', question: 'どのフォルダーを開きたいですか？', instruction: '', confidence: 0.9
});
assert(normalClarify === null, 'ordinary clarification must remain allowed');
console.log('reliability v4 secret-clarification self-test passed.');
'''
p.write_text(text, encoding='utf-8')

# F) Remove dead global target-finder path from scanner. It no longer has any caller.
scanner = 'src/HelpSys.Desktop/Services/UiAutomationScanner.cs'
p = Path(scanner)
text = p.read_text(encoding='utf-8')
text = text.replace("""    public Task<UiTarget?> FindBestTargetAsync(IReadOnlyList<string> hints, CancellationToken cancellationToken = default)\n        => Task.Run(() => FindBestTarget(hints, cancellationToken), cancellationToken);\n\n""", '', 1)
s = text.index('    private UiTarget? FindBestTarget(IReadOnlyList<string> hints, CancellationToken cancellationToken)\n')
e = text.index('    private static bool IsInteractiveType(string typeName) =>\n', s)
text = text[:s] + text[e:]
# Score is now dead too.
s = text.index('    private static double Score(string? name, string? automationId, string? className, string controlType, Rect rect, IReadOnlyList<string> hints)\n')
e = text.index('    private readonly record struct ActionState', s)
text = text[:s] + text[e:]
p.write_text(text, encoding='utf-8')

# Remove dead model types associated only with the deleted offline/global targeting path.
for path in [
    'src/HelpSys.Desktop/Models/UiTarget.cs',
    'src/HelpSys.Desktop/Models/GuidePlan.cs',
    'src/HelpSys.Desktop/Models/GuideStep.cs',
]:
    Path(path).unlink(missing_ok=True)

print('Reliability v4 final hardening applied.')
