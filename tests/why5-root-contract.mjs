import fs from 'node:fs';

const read = file => fs.readFileSync(file, 'utf8');
const assert = (condition, message) => { if (!condition) throw new Error(message); };

const observerHost = read('src/HelpSys.Desktop/Services/UiAutomationObserverHost.cs');
const lowerScanner = read('src/HelpSys.Desktop/Services/UiAutomationScanner.cs');
const watcher = read('src/HelpSys.Desktop/Services/GuidanceStateWatcher.cs');
const context = read('src/HelpSys.Desktop/Services/SystemContextService.cs');
const capture = read('src/HelpSys.Desktop/ScreenCaptureService.cs');
const quality = read('src/HelpSys.Desktop/MainWindow.QualityFirst.cs');
const reliability = read('src/HelpSys.Desktop/MainWindow.ReliabilityV3.cs');
const resilience = read('src/HelpSys.Desktop/MainWindow.Resilience.cs');
const currentReplan = read('src/HelpSys.Desktop/MainWindow.CurrentStateReplan.cs');
const routeRecovery = read('src/HelpSys.Desktop/MainWindow.RouteRecovery.cs');
const cloud = read('src/HelpSys.Desktop/Services/CloudGuideService.cs');
const trace = read('src/HelpSys.Desktop/Services/PerformanceTrace.cs');
const observerClient = read('src/HelpSys.Desktop/Services/UiAutomationObserverClient.cs');
const knowledge = read('worker/windows-knowledge.js');
const workerGuard = read('worker/reliability-v4-guard.js');

// Freeze root cause: UIA has exactly one process-side owner and one MTA execution lane.
assert(observerHost.includes('ApartmentState.MTA'), 'Observer must own a dedicated MTA automation thread.');
assert(observerHost.includes('BlockingCollection<WorkItem>'), 'Observer automation requests must be serialized.');
assert(lowerScanner.includes('TryRevalidateNearOriginalBounds'), 'Target validation must avoid a full UIA scan in the common case.');
assert(lowerScanner.includes('CacheRequest') && lowerScanner.includes('CreateTraversalCacheRequest'), 'UIA traversal properties/patterns must be batched with CacheRequest.');
assert(lowerScanner.includes('GetFirstChild(parent, cacheRequest)') && lowerScanner.includes('GetNextSibling(child, cacheRequest)'), 'Bounded UIA tree walking must request cached children.');

assert(!watcher.includes('System.Windows.Automation'), 'Live watcher must not own UIA.');
assert(!context.includes('System.Windows.Automation') && !context.includes('AutomationElement'), 'System context must not own UIA.');
assert(!capture.includes('System.Windows.Automation') && !capture.includes('AutomationElement'), 'Screenshot capture must not own UIA.');

// One observation per decision rather than repeated state reads.
assert(quality.includes('_observationBroker.CaptureAsync(240'), 'Normal planning must start from one bounded observation snapshot.');
assert(quality.includes('planningCts.CancelAfter(TimeSpan.FromSeconds(15))'), 'Whole interactive planning must have a short hard deadline.');
assert(!quality.includes('planningCts.CancelAfter(TimeSpan.FromSeconds(38))'), 'Legacy 38-second planning window must stay removed.');

// Action verification is event-driven and has one post-action snapshot, not a 7.5s polling loop.
assert(reliability.includes('_liveWatcher.Changed += handler'), 'Action verification must be event-driven.');
assert(reliability.includes('_observationBroker.CaptureAsync(240'), 'Action verification must use one bounded post-action snapshot.');
assert(!reliability.includes('TimeSpan.FromSeconds(7.5)'), 'Legacy full-scan polling verifier must stay removed.');
assert(reliability.includes('no_effect_'), 'No-effect actions must be recorded explicitly.');
assert(reliability.includes('同じ操作を繰り返さず'), 'No-effect actions must not be blindly repeated.');

// Recovery is finite and uses the same planner.
assert(resilience.includes('MaximumAutomaticRecoveriesPerState = 3'), 'Automatic recovery must have a finite per-state budget.');
assert(!resilience.includes('15000'), 'Legacy endless 15-second retry tail must stay removed.');
assert(currentReplan.includes('MaximumAutomaticCurrentStateReplans = 1'), 'Current-state refresh must be a single fresh snapshot.');
assert(!routeRecovery.includes('PlanRecoveryAsync'), 'Recovery must not own a separate AI planner.');
assert(routeRecovery.includes('await AdvanceGuideAsync()'), 'Recovery must re-enter the normal planner.');

// Transport retry is local and bounded.
assert(cloud.includes('for (var attempt = 0; attempt < 2; attempt++)'), 'Transport may perform exactly one immediate retry.');
assert(cloud.includes('CircuitFailureThreshold = 3'), 'Repeated service failures must open a circuit.');
assert(cloud.includes('CircuitOpenDuration = TimeSpan.FromSeconds(8)'), 'Circuit breaker duration must remain explicit and bounded.');
assert(cloud.includes('const int totalBudget = 96'), 'Cloud evidence must remain goal-ranked and bounded.');
assert(cloud.includes('"/v2/plan"'), 'Desktop normal planning must use the unified v2 planner endpoint.');
assert(workerGuard.includes("url.pathname === '/v2/plan'"), 'Worker must expose the unified v2 planner boundary.');
assert(workerGuard.includes("hasImage ? '/v1/quality-guide' : '/v1/guide'"), 'The unified boundary must select exactly one internal planner based on available image evidence.');
assert(trace.includes('MaxEvents = 256'), 'Performance tracing must remain bounded in memory.');
assert(trace.includes('HELPSYS_DIAGNOSTIC_MODE'), 'Performance trace mirroring must remain explicitly diagnostic.');
assert(observerClient.includes('"uia.capture-process"'), 'UIA latency must be measured by operation.');
assert(cloud.includes('"cloud.plan"'), 'Unified planner latency must be measured independently.');
assert(capture.includes('"screenshot.capture"'), 'Screenshot latency must be measured independently.');

// Route knowledge constrains unsafe actions instead of forcing recipes.
assert(knowledge.includes('forbiddenTargetIds'), 'Cross-site route constraints must be enforceable, not prompt-only.');
assert(knowledge.includes("elementRole(element) === 'web_search'"), 'Foreign-site internal search targets must be identified structurally.');
assert(knowledge.includes('taskInfo.forbiddenTargetIds'), 'Forbidden route targets must be rejected after model output.');
assert(knowledge.includes('固定ルートを強制しない'), 'Site guidance must remain current-state based.');


const mainWindow = read('src/HelpSys.Desktop/MainWindow.xaml.cs');
const startSessionIndex = mainWindow.indexOf('private async Task StartOrContinueSessionAsync()');
const commitForegroundIndex = mainWindow.indexOf('_systemContext.CommitStableForegroundForAssistantInteraction()', startSessionIndex);
const advanceIndex = mainWindow.indexOf('await AdvanceGuideAsync();', startSessionIndex);
assert(startSessionIndex >= 0 && commitForegroundIndex > startSessionIndex, 'Guide session must explicitly commit the sampled external foreground.');
assert(advanceIndex < 0 || commitForegroundIndex < advanceIndex, 'External foreground must be committed before planner entry.');

console.log('HelpSys Why5 root-cause architecture contract passed.');
