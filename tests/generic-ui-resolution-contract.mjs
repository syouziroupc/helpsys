import fs from 'node:fs';

const facade = fs.readFileSync('src/HelpSys.Desktop/UiAutomationScanner.cs', 'utf8');
const local = fs.readFileSync('src/HelpSys.Desktop/MainWindow.LocalChoiceResolver.cs', 'utf8');
const replan = fs.readFileSync('src/HelpSys.Desktop/MainWindow.CurrentStateReplan.cs', 'utf8');
const policy = fs.readFileSync('src/HelpSys.Desktop/MainWindow.RecoveryPolicy.cs', 'utf8');
const recovery = fs.readFileSync('src/HelpSys.Desktop/MainWindow.RouteRecovery.cs', 'utf8');
const reliability = fs.readFileSync('src/HelpSys.Desktop/MainWindow.ReliabilityV3.cs', 'utf8');

if (!facade.includes('ScopeToForegroundWindow(processId, candidates)'))
  throw new Error('normal UI observation must pass through foreground-window scoping');
if (!facade.includes('return scoped.Any(candidate => candidate.Interactable) ? scoped : candidates;'))
  throw new Error('foreground scoping must retain a process-level compatibility fallback');
if (!local.includes('TryHandleLocalVisibleChoiceAnswerAsync'))
  throw new Error('visible clarification choices must use the generic local resolver');
if (local.includes('IsSupportedLocalChoiceBrowser'))
  throw new Error('generic local choice must not contain a browser allowlist');
if (!replan.includes('MaximumAutomaticCurrentStateReplans = 2'))
  throw new Error('transient UI uncertainty must get two bounded observations');
if (policy.includes('WaitForClarification('))
  throw new Error('technical observation uncertainty must not masquerade as a user clarification');
if (!recovery.includes('MaximumRouteRecoveryAttempts = 3'))
  throw new Error('heavy route recovery must remain separately bounded');
if (!reliability.includes('_consecutiveFailures == 2') || !reliability.includes('routeRecoveryIssue ='))
  throw new Error('heavy route recovery must remain tied to confirmed repeated action failure');

console.log('HelpSys generic UI resolution integration contract passed.');
