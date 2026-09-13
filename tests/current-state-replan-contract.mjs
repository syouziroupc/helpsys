import fs from 'node:fs';

const quality = fs.readFileSync('src/HelpSys.Desktop/MainWindow.QualityFirst.cs', 'utf8');
const main = fs.readFileSync('src/HelpSys.Desktop/MainWindow.xaml.cs', 'utf8');
const helper = fs.readFileSync('src/HelpSys.Desktop/MainWindow.CurrentStateReplan.cs', 'utf8');
const policy = fs.readFileSync('src/HelpSys.Desktop/MainWindow.RecoveryPolicy.cs', 'utf8');

if (!helper.includes('using HelpSys.Models;') || !helper.includes('using HelpSys.Services;'))
  throw new Error('current-state replan partial must import model and session-state namespaces');
if (!helper.includes('MaximumAutomaticCurrentStateReplans = 1'))
  throw new Error('automatic current-state replan must be strictly bounded to one attempt');
if (!helper.includes('_liveReplanPending = true') || !helper.includes('TryRunPendingLiveReplanAsync()'))
  throw new Error('current-state replan must use the established live replan pipeline');
if (helper.includes('await AdvanceGuideAsync()'))
  throw new Error('current-state replan helper must not recursively start the planner directly');
if (!policy.includes('TryQueueCurrentStateReplan(reason, generation)'))
  throw new Error('technical uncertainty must first use bounded current-state replan');
if (!policy.includes('WaitForClarification('))
  throw new Error('technical uncertainty must fall back to bounded technical retry/stop, not route recovery');

for (const reason of [
  '再確認しても前面ウィンドウを特定できない',
  '確認中に画面切替が続いている',
  '判断中の画面変化が続いている',
  '選ばれた対象を現在画面で操作できない',
  '案内表示直前の画面変化が続いている',
  '案内対象を表示直前に再確認できない',
  '画像候補確認中の画面変化が続いている'
]) {
  if (!quality.includes(`HandleTechnicalPlanningUncertainty("${reason}"`))
    throw new Error(`technical UI uncertainty must use current-state policy: ${reason}`);
}

const structuredStart = main.indexOf('private void ShowStructuredTarget(');
const keyboardStart = main.indexOf('private void ShowKeyboardGuide(');
const visionFallbackStart = main.indexOf('private async Task<bool> TryVisionFallbackAsync', keyboardStart);
const structuredBlock = main.slice(structuredStart, keyboardStart);
const keyboardBlock = main.slice(keyboardStart, visionFallbackStart);
if (!structuredBlock.includes('ResetCurrentStateReplanBudget();'))
  throw new Error('successful structured target guidance must reset the replan budget');
if (!keyboardBlock.includes('ResetCurrentStateReplanBudget();'))
  throw new Error('successful keyboard guidance must reset the replan budget');
if (!quality.includes('ResetCurrentStateReplanBudget();'))
  throw new Error('successful visual target guidance must reset the replan budget');

console.log('HelpSys bounded current-state replan contract passed.');
