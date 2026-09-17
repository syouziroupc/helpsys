import fs from 'node:fs';

const facade = fs.readFileSync('src/HelpSys.Desktop/UiAutomationScanner.cs', 'utf8');
const local = fs.readFileSync('src/HelpSys.Desktop/MainWindow.LocalChoiceResolver.cs', 'utf8');
const replan = fs.readFileSync('src/HelpSys.Desktop/MainWindow.CurrentStateReplan.cs', 'utf8');
const policy = fs.readFileSync('src/HelpSys.Desktop/MainWindow.RecoveryPolicy.cs', 'utf8');
const recovery = fs.readFileSync('src/HelpSys.Desktop/MainWindow.RouteRecovery.cs', 'utf8');
const reliability = fs.readFileSync('src/HelpSys.Desktop/MainWindow.ReliabilityV3.cs', 'utf8');

if (!facade.includes('ScopeToForegroundWindow(processId, candidates)'))
  throw new Error('normal UI observation must pass through foreground-window scoping');
if (!facade.includes('candidate.ProcessId <= 0 || candidate.ProcessId == processId'))
  throw new Error('shell observation must stay bound to the foreground shell process instead of mixing PIDs');
if (!facade.includes('RestoreLocalInputEvidence'))
  throw new Error('non-secret input evidence must be restored locally for usable search/address guidance');
if (!facade.includes('ValuePattern.Pattern'))
  throw new Error('local input evidence must be able to inspect current non-password text');
if (!facade.includes('candidate.Password'))
  throw new Error('password inputs must remain excluded from local value restoration');
if (!local.includes('TryHandleLocalVisibleChoiceAnswerAsync'))
  throw new Error('visible clarification choices must use the generic local resolver');
if (local.includes('IsSupportedLocalChoiceBrowser'))
  throw new Error('generic local choice must not contain a browser allowlist');
if (!replan.includes('MaximumAutomaticCurrentStateReplans = 4'))
  throw new Error('transient UI uncertainty must get four bounded observations before escalation');
if (!policy.includes('TryRouteRecoveryAsync'))
  throw new Error('technical observation uncertainty must be allowed to use alternate route recovery instead of becoming a dead stop');
if (!policy.includes('WaitForClarification('))
  throw new Error('technical recovery must preserve a human-assisted continuation path after automatic recovery is exhausted');
if (policy.includes('StopWithMessage('))
  throw new Error('technical observation uncertainty must not terminate the active guidance task');
if (!recovery.includes('MaximumRouteRecoveryAttempts = 3'))
  throw new Error('heavy route recovery must remain separately bounded');
if (!reliability.includes('_consecutiveFailures == 2') || !reliability.includes('routeRecoveryIssue ='))
  throw new Error('confirmed repeated action failure must still use the reliability recovery trigger');

console.log('HelpSys generic UI resolution integration contract passed.');
