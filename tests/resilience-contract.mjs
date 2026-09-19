import fs from 'node:fs';

const read = file => fs.readFileSync(file, 'utf8');
const assert = (value, message) => { if (!value) throw new Error(message); };

const app = read('src/HelpSys.Desktop/App.xaml.cs');
const scanner = read('src/HelpSys.Desktop/Services/UiAutomationScanner.cs');
const scannerFacade = read('src/HelpSys.Desktop/UiAutomationScanner.cs');
const deepAudit = read('src/HelpSys.Desktop/MainWindow.DeepAuditGuards.cs');
const reliability = read('src/HelpSys.Desktop/MainWindow.ReliabilityV3.cs');
const observerClient = read('src/HelpSys.Desktop/Services/UiAutomationObserverClient.cs');
const observerHost = read('src/HelpSys.Desktop/Services/UiAutomationObserverHost.cs');
const watcher = read('src/HelpSys.Desktop/Services/GuidanceStateWatcher.cs');
const cloud = read('src/HelpSys.Desktop/Services/CloudGuideService.cs');
const stable = read('src/HelpSys.Desktop/MainWindow.StableGuidance.cs');
const resilience = read('src/HelpSys.Desktop/MainWindow.Resilience.cs');
const recovery = read('src/HelpSys.Desktop/MainWindow.RecoveryPolicy.cs');
const main = read('src/HelpSys.Desktop/MainWindow.xaml.cs');
const quality = read('src/HelpSys.Desktop/MainWindow.QualityFirst.cs');
const routeRecovery = read('src/HelpSys.Desktop/MainWindow.RouteRecovery.cs');
const privacy = read('src/HelpSys.Desktop/MainWindow.Privacy.cs');

assert(app.indexOf('UiAutomationObserverHost.IsObserverProcess') < app.indexOf('HelpSys.Desktop.SingleInstance'),
  'UIA observer mode must bypass the desktop single-instance mutex.');
assert(app.includes('UiAutomationScanner.ShutdownSharedObserver()'),
  'Normal HelpSys shutdown must terminate the isolated UIA observer.');

assert(scanner.includes('SharedObserver') && scanner.includes('UseObserver'),
  'UIA scanner must route normal desktop calls through the isolated observer.');
assert(!scannerFacade.includes('AutomationElement.') && !scannerFacade.includes('TreeWalker.') && !scannerFacade.includes('ValuePattern.'),
  'Main-process scanner facade must not perform direct UI Automation calls.');
assert(!deepAudit.includes('AutomationElement.') && !deepAudit.includes('TreeWalker.'),
  'Type-text completion audit must not perform direct UI Automation in the desktop process.');
assert(deepAudit.includes('_scanner.RevalidateCandidateAsync') &&
       reliability.includes('Volatile.Read(ref _typeTextFocusRecoveryInFlight) != 0'),
  'Type-text completion must wait for observer-backed focus proof before marking the step successful.');
assert(scanner.includes('var rawValue = valueValue.Current.Value?.Trim();'),
  'Ordinary non-password input evidence must be captured inside the isolated observer before local privacy filtering.');
assert(scanner.includes('forceLocal: true') === false,
  'The normal UIA scanner must not hard-code local execution.');
assert(observerHost.includes('new UiAutomationScanner(forceLocal: true)'),
  'Only the observer child may execute UIA scans locally.');
assert(observerClient.includes('RequestTimeout = TimeSpan.FromMilliseconds(3800)'),
  'UIA observer calls must have a hard outer deadline.');
assert(observerClient.includes('_process.Kill(entireProcessTree: true)'),
  'A hung UIA observer must be killable without terminating HelpSys.');
assert(observerClient.includes('RestartObserver();') && observerClient.includes('TimeoutException'),
  'UIA timeout must reset the observer process.');

assert(watcher.includes('Task.Run(() =>') && watcher.includes('Automation.AddAutomationFocusChangedEventHandler'),
  'Global UIA event registration must stay off the WPF dispatcher.');
assert(watcher.includes('DrainStoppedPumpAndSubscriptionsAsync'),
  'UIA watcher removal must be asynchronous.');
assert(stable.includes('Interval = TimeSpan.FromMilliseconds(120)'),
  'Foreground handoff sampling must not run at the former 30 ms dispatcher cadence.');
assert(watcher.includes('public event EventHandler? Changed;') && watcher.includes('Changed?.Invoke'),
  'Security monitoring must receive UIA changes independently of the heavier live planner scan.');
assert(cloud.includes('UsePrivacyEpochProvider') && cloud.includes('EnsurePrivacyEpochCurrent(privacyEpoch)'),
  'Every cloud planning path must bind egress to the current security epoch.');
assert(privacy.includes('Interlocked.Increment(ref _privacyEgressEpoch)'),
  'UI changes and privacy transitions must invalidate previously approved egress state.');
const plannerGuard = stable.indexOf('if (_sessionState.PlannerInFlight)');
const liveScan = stable.indexOf('_scanner.CaptureCandidatesForProcessAsync', plannerGuard);
assert(plannerGuard >= 0 && liveScan > plannerGuard,
  'Heavy live UIA scanning must be suppressed while the planner owns the observer.');

assert(resilience.includes('ResilienceRecoveryDelaysMs') && resilience.includes('15000'),
  'Automatic recovery must back off instead of spinning.');
assert(resilience.includes('QueueResilientRecovery') && resilience.includes('TryRunPendingLiveReplanAsync()'),
  'Recovery must preserve the goal and return through the established planner.');
assert(resilience.includes('ResetCurrentStateReplanBudget()'),
  'A backed-off retry must get a fresh bounded observation budget.');
assert(recovery.includes('QueueResilientRecovery(reason, generation)'),
  'Technical planning uncertainty must enter resilience recovery.');
assert(!recovery.includes('StopWithMessage('),
  'Technical planning uncertainty must never terminate the session.');

const stopFailureStart = main.indexOf('private void StopWithGuideFailure');
const stopMessageStart = main.indexOf('private void StopWithMessage', stopFailureStart);
const stopFailureBlock = main.slice(stopFailureStart, stopMessageStart);
assert(stopFailureBlock.includes('GuideFailureKind.PrivacyBlocked'),
  'Privacy-blocked guidance must remain a resumable privacy pause.');
assert(stopFailureBlock.includes('QueueResilientRecovery(reason)'),
  'Network/service/context/model failures must automatically recover.');
assert(!stopFailureBlock.includes('StopWithMessage('),
  'Guide-service failures must not discard the active goal.');

assert(main.includes('QueueResilientRecovery("技術的な画面不確実性が継続しているため自動復旧へ移行", generation)'),
  'Repeated technical clarification must enter automatic recovery instead of stopping.');
assert(privacy.includes('CancelResilienceRecovery();') && privacy.includes('ResetResilienceRecovery();'),
  'Privacy pause/resume must coordinate with resilience retries without losing the goal.');

assert(quality.includes('SnapToAccessibleCandidateAsync') &&
       quality.includes('snappedTarget.ProcessId != systemContext.ForegroundProcessId'),
  'Quality visual guidance must require concrete metadata from the current foreground Windows target.');
assert(quality.includes('NormalizeStructuredDecisionForTarget') &&
       quality.includes('画像候補を現在のWindows操作要素へ対応付けできない'),
  'Visual coordinates must be locally action-normalized or discarded.');
assert(routeRecovery.includes('SnapToAccessibleCandidateAsync') &&
       routeRecovery.includes('snappedTarget.ProcessId != context.ForegroundProcessId') &&
       routeRecovery.includes('NormalizeStructuredDecisionForTarget'),
  'Route recovery must require the same real-control metadata and action normalization.');

console.log('HelpSys vNext resilience/freeze/misguidance contract passed.');
