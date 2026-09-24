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
if (!local.includes('TryPresentOutlawVisibleChoiceButtons'))
  throw new Error('Outlaw clarify handling must be able to present current visible choices directly');
if (!local.includes('"outlaw_direct_choice"'))
  throw new Error('Outlaw direct-choice presentation must be logged');
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

const quality = fs.readFileSync('src/HelpSys.Desktop/MainWindow.QualityFirst.cs', 'utf8');
const outlawClarify = quality.indexOf('if (OutlawModePolicy.Enabled)', quality.indexOf('quality.Status.Equals("clarify"'));
const directChoice = quality.indexOf('TryPresentOutlawVisibleChoiceButtons(candidates, systemContext, generation)', outlawClarify);
const technicalFallback = quality.indexOf('モデルが選択を要求したが現在画面から直接選択肢を構成できない', directChoice);
if (outlawClarify < 0 || directChoice < 0 || technicalFallback < 0)
  throw new Error('Outlaw clarification must prefer direct visible-choice UI and avoid open-ended questioning');

if (!quality.includes('高速構造判断が選択を要求したが現在画面から直接選択肢を構成できない'))
  throw new Error('Outlaw fast structured fallback must not drop into free-text clarification');
if (!quality.includes('(!OutlawModePolicy.Enabled && quick.Confidence < 0.93)'))
  throw new Error('Outlaw fast structured fallback must not apply the normal confidence veto');

if (!main.includes('HELPSYS_OUTLAW_AI_PROVIDER") ?? "glm"'))
  throw new Error('Pilot Outlaw build must default to GLM unless the user explicitly selects another provider');
const mainXaml = fs.readFileSync('src/HelpSys.Desktop/MainWindow.xaml', 'utf8');
if (!mainXaml.includes('Content="AI: GLM" Tag="glm" IsSelected="True"'))
  throw new Error('Pilot Outlaw provider selector must visibly default to GLM');

if (!local.includes('TryAutoSelectOutlawIdentityChoice'))
  throw new Error('Outlaw must locally auto-resolve visible low-risk identity/profile choices before cloud planning');
if (!local.includes('profileCardButton') || !local.includes('outlaw_identity_choice'))
  throw new Error('Outlaw identity-choice fast path must specifically recognize profile-card choices and log them');
if (!quality.includes('TryAutoSelectOutlawIdentityChoice(candidates, systemContext, generation)'))
  throw new Error('Outlaw must run identity auto-selection before screenshot/cloud planning');

if (!local.includes('TryAutoSelectOutlawVisibleChoice'))
  throw new Error('Outlaw low-risk visible choices must be auto-selected instead of asked back to the user');
if (!local.includes('outlaw_visible_choice_auto_selected'))
  throw new Error('Outlaw low-risk visible auto-selection must be logged');
if (!quality.includes('TryPresentOutlawVisibleChoiceButtons(candidates, systemContext, generation)'))
  throw new Error('Outlaw must retain explicit choice UI as a fallback for high-impact branches');
