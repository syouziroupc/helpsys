import fs from 'node:fs';

const read = file => fs.readFileSync(file, 'utf8');
const assert = (condition, message) => { if (!condition) throw new Error(message); };

const guards = read('src/HelpSys.Desktop/MainWindow.DeepAuditGuards.cs');
const stable = read('src/HelpSys.Desktop/MainWindow.StableGuidance.cs');
const watcher = read('src/HelpSys.Desktop/Services/GuidanceStateWatcher.cs');
const qualityWindow = read('src/HelpSys.Desktop/MainWindow.QualityFirst.cs');
const qualityWorker = read('worker/quality-guide.js');

assert(guards.includes('off_route_click'), 'Off-route clicks must be recorded as route-deviation evidence.');
assert(guards.includes('TryRouteRecoveryAsync("案内枠以外の場所が操作された"'), 'Off-route clicks must immediately trigger route recovery.');
assert(guards.includes('IsPointInsideHelpSysWindow'), 'HelpSys self-interaction must not be mistaken for route deviation.');
assert(guards.includes('_actionObserver.KeyReleased -= OnObservedKeyReleasedV3;'), 'The type-text submit guard must run before the normal key verifier.');
assert(guards.includes('_actionObserver.KeyReleased += ObserveTypeTextSubmitDeepAudit;'), 'A synchronous type-text submit guard must be installed.');
assert(guards.includes('type_target_lost_focus'), 'Lost focus at text-submit time must be recorded as route-deviation evidence.');
assert(guards.includes('IsCurrentTextTargetFocusedDeepAudit'), 'The finishing key must re-check the actual focused UIA target.');
assert(guards.includes('ClearCurrentGuidanceV3();'), 'A bad finishing key must clear current guidance before normal verification can consume it.');
assert(guards.includes('入力確定時に案内対象の入力欄からフォーカスが外れている'), 'Lost-focus text entry must route into current-state recovery.');

assert(stable.includes('AttachDeepAuditGuards();'), 'Deep-audit interaction guards must be attached with the live watcher.');
assert(stable.includes('HasSemanticLiveStateChanged'), 'Single-control semantic state changes must be detected independently of whole-screen similarity.');
assert(stable.includes('semanticChange || HasStableLiveTopologyChanged'), 'Semantic deviations must feed the live change decision directly.');
assert(stable.includes('!semanticChange && !ConfirmStableLiveChange'), 'A confirmed UIA semantic change must not be hidden by the whole-screen similarity threshold.');
assert(stable.includes('SemanticLiveState'), 'Stable UI fingerprints must include semantic control state.');
assert(stable.includes('toggle={x.ToggleState'), 'Toggle state must participate in stable-state detection.');
assert(stable.includes('selected={x.Selected'), 'Selection state must participate in stable-state detection.');
assert(stable.includes('expand={x.ExpandCollapseState'), 'Expand/collapse state must participate in stable-state detection.');
assert(stable.includes('if (!_verifyingAction) InvalidatePlannerForLiveContextChange();'), 'Hard context changes during scanning must invalidate stale guidance even when no planner request is active.');
assert(!stable.includes('if (!_sessionState.PlannerInFlight) return;\n        _sessionState.Invalidate'), 'Stale-guidance invalidation must not depend solely on PlannerInFlight.');
assert(!stable.includes('_currentDecision.Action.Equals("type_text", StringComparison.OrdinalIgnoreCase)'), 'Text-entry guidance must not suppress focus/state deviation detection.');

assert(watcher.includes('AddAutomationPropertyChangedEventHandler'), 'Semantic UI state changes must trigger live observation without waiting only for heartbeat scans.');
assert(watcher.includes('TogglePattern.ToggleStateProperty'), 'Toggle changes must trigger the live watcher.');
assert(watcher.includes('SelectionItemPattern.IsSelectedProperty'), 'Selection changes must trigger the live watcher.');
assert(watcher.includes('ExpandCollapsePattern.ExpandCollapseStateProperty'), 'Expand/collapse changes must trigger the live watcher.');
assert(watcher.includes('AutomationElement.HasKeyboardFocusProperty'), 'Focus changes must trigger the live watcher.');
assert(watcher.includes('RemoveAutomationPropertyChangedEventHandler'), 'Semantic UIA subscriptions must be removed during scope changes/shutdown.');

assert(qualityWindow.includes('var visualAction = decision.Action.Equals("double_click"'), 'Visual targets must preserve double-click semantics.');
assert(qualityWindow.includes('new GuideDecision("target", "vision-target", visualAction'), 'Visual target runtime state must use the preserved action.');
assert(!qualityWindow.includes('new GuideDecision("target", "vision-target", "left_click"'), 'Visual targets must not be forcibly downgraded to a single click.');

assert(qualityWorker.includes('guardVisionDecisionForTask'), 'Quality visual targets must pass the known-site vision safety guard.');
assert(qualityWorker.includes("task?.kind === 'site' && task?.deterministic?.status !== 'done'"), 'Known-site done must require the official current browser domain.');
assert(qualityWorker.includes("task?.kind === 'site' || (isStrictTask(task)"), 'Known-site structured targets must pass the final target guard in all quality modes.');
assert(qualityWorker.includes("task?.kind === 'site' && task?.forceVision === true"), 'Search results without an official structured match must not fall back to arbitrary structured links.');

console.log('HelpSys deep-audit desktop/quality contract passed.');
