import fs from 'node:fs';

const fast = fs.readFileSync('src/HelpSys.Desktop/MainWindow.OutlawFastPath.cs', 'utf8');
const quality = fs.readFileSync('src/HelpSys.Desktop/MainWindow.QualityFirst.cs', 'utf8');
const session = fs.readFileSync('src/HelpSys.Desktop/Services/GuidanceSessionController.cs', 'utf8');

function need(text, fragment, message) {
  if (!text.includes(fragment)) throw new Error(message);
}
function reject(text, fragment, message) {
  if (text.includes(fragment)) throw new Error(message);
}

need(fast, 'TryOutlawUniversalShellNavigationAsync(',
  'Windows shell must have a generic navigator lane');
need(fast, 'ResolveGenericOpenTarget(',
  'Navigator must extract unknown open targets without an application whitelist');
need(fast, 'FindWindowsSearchField(',
  'Navigator must ground Windows Search from current UI evidence');
need(fast, 'FindNavigatorResultCandidate(',
  'Navigator must resolve a visible Windows Search result generically');
need(fast, 'FindVisibleShellTargetCandidate(',
  'Navigator must preserve visible desktop/taskbar targets before opening Windows Search');
need(fast, 'visible_shell_target',
  'Visible shell target deferral must be observable in navigator telemetry');
need(fast, 'navigator_postcondition',
  'Navigator must log verified postconditions');
need(fast, 'navigator_stage',
  'Navigator must log explicit state transitions');
need(fast, 'navigator_present',
  'Keyboard presentation success/failure must be observable');
need(fast, 'searchField.Value',
  'Browser query verification must inspect the live search-field value');
need(fast, 'IsNavigatorQueryVerified(',
  'Browser search must verify live state rather than infer success from history');
need(fast, 'Ctrl+A',
  'Mismatched live queries must be replaced rather than appended');
reject(fast, 'reason=query_already_submitted',
  'Audit history must not be treated as authoritative query state');
need(fast, 'return _sessionState.State == GuidanceSessionState.AwaitingUserAction;',
  'Fast Path success must require actual presentation into AwaitingUserAction');
need(fast, 'ResolveBeginnerWebSearchText(_activeRequest) is not null',
  'Known web destinations must bypass Windows application search while the shell is foreground');
need(fast, 'navigator_defer_web_goal',
  'Web-goal shell deferral must be observable in logs');
need(fast, 'IsWindowsSearchSurfaceProcess(context.ForegroundProcess)',
  'Windows Search must be recognized from the foreground process even when fused UIA candidates omit SearchHost');
need(fast, '_outlawNavigatorStage is "open_search_surface" or "search_surface_unresolved"',
  'Navigator must never present the Windows key repeatedly for the same transaction');
const genericStart = fast.indexOf('private string? ResolveGenericOpenTarget(');
const genericEnd = fast.indexOf('private static string? ResolveBeginnerWebSearchText(', genericStart);
const genericMethod = genericStart >= 0 && genericEnd > genericStart ? fast.slice(genericStart, genericEnd) : '';
reject(genericMethod, 'value.Contains("見たい"',
  'Generic application launching must not interpret a bare Japanese viewing intent as an app launch');
need(quality, 'reason=verified_navigation',
  'Verified universal navigation must be the primary local navigation path');
need(quality, 'reason=application_launch_via_start_fallback',
  'Legacy known-app launch must be fallback-only');
const universalCall = quality.indexOf('await TryOutlawBrowserSearchFastPathAsync(');
const legacyCall = quality.indexOf('TryOutlawApplicationLaunchFastPath(localCandidates, localContext, generation)');
if (universalCall < 0 || legacyCall < 0 || universalCall > legacyCall) {
  throw new Error('Universal verified navigation must run before the legacy known-app launcher');
}
const legacyMethod = quality.slice(quality.indexOf('private bool TryOutlawApplicationLaunchFastPath('));
need(legacyMethod, 'ShowKeyboardGuide(decision, candidates, systemContext, generation);',
  'Legacy keyboard fallback must still present keyboard guidance');
need(legacyMethod, 'return _sessionState.State == GuidanceSessionState.AwaitingUserAction;',
  'Legacy keyboard fallback must not report success unless presentation reached AwaitingUserAction');
need(session, 'GuidanceSessionState.Presenting or GuidanceSessionState.Clarifying',
  'Deterministic local keyboard Fast Paths must be able to present directly from Capturing');

console.log('Outlaw universal navigator contract passed');