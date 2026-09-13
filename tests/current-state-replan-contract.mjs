import fs from 'node:fs';

const quality = fs.readFileSync('src/HelpSys.Desktop/MainWindow.QualityFirst.cs', 'utf8');
const main = fs.readFileSync('src/HelpSys.Desktop/MainWindow.xaml.cs', 'utf8');
const helper = fs.readFileSync('src/HelpSys.Desktop/MainWindow.CurrentStateReplan.cs', 'utf8');

if (!helper.includes('using HelpSys.Models;') || !helper.includes('using HelpSys.Services;'))
  throw new Error('current-state replan partial must import model and session-state namespaces');

if (!helper.includes('MaximumAutomaticCurrentStateReplans = 1'))
  throw new Error('automatic current-state replan must be strictly bounded to one attempt');
if (!helper.includes('_liveReplanPending = true'))
  throw new Error('current-state replan must use the established live replan pipeline');
if (!helper.includes('TryRunPendingLiveReplanAsync()'))
  throw new Error('current-state replan must delegate execution to the live replan owner');
if (helper.includes('await AdvanceGuideAsync()'))
  throw new Error('current-state replan helper must not recursively start the planner directly');

for (const reason of [
  '前面ウィンドウを一時的に特定できない',
  '確認中に画面が切り替わった',
  '判断中に画面が変化した',
  '選ばれた対象が現在は操作できない',
  '案内表示の直前に画面が変わった',
  '案内対象が表示直前に消えた',
  '画像上の候補を確認中に画面が変わった'
]) {
  if (!quality.includes(`TryQueueCurrentStateReplan("${reason}"`))
    throw new Error(`transient UI drift must replan before recovery: ${reason}`);
}

if (!quality.includes('error.Kind == GuideFailureKind.ContextChanged'))
  throw new Error('only context-change service failures should enter the lightweight replan path');
if (!quality.includes('再確認しても前面ウィンドウを特定できない') ||
    !quality.includes('再確認しても案内対象を確定できない'))
  throw new Error('heavy route recovery must remain as a bounded fallback after replan failure');
const structuredStart = main.indexOf('private void ShowStructuredTarget(');
const keyboardStart = main.indexOf('private void ShowKeyboardGuide(');
const visionFallbackStart = main.indexOf('private async Task<bool> TryVisionFallbackAsync', keyboardStart);
const structuredBlock = main.slice(structuredStart, keyboardStart);
const keyboardBlock = main.slice(keyboardStart, visionFallbackStart);
if (!structuredBlock.includes('ResetCurrentStateReplanBudget();'))
  throw new Error('successful structured target guidance must reset the replan budget');
if (!keyboardBlock.includes('ResetCurrentStateReplanBudget();'))
  throw new Error('successful keyboard guidance must reset the replan budget');
if (!main.includes('_technicalClarificationRetries = 0;\n        ResetCurrentStateReplanBudget();\n        if (generation.HasValue)') &&
    !main.includes('_technicalClarificationRetries = 0;\r\n        ResetCurrentStateReplanBudget();\r\n        if (generation.HasValue)'))
  throw new Error('genuine clarification must reset the replan budget');
if (!quality.includes('ResetCurrentStateReplanBudget();'))
  throw new Error('successful visual target guidance must reset the replan budget');

console.log('HelpSys bounded current-state replan contract passed.');
