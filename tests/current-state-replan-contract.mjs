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

if (!quality.includes('planningCts.CancelAfter(TimeSpan.FromSeconds(75))'))
  throw new Error('one interactive planning attempt must have a bounded 75 second hard deadline');
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

const captureService = fs.readFileSync('src/HelpSys.Desktop/ScreenCaptureService.cs', 'utf8');
const scanner = fs.readFileSync('src/HelpSys.Desktop/Services/UiAutomationScanner.cs', 'utf8');
const cloud = fs.readFileSync('src/HelpSys.Desktop/Services/CloudGuideService.cs', 'utf8');
const xaml = fs.readFileSync('src/HelpSys.Desktop/MainWindow.xaml', 'utf8');
if (!captureService.includes('SmCxVirtualScreen') || !captureService.includes('GetSystemMetrics(SmCxVirtualScreen)'))
  throw new Error('Outlaw shell capture must cover the full Windows virtual desktop across monitors');
if (!scanner.includes('IntersectsVirtualDesktop(rect)'))
  throw new Error('Outlaw UIA candidates must exclude minimized/off-virtual-desktop controls');
if (!cloud.includes('TimeSpan.FromSeconds(60)') || !cloud.includes('outlaw_api_timeout') || !cloud.includes('outlaw_api_error'))
  throw new Error('Outlaw one-shot planner must allow long reasoning and persist exact API failures');
if (!xaml.includes('AI: Gemini (未設定)') || !xaml.includes('Tag="gemini" IsEnabled="False"'))
  throw new Error('Pilot UI must not expose the unconfigured Gemini provider as selectable');

const fastPath = fs.readFileSync('src/HelpSys.Desktop/MainWindow.OutlawFastPath.cs', 'utf8');
if (!quality.includes('outlaw_phase_timing'))
  throw new Error('Outlaw must log phase timing for observation/screenshot/planner/presentation');
if (!quality.includes('TryOutlawBrowserSearchFastPath(candidates, systemContext, generation)'))
  throw new Error('Outlaw must try the local browser-search fast path before screenshot/cloud planning');
if (!fastPath.includes('ResolveBeginnerWebSearchText') || !fastPath.includes('outlaw_local_browser_search'))
  throw new Error('Outlaw browser-search fast path must use simple service names and log its local decision');
if (!fastPath.includes('x.Y >= 220') || !fastPath.includes('x.Width >= 220'))
  throw new Error('Outlaw browser-search fast path must prefer a large visible page search field over the top omnibox');

const mainXaml = fs.readFileSync('src/HelpSys.Desktop/MainWindow.xaml', 'utf8');
const mainCode = fs.readFileSync('src/HelpSys.Desktop/MainWindow.xaml.cs', 'utf8');
const observationBroker = fs.readFileSync('src/HelpSys.Desktop/Services/ObservationBroker.cs', 'utf8');

if (!mainXaml.includes('x:Name="VersionLabel"') || mainXaml.includes('Outlaw 3.1.0'))
  throw new Error('Outlaw UI version must not be hardcoded to 3.1.0');
if (!mainCode.includes('Assembly.GetName().Version') && !mainCode.includes('typeof(MainWindow).Assembly.GetName().Version'))
  throw new Error('Outlaw UI must derive its visible version from executable metadata');
if (!fastPath.includes('query_already_submitted'))
  throw new Error('Outlaw local browser-search fast path must not submit the same query twice');
if (!observationBroker.includes('outlaw_background_uia_filtered') ||
    !observationBroker.includes('KeepForegroundProcessEvidence'))
  throw new Error('Outlaw observation must remove background-process UIA before planning');
if (!observationBroker.includes('outlaw_observation_rebound') ||
    !observationBroker.includes('observation.rescan'))
  throw new Error('Outlaw must rescan once when the same process recreates its foreground HWND');
