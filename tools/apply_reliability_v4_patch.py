from pathlib import Path


def replace_once(path: str, old: str, new: str) -> None:
    p = Path(path)
    text = p.read_text(encoding="utf-8")
    count = text.count(old)
    if count != 1:
        raise RuntimeError(f"{path}: expected exactly one match, got {count}: {old[:120]!r}")
    p.write_text(text.replace(old, new, 1), encoding="utf-8")


# 1) Scope recurring UIA scans and candidate revalidation to one process subtree.
scanner = "src/HelpSys.Desktop/Services/UiAutomationScanner.cs"
replace_once(
    scanner,
    """    public Task<IReadOnlyList<UiElementCandidate>> CaptureCandidatesAsync(int maxCandidates = 360, CancellationToken cancellationToken = default)\n        => Task.Run(() => CaptureCandidates(maxCandidates, cancellationToken), cancellationToken);\n\n""",
    """    public Task<IReadOnlyList<UiElementCandidate>> CaptureCandidatesAsync(int maxCandidates = 360, CancellationToken cancellationToken = default)\n        => Task.Run(() => CaptureCandidates(maxCandidates, cancellationToken), cancellationToken);\n\n    public Task<IReadOnlyList<UiElementCandidate>> CaptureCandidatesForProcessAsync(int processId, int maxCandidates = 360, CancellationToken cancellationToken = default)\n    {\n        if (processId <= 0) return Task.FromResult<IReadOnlyList<UiElementCandidate>>([]);\n        return Task.Run(() => CaptureCandidates(maxCandidates, cancellationToken, processId), cancellationToken);\n    }\n\n""",
)
replace_once(
    scanner,
    "var current = CaptureCandidates(700, cancellationToken).Where(x => x.Interactable).ToArray();",
    "var current = CaptureCandidates(700, cancellationToken, candidate.ProcessId > 0 ? candidate.ProcessId : null).Where(x => x.Interactable).ToArray();",
)
replace_once(
    scanner,
    """        var candidates = CaptureCandidates(700, cancellationToken)\n            .Where(x => x.Interactable && !x.Bounds.IsEmpty && x.ProcessId == visibleProcessId)\n""",
    """        var candidates = CaptureCandidates(700, cancellationToken, visibleProcessId)\n            .Where(x => x.Interactable && !x.Bounds.IsEmpty && x.ProcessId == visibleProcessId)\n""",
)
replace_once(
    scanner,
    """    private IReadOnlyList<UiElementCandidate> CaptureCandidates(int maxCandidates, CancellationToken cancellationToken)\n    {\n        var root = AutomationElement.RootElement;\n        var walker = TreeWalker.ControlViewWalker;\n        var queue = new Queue<(AutomationElement Element, int Depth)>();\n        EnqueueChildren(walker, root, 0, queue);\n""",
    """    private IReadOnlyList<UiElementCandidate> CaptureCandidates(int maxCandidates, CancellationToken cancellationToken, int? rootProcessId = null)\n    {\n        var root = AutomationElement.RootElement;\n        var walker = TreeWalker.ControlViewWalker;\n        var queue = new Queue<(AutomationElement Element, int Depth)>();\n        if (rootProcessId is > 0) EnqueueProcessRoots(rootProcessId.Value, queue);\n        else EnqueueChildren(walker, root, 0, queue);\n""",
)
replace_once(
    scanner,
    """    private static void EnqueueChildren(TreeWalker walker, AutomationElement parent, int depth, Queue<(AutomationElement Element, int Depth)> queue)\n    {\n""",
    """    private static void EnqueueProcessRoots(int processId, Queue<(AutomationElement Element, int Depth)> queue)\n    {\n        try\n        {\n            var condition = new PropertyCondition(AutomationElement.ProcessIdProperty, processId);\n            var roots = AutomationElement.RootElement.FindAll(TreeScope.Children, condition);\n            foreach (AutomationElement root in roots) queue.Enqueue((root, 0));\n        }\n        catch (ElementNotAvailableException) { }\n        catch (InvalidOperationException) { }\n    }\n\n    private static void EnqueueChildren(TreeWalker walker, AutomationElement parent, int depth, Queue<(AutomationElement Element, int Depth)> queue)\n    {\n""",
)

# 2) Heartbeat/live monitoring must not rescan the entire desktop. Also detect PID switches.
stable = "src/HelpSys.Desktop/MainWindow.StableGuidance.cs"
replace_once(
    stable,
    """            var token = _sessionCts.IsCancellationRequested ? CancellationToken.None : _sessionCts.Token;\n            IReadOnlyList<UiElementCandidate> nowElements;\n            try { nowElements = await _scanner.CaptureCandidatesAsync(320, token); }\n            catch (OperationCanceledException) { return; }\n            var nowSystem = _systemContext.Capture();\n            await _liveWatcher.SetForegroundProcessAsync(nowSystem.ForegroundProcessId, token);\n""",
    """            var token = _sessionCts.IsCancellationRequested ? CancellationToken.None : _sessionCts.Token;\n            var nowSystem = _systemContext.Capture();\n            if (!HasUsableForeground(nowSystem))\n            {\n                ClearStableLiveChangeCandidate();\n                await _liveWatcher.SetForegroundProcessAsync(0, token);\n                return;\n            }\n\n            await _liveWatcher.SetForegroundProcessAsync(nowSystem.ForegroundProcessId, token);\n            IReadOnlyList<UiElementCandidate> nowElements;\n            try { nowElements = await _scanner.CaptureCandidatesForProcessAsync(nowSystem.ForegroundProcessId, 320, token); }\n            catch (OperationCanceledException) { return; }\n\n            var afterScanSystem = _systemContext.Capture();\n            if (HasHardStableLiveChange(nowSystem, afterScanSystem))\n            {\n                ClearStableLiveChangeCandidate();\n                _liveElements = [];\n                _liveSystem = null;\n                if (_sessionState.PlannerInFlight) InvalidatePlannerForLiveContextChange();\n                return;\n            }\n            nowSystem = afterScanSystem;\n""",
)
replace_once(
    stable,
    """    private static bool HasHardStableLiveChange(SystemContextSnapshot before, SystemContextSnapshot after)\n    {\n        if (!before.ForegroundProcess.Equals(after.ForegroundProcess, StringComparison.OrdinalIgnoreCase)) return true;\n""",
    """    private static bool HasHardStableLiveChange(SystemContextSnapshot before, SystemContextSnapshot after)\n    {\n        if (before.ForegroundProcessId > 0 && after.ForegroundProcessId > 0 && before.ForegroundProcessId != after.ForegroundProcessId) return true;\n        if (!before.ForegroundProcess.Equals(after.ForegroundProcess, StringComparison.OrdinalIgnoreCase)) return true;\n""",
)
replace_once(
    stable,
    """    private void ClearStableLiveChangeCandidate()\n    {\n""",
    """    private void InvalidatePlannerForLiveContextChange()\n    {\n        if (!_sessionState.PlannerInFlight) return;\n        _sessionState.Invalidate(GuidanceSessionState.Idle);\n        _speechOutput.Stop();\n        InvalidateCurrentGuidanceForLiveChange();\n        _liveRestartAfterPlanCancel = true;\n        try { _sessionCts?.Cancel(); } catch { }\n        _liveReplanPending = true;\n        SetState(\"操作中の画面が切り替わったため、古い案内を破棄しました。新しい画面が落ち着いてから案内を作り直します…\", speak: false);\n    }\n\n    private void ClearStableLiveChangeCandidate()\n    {\n""",
)

# 3) type_text: text changing is not proof that the finishing Enter/action succeeded.
v3 = "src/HelpSys.Desktop/MainWindow.ReliabilityV3.cs"
replace_once(
    v3,
    """        if (action.Equals(\"type_text\", StringComparison.OrdinalIgnoreCase) && systemBefore?.Browser is null && type is \"Edit\" or \"ComboBox\")\n        {\n            if (!string.Equals(beforeTarget.Value, current.Value, StringComparison.Ordinal) && current.Value is not null) return true;\n        }\n\n""",
    """        // A changed Edit value proves only that typing happened. type_text is completed only\n        // after the finishing key causes a stable system/window/content transition.\n\n""",
)
replace_once(
    v3,
    """    private static bool HasSystemTransitionV3(SystemContextSnapshot? before, SystemContextSnapshot after)\n    {\n        if (before is null) return false;\n        if (!before.ForegroundProcess.Equals(after.ForegroundProcess, StringComparison.OrdinalIgnoreCase)) return true;\n""",
    """    private static bool HasSystemTransitionV3(SystemContextSnapshot? before, SystemContextSnapshot after)\n    {\n        if (before is null) return false;\n        if (before.ForegroundProcessId > 0 && after.ForegroundProcessId > 0 && before.ForegroundProcessId != after.ForegroundProcessId) return true;\n        if (!before.ForegroundProcess.Equals(after.ForegroundProcess, StringComparison.OrdinalIgnoreCase)) return true;\n""",
)
# Isolate exceptions escaping async-void hook handlers.
replace_once(
    v3,
    "private async void OnObservedLeftClickV3(Point point)\n    {",
    """private async void OnObservedLeftClickV3(Point point)\n    {\n        try\n        {\n            await HandleObservedLeftClickV3Async(point);\n        }\n        catch (OperationCanceledException) { }\n        catch (ObjectDisposedException) { }\n        catch\n        {\n            try\n            {\n                if (!Dispatcher.HasShutdownStarted && !Dispatcher.HasShutdownFinished)\n                    StopWithMessage(\"操作結果の確認中に予期しない問題が起きたため、古い案内を破棄しました。もう一度「案内」を押してください。\");\n            }\n            catch { }\n        }\n    }\n\n    private async Task HandleObservedLeftClickV3Async(Point point)\n    {""",
)
replace_once(
    v3,
    "private async void OnObservedKeyReleasedV3(KeyObservation observation)\n    {",
    """private async void OnObservedKeyReleasedV3(KeyObservation observation)\n    {\n        try\n        {\n            await HandleObservedKeyReleasedV3Async(observation);\n        }\n        catch (OperationCanceledException) { }\n        catch (ObjectDisposedException) { }\n        catch\n        {\n            try\n            {\n                if (!Dispatcher.HasShutdownStarted && !Dispatcher.HasShutdownFinished)\n                    StopWithMessage(\"キー操作の確認中に予期しない問題が起きたため、古い案内を破棄しました。もう一度「案内」を押してください。\");\n            }\n            catch { }\n        }\n    }\n\n    private async Task HandleObservedKeyReleasedV3Async(KeyObservation observation)\n    {""",
)

# 4) Cloud response is stale if the working foreground changes during inference.
cloud = "src/HelpSys.Desktop/Services/CloudGuideService.cs"
replace_once(
    cloud,
    """    private readonly HttpClient _http;\n    private readonly string _apiBase;\n""",
    """    private readonly HttpClient _http;\n    private readonly SystemContextService _contextVerifier = new();\n    private readonly string _apiBase;\n""",
)
replace_once(
    cloud,
    """        return await SendAsync<GuideDecision>(() => CreateMessage(HttpMethod.Post, $\"{_apiBase}/v1/guide\", body), cancellationToken);\n""",
    """        EnsurePlanningContextCurrent(systemContext);\n        var decision = await SendAsync<GuideDecision>(() => CreateMessage(HttpMethod.Post, $\"{_apiBase}/v1/guide\", body), cancellationToken);\n        EnsurePlanningContextCurrent(systemContext);\n        return decision;\n""",
)
replace_once(
    cloud,
    """        return await SendAsync<VisionGuideDecision>(() => CreateMessage(HttpMethod.Post, $\"{_apiBase}/v1/vision-guide\", body), cancellationToken);\n""",
    """        EnsurePlanningContextCurrent(systemContext);\n        var decision = await SendAsync<VisionGuideDecision>(() => CreateMessage(HttpMethod.Post, $\"{_apiBase}/v1/vision-guide\", body), cancellationToken);\n        EnsurePlanningContextCurrent(systemContext);\n        return decision;\n""",
)
replace_once(
    cloud,
    """                (foregroundId > 0 && x.ProcessId == foregroundId) ||\n                (!string.IsNullOrWhiteSpace(foregroundName) && x.ProcessName.Equals(foregroundName, StringComparison.OrdinalIgnoreCase)) ||\n                ShellProcesses.Contains(x.ProcessName))\n""",
    """                (foregroundId > 0\n                    ? x.ProcessId == foregroundId\n                    : !string.IsNullOrWhiteSpace(foregroundName) && x.ProcessName.Equals(foregroundName, StringComparison.OrdinalIgnoreCase)) ||\n                ShellProcesses.Contains(x.ProcessName))\n""",
)
replace_once(
    cloud,
    """    private HttpRequestMessage CreateMessage(HttpMethod method, string url, object body)\n    {\n""",
    """    private void EnsurePlanningContextCurrent(SystemContextSnapshot expected)\n    {\n        var current = _contextVerifier.Capture();\n        var foregroundChanged = expected.ForegroundProcessId <= 0 || current.ForegroundProcessId <= 0 ||\n                                expected.ForegroundProcessId != current.ForegroundProcessId ||\n                                !expected.ForegroundProcess.Equals(current.ForegroundProcess, StringComparison.OrdinalIgnoreCase);\n\n        var expectedUrl = expected.Browser?.Url ?? string.Empty;\n        var currentUrl = current.Browser?.Url ?? string.Empty;\n        var browserChanged = !string.IsNullOrWhiteSpace(expectedUrl) && !string.IsNullOrWhiteSpace(currentUrl) &&\n                             !expectedUrl.Equals(currentUrl, StringComparison.OrdinalIgnoreCase);\n\n        if (foregroundChanged || browserChanged)\n            throw new GuideServiceException(GuideFailureKind.ContextChanged, \"操作中の画面が切り替わったため、古い案内応答を破棄しました。\");\n    }\n\n    private HttpRequestMessage CreateMessage(HttpMethod method, string url, object body)\n    {\n""",
)

failure = "src/HelpSys.Desktop/Services/GuideServiceException.cs"
replace_once(failure, """    Rejected,\n    InvalidResponse\n""", """    Rejected,\n    InvalidResponse,\n    ContextChanged\n""")

main = "src/HelpSys.Desktop/MainWindow.xaml.cs"
replace_once(
    main,
    """            GuideFailureKind.InvalidResponse => \"案内サービスから利用できる形式の応答を受け取れませんでした。現在の画面を推測せず、案内を停止します。\",\n            _ => \"案内サービスを利用できませんでした。\"\n""",
    """            GuideFailureKind.InvalidResponse => \"案内サービスから利用できる形式の応答を受け取れませんでした。現在の画面を推測せず、案内を停止します。\",\n            GuideFailureKind.ContextChanged => \"操作中の画面が切り替わったため、古い案内を表示せず破棄しました。現在の画面で、もう一度「案内」を押してください。\",\n            _ => \"案内サービスを利用できませんでした。\"\n""",
)

# 5) Vision must never expand a failed monitor lookup to every monitor.
capture = "src/HelpSys.Desktop/Services/ScreenCaptureService.cs"
replace_once(
    capture,
    """        return new CaptureArea(\n            GetSystemMetrics(SmXVirtualScreen),\n            GetSystemMetrics(SmYVirtualScreen),\n            GetSystemMetrics(SmCxVirtualScreen),\n            GetSystemMetrics(SmCyVirtualScreen));\n""",
    """        throw new InvalidOperationException(\"操作中のモニターを特定できないため、複数画面をまとめて送信せずVision案内を停止します。\");\n""",
)

# 6) Final Worker guard: background process existence is not task completion.
worker_guard = Path("worker/reliability-v4-guard.js")
worker_guard.write_text(r"""import base from './deliberation-guard.js';

const START_PROCESS = /(searchhost|startmenuexperiencehost)/i;
const APP_RULES = [
  { goal: /(excel|エクセル)/i, processes: ['excel'], search: 'Excel' },
  { goal: /(word|ワード)/i, processes: ['winword'], search: 'Word' },
  { goal: /(powerpoint|パワーポイント|パワポ)/i, processes: ['powerpnt'], search: 'PowerPoint' },
  { goal: /(chrome|クローム|グーグルクローム)/i, processes: ['chrome'], search: 'Google Chrome' },
  { goal: /(edge|エッジ)/i, processes: ['msedge'], search: 'Microsoft Edge' },
  { goal: /(メモ帳|notepad)/i, processes: ['notepad'], search: 'メモ帳' },
  { goal: /(電卓|calculator)/i, processes: ['calculatorapp'], search: '電卓' }
];

export default {
  async fetch(request, env, ctx) {
    let bodyPromise = null;
    try {
      const url = new URL(request.url);
      if (request.method === 'POST' && url.pathname === '/v1/guide') bodyPromise = request.clone().json();
    } catch { }

    const response = await base.fetch(request, env, ctx);
    if (!bodyPromise || response.status !== 200) return response;

    let body;
    let decision;
    try {
      body = await bodyPromise;
      decision = await response.clone().json();
    } catch {
      return response;
    }

    const override = preventBackgroundDone(body, decision);
    return override ? replaceJson(response, override) : response;
  }
};

function preventBackgroundDone(body, decision) {
  if (!decision || String(decision.status || '').toLowerCase() !== 'done') return null;
  if (!/すでに開いて|既に開いて/i.test(String(decision.instruction || ''))) return null;

  const goal = String(body?.request || '');
  const rule = APP_RULES.find(x => x.goal.test(goal));
  if (!rule) return null;

  const foreground = String(body?.systemContext?.foregroundProcess ?? body?.systemContext?.ForegroundProcess ?? '').toLowerCase();
  if (rule.processes.includes(foreground)) return null;

  const elements = usable(body?.elements);
  const search = elements.find(x => String(x.controlType || '').toLowerCase() === 'edit' &&
    /(検索|search)/i.test(`${x.name || ''} ${x.automationId || ''}`) &&
    /searchhost|startmenuexperiencehost|explorer/i.test(String(x.processName || '')));

  if (search?.focused === true) {
    return {
      status: 'target', targetId: String(search.id), action: 'type_text',
      instruction: `キーボードで「${rule.search}」と入力し、最後に「Enter」と書かれたキーを1回押してください。`,
      question: null, key: 'Enter', confidence: 0.99
    };
  }
  if (search) {
    return {
      status: 'target', targetId: String(search.id), action: 'left_click',
      instruction: '開いている検索画面の、文字を入力できる欄で、マウスの左ボタンを1回押してください。',
      question: null, key: null, confidence: 0.99
    };
  }

  const startOpen = START_PROCESS.test(foreground) || elements.some(x =>
    START_PROCESS.test(String(x.processName || '')) &&
    (x.focused === true || /(検索|search|ピン留め|pinned|おすすめ|すべて)/i.test(String(x.name || ''))));
  if (startOpen) {
    return {
      status: 'not_found', targetId: null, action: 'none',
      instruction: '目的のアプリは別の画面で開いていますが、今見えている画面にはまだ出ていません。現在の検索画面から安全に選べる場所を確認し直します。',
      question: null, key: null, confidence: 0.99
    };
  }

  return {
    status: 'target', targetId: null, action: 'press_key',
    instruction: '目的のアプリは別の画面で開いています。今操作できる画面へ出すため、キーボードの左下にある窓の形の「Windows」キーを1回押してください。',
    question: null, key: 'Windows', confidence: 0.99
  };
}

function usable(raw) {
  return (Array.isArray(raw) ? raw : []).filter(x => x && x.interactable !== false && x.enabled !== false && String(x.id || '').trim());
}

function replaceJson(response, value) {
  const headers = new Headers(response.headers);
  headers.set('content-type', 'application/json; charset=utf-8');
  return new Response(JSON.stringify(value), { status: 200, headers });
}
""", encoding="utf-8")

selftest = Path("worker/reliability-v4-selftest.mjs")
selftest.write_text(r"""import guard from './reliability-v4-guard.js';

function assert(condition, message) {
  if (!condition) throw new Error(message);
}

async function guide(body) {
  const request = new Request('https://example.test/v1/guide', {
    method: 'POST', headers: { 'content-type': 'application/json' }, body: JSON.stringify(body)
  });
  const response = await guard.fetch(request, {}, {});
  assert(response.status === 200, `unexpected status ${response.status}`);
  return response.json();
}

const background = await guide({
  request: 'Excelを開いて',
  history: [],
  systemContext: { foregroundProcess: 'notepad', foregroundProcessId: 22, runningApps: ['excel', 'notepad'], taskbarVisible: true },
  elements: [{ id: 'u1', name: '本文', automationId: 'Editor', className: 'Edit', controlType: 'Edit', processName: 'notepad', interactable: true, enabled: true, keyboardFocusable: true, focused: true, password: false, x: 10, y: 10, width: 400, height: 300 }]
});
assert(background.status !== 'done', 'background Excel must not be treated as completed');
assert(background.action === 'press_key' && /windows/i.test(background.key || ''), 'background app should be brought forward through a safe route');

const foreground = await guide({
  request: 'Excelを開いて',
  history: [],
  systemContext: { foregroundProcess: 'excel', foregroundProcessId: 44, runningApps: ['excel'], taskbarVisible: true },
  elements: [{ id: 'u1', name: 'Microsoft Excel', automationId: 'Main', className: 'XLMAIN', controlType: 'Window', processName: 'excel', interactable: false, enabled: true, keyboardFocusable: false, focused: false, password: false, x: 0, y: 0, width: 1000, height: 700 }]
});
assert(foreground.status === 'done', 'foreground Excel should remain completed');

console.log('reliability v4 guard self-test passed.');
""", encoding="utf-8")

# Route deployment and CI through the final guard.
replace_once("wrangler.jsonc", '"main": "worker/deliberation-guard.js"', '"main": "worker/reliability-v4-guard.js"')
# Remove obsolete offline keyword planner; it is not part of the active guidance path.
planner = Path("src/HelpSys.Desktop/Services/GuidePlanner.cs")
if planner.exists():
    planner.unlink()

# Remove the one-shot patch machinery from the resulting source commit.

print("Reliability v4 patch applied.")
