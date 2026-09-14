import fs from 'node:fs';

const main = fs.readFileSync('src/HelpSys.Desktop/MainWindow.xaml.cs', 'utf8');
const live = fs.readFileSync('src/HelpSys.Desktop/MainWindow.LiveGuidance.cs', 'utf8');
const local = fs.readFileSync('src/HelpSys.Desktop/MainWindow.LocalChoiceResolver.cs', 'utf8');
const facade = fs.readFileSync('src/HelpSys.Desktop/UiAutomationScanner.cs', 'utf8');

const legacyHook = main.indexOf('TryHandleLocalAccountChoiceAnswerAsync(text)');
const legacyHistory = main.indexOf('new GuideHistoryItem(_stepNumber, "clarification_answer", text');
if (legacyHook < 0 || legacyHistory < 0 || legacyHook > legacyHistory)
  throw new Error('legacy clarification path must intercept a visible-choice answer before history/cloud handling');

const liveHook = live.indexOf('TryHandleLocalAccountChoiceAnswerAsync(answer)');
const liveHistory = live.indexOf('new GuideHistoryItem(_stepNumber, "clarification_answer", answer');
if (liveHook < 0 || liveHistory < 0 || liveHook > liveHistory)
  throw new Error('AnswerBox path must intercept a visible-choice answer before history/cloud handling');

if (!local.includes('TryHandleLocalVisibleChoiceAnswerAsync'))
  throw new Error('local clarification handling must be implemented as a generic visible-choice resolver');
if (!local.includes('LocalVisibleChoiceQuestionRegex'))
  throw new Error('generic visible-choice questions must be recognized independently from account-specific wording');
if (local.includes('IsSupportedLocalChoiceBrowser') || local.includes('LocalAccountChoiceSurfaceRegex'))
  throw new Error('visible-choice resolution must not be restricted to specific browsers or account surfaces');
if (!local.includes('FindUniqueLocalVisibleChoice'))
  throw new Error('visible-choice resolver must require a unique local UIA match');
if (!local.includes('RevalidateCandidateAsync(match, context.ForegroundProcessId'))
  throw new Error('locally resolved target must be revalidated before guidance');
if (!local.includes('"利用者が選んだ項目"'))
  throw new Error('local visible-choice history must use an opaque generic label');
if (local.includes('_activeRequest +=') || local.includes('clarification_answer", answer'))
  throw new Error('local visible-choice resolver must not append the raw answer to cloud-bound request/history');
if (!local.includes('fresh with { Name = "利用者が選んだ項目" }'))
  throw new Error('generic local choices must keep later cloud-bound history opaque');
if (!local.includes('HideClarificationUiIfNeeded(force: true)'))
  throw new Error('successful local choice must close the clarification panel before target guidance');

const retryWait = local.indexOf('WaitForClarification(question);');
const retryUi = local.indexOf('EnsureClarificationUi();', retryWait);
if (retryWait < 0 || retryUi < 0 || retryUi < retryWait)
  throw new Error('failed local matching must keep clarification UI available for a local retry');

if (!local.includes('FindUniqueContainingLocalChoice(candidates, interactable, normalizedAnswer'))
  throw new Error('resolver must support visible child-label to containing clickable-control mapping');
if (!local.includes('x.Bounds.Contains(center)'))
  throw new Error('context mapping must require geometric containment, not nearest-neighbour guessing');
if (!local.includes('mapped.Count == 1 ? mapped.Values.Single() : null'))
  throw new Error('context mapping must fail closed unless exactly one clickable target remains');
if (!local.includes('!x.Interactable && x.Enabled') || !local.includes('context.ProcessId <= 0 || x.ProcessId <= 0 || x.ProcessId == context.ProcessId'))
  throw new Error('context mapping must stay local to visible context and the same UIA process when known');

if (!facade.includes('HelpSys.Services.UiAutomationScanner _inner'))
  throw new Error('MainWindow scanner facade must preserve the established low-level scanner as fallback');
if (!facade.includes('GetForegroundWindow()') || !facade.includes('actualProcessId != expectedProcessId'))
  throw new Error('candidate scoping must bind the foreground HWND to the requested process before filtering');
if (!facade.includes('IsInsideOrMostlyOverlapping(candidate.Bounds, foregroundBounds)'))
  throw new Error('foreground-window scoping must use current window geometry');
if (!facade.includes('scoped.Any(candidate => candidate.Interactable) ? scoped : candidates'))
  throw new Error('foreground-window scoping must fall back when the narrowed UIA surface is not actionable');
if (!facade.includes('IsShellSurfaceProcess(processId)'))
  throw new Error('Windows shell cross-process surfaces must retain compatibility fallback');
if (!facade.includes('IsAllowedByForegroundWindow(fresh, rootProcessId)'))
  throw new Error('revalidated targets must remain bound to the current foreground window when possible');

console.log('HelpSys generic visible-choice and foreground-window contract passed.');
