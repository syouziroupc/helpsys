import fs from 'node:fs';

const main = fs.readFileSync('src/HelpSys.Desktop/MainWindow.xaml.cs', 'utf8');
const live = fs.readFileSync('src/HelpSys.Desktop/MainWindow.LiveGuidance.cs', 'utf8');
const local = fs.readFileSync('src/HelpSys.Desktop/MainWindow.LocalChoiceResolver.cs', 'utf8');
const reliability = fs.readFileSync('src/HelpSys.Desktop/MainWindow.ReliabilityV3.cs', 'utf8');

const legacyHook = main.indexOf('TryHandleLocalAccountChoiceAnswerAsync(text)');
const legacyHistory = main.indexOf('new GuideHistoryItem(_stepNumber, "clarification_answer", text');
if (legacyHook < 0 || legacyHistory < 0 || legacyHook > legacyHistory)
  throw new Error('legacy clarification path must intercept the raw account answer before history/cloud handling');

const liveHook = live.indexOf('TryHandleLocalAccountChoiceAnswerAsync(answer)');
const liveHistory = live.indexOf('new GuideHistoryItem(_stepNumber, "clarification_answer", answer');
if (liveHook < 0 || liveHistory < 0 || liveHook > liveHistory)
  throw new Error('AnswerBox clarification path must intercept the raw account answer before history/cloud handling');

if (!local.includes('FindUniqueLocalAccountChoice'))
  throw new Error('local account resolver must require a unique local UIA match');
if (!local.includes('RevalidateCandidateAsync(match, context.ForegroundProcessId'))
  throw new Error('local account target must be revalidated before guidance');
if (!local.includes('利用者が選んだアカウント'))
  throw new Error('local account history must use an opaque label');
if (local.includes('_activeRequest +=') || local.includes('clarification_answer", answer'))
  throw new Error('local resolver must not append the raw account answer to the cloud-bound request/history');
if (!local.includes('HideClarificationUiIfNeeded(force: true)'))
  throw new Error('successful local choice must close the clarification panel before target guidance');
const retryWait = local.indexOf('WaitForClarification(question);');
const retryUi = local.indexOf('EnsureClarificationUi();', retryWait);
if (retryWait < 0 || retryUi < 0 || retryUi < retryWait)
  throw new Error('failed local matching must keep the clarification UI available for retry');
if (!reliability.includes('_localChoiceTargetActive') || !reliability.includes('利用者が選んだアカウント'))
  throw new Error('successful local account selection must keep later history opaque');


if (!local.includes('FindUniqueContainingLocalChoice(candidates, interactable, normalizedAnswer'))
  throw new Error('local resolver must support context-text to containing account-card mapping');
if (!local.includes('x.Bounds.Contains(center)'))
  throw new Error('context identity mapping must require geometric containment, not nearest-neighbour guessing');
if (!local.includes('mapped.Count == 1 ? mapped.Values.Single() : null'))
  throw new Error('context identity mapping must fail closed unless exactly one clickable target remains');
if (!local.includes('!x.Interactable && x.Enabled') || !local.includes('context.ProcessId <= 0 || x.ProcessId <= 0 || x.ProcessId == context.ProcessId'))
  throw new Error('context identity mapping must stay local to visible context and the same UIA process when known');

console.log('HelpSys local account-choice privacy contract passed.');
