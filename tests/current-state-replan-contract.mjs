import fs from 'node:fs';

const quality = fs.readFileSync('src/HelpSys.Desktop/MainWindow.QualityFirst.cs', 'utf8');
const main = fs.readFileSync('src/HelpSys.Desktop/MainWindow.xaml.cs', 'utf8');
const helper = fs.readFileSync('src/HelpSys.Desktop/MainWindow.CurrentStateReplan.cs', 'utf8');
const policy = fs.readFileSync('src/HelpSys.Desktop/MainWindow.RecoveryPolicy.cs', 'utf8');

if (!helper.includes('MaximumAutomaticCurrentStateReplans = 1'))
  throw new Error('same observed state must receive at most one automatic fresh observation');
if (!helper.includes('await Task.Delay(180, _sessionCts.Token)'))
  throw new Error('single refresh must keep one short quiet-period delay');
if (!helper.includes('_liveReplanPending = true') || !helper.includes('TryRunPendingLiveReplanAsync()'))
  throw new Error('current-state refresh must use the established live replan pipeline');
if (helper.includes('await AdvanceGuideAsync()'))
  throw new Error('current-state helper must not recursively invoke the planner');

if (!policy.includes('if (!OutlawModePolicy.Enabled && TryQueueCurrentStateReplan(reason, generation)) return;'))
  throw new Error('Outlaw technical uncertainty must not re-run the planner on the same observation');
if (!policy.includes('"one_shot_planning_failed"') || !policy.includes('"outlaw_one_shot_failed"'))
  throw new Error('Outlaw one-shot planning failure must be logged explicitly without same-screen retry');
if (policy.includes('QueueResilientRecovery(reason, generation)'))
  throw new Error('technical uncertainty must not enter a second same-state recovery loop after the one refresh');
if (policy.includes('WaitForClarification('))
  throw new Error('technical observer uncertainty must not become a user clarification question');

if (!quality.includes('planningCts.CancelAfter(TimeSpan.FromSeconds(42))'))
  throw new Error('one interactive planning attempt must have a bounded 42 second hard deadline');
if (quality.includes('planningCts.CancelAfter(TimeSpan.FromSeconds(180))'))
  throw new Error('legacy 180 second interactive planning window must stay removed');

for (const marker of [
  'phase=after_screenshot;discarding planner observation because foreground identity changed',
  'phase=after_planner;discarding planner result because foreground identity changed'
]) {
  if (!quality.includes(marker))
    throw new Error(`freshness gate missing: ${marker}`);
}

const structuredStart = main.indexOf('private void ShowStructuredTarget(');
const keyboardStart = main.indexOf('private void ShowKeyboardGuide(');
const visionFallbackStart = main.indexOf('private async Task<bool> TryVisionFallbackAsync', keyboardStart);
const structuredBlock = main.slice(structuredStart, keyboardStart);
const keyboardBlock = main.slice(keyboardStart, visionFallbackStart);
for (const block of [structuredBlock, keyboardBlock]) {
  if (!block.includes('ResetCurrentStateReplanBudget()') || !block.includes('ResetResilienceRecovery()'))
    throw new Error('a successfully presented target must clear stale failure state for every edition');
}

console.log('HelpSys single-refresh current-state contract passed.');

if (!quality.includes('_observationBroker.CaptureAsync(4000, cancellationToken)'))
  throw new Error('Outlaw one-shot observation must provide up to 4000 current UIA candidates');
if (!quality.includes('outlaw_visual_capture_changed_ignored') || !quality.includes('outlaw_post_capture_change_ignored'))
  throw new Error('Outlaw must not discard a first-pass decision for soft same-window WinEvent churn');
if (!quality.includes('outlaw_target_revalidation_fallback'))
  throw new Error('Outlaw must retain immutable first-observation target bounds when same-window revalidation is transiently unavailable');
