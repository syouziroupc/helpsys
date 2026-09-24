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

if (!policy.includes('if (TryQueueCurrentStateReplan(reason, generation)) return;'))
  throw new Error('technical uncertainty must receive one bounded refresh first');
if (!policy.includes('"automatic_replan_exhausted"') || !policy.includes('LocalLogService.Write("replan_stop", reason)'))
  throw new Error('exhausted same-state refresh must stop explicitly instead of looping');
if (policy.includes('QueueResilientRecovery(reason, generation)'))
  throw new Error('technical uncertainty must not enter a second same-state recovery loop after the one refresh');
if (policy.includes('WaitForClarification('))
  throw new Error('technical observer uncertainty must not become a user clarification question');

if (!quality.includes('planningCts.CancelAfter(TimeSpan.FromSeconds(35))'))
  throw new Error('one interactive planning attempt must have a bounded 35 second hard deadline');
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
  if (!block.includes('if (!OutlawModePolicy.Enabled)'))
    throw new Error('Outlaw presentation must not reset same-state loop budgets merely because a target was shown');
}

console.log('HelpSys single-refresh current-state contract passed.');
