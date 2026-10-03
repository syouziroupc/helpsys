import fs from 'node:fs';

const fast = fs.readFileSync('src/HelpSys.Desktop/MainWindow.OutlawFastPath.cs', 'utf8');
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
need(session, 'GuidanceSessionState.Presenting or GuidanceSessionState.Clarifying',
  'Deterministic local keyboard Fast Paths must be able to present directly from Capturing');

console.log('Outlaw universal navigator contract passed');
